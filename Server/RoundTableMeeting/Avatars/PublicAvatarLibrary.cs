using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Configuration;

namespace RoundTableMeeting.Avatars;

public sealed record AvatarGroup(string Name, IReadOnlyList<AvatarEntry> Items);
public sealed record LibraryEdit(string Action, string? Group, string? Name, int Direction = 0, string? Target = null, bool After = false);
internal sealed class LibraryError(string message, int status = 400) : Exception(message)
{
    internal int Status { get; } = status;
}

public sealed partial class PublicAvatarLibrary(IOptions<SqliteDatabaseOptions> database)
{
    private readonly string connection = SqlitePathResolver.NormalizeConnectionString(database.Value.ConnectionString);
    private string Root => Path.Combine(SqlitePathResolver.GetDatabaseDirectory(connection), "Avatars");
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string Removed = ".removed-";
    private static readonly EnumerationOptions Enumeration = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };

    internal async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        FileStream? handle = null;
        try
        {
            // Hosts sharing the same data directory must serialize catalogue and reference changes.
            var timer = Stopwatch.StartNew();
            while (handle is null)
            {
                try { handle = File.Open(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(40, ct); }
            }
            FinishRename();
            return new Lease(gate, handle);
        }
        catch
        {
            handle?.Dispose();
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate, FileStream handle) : IDisposable
    {
        public void Dispose()
        {
            handle.Dispose();
            gate.Release();
        }
    }

    private static bool Valid(string? name) => name is { Length: > 0 and <= 180 }
        && name == name.Trim() && !name.StartsWith('.') && !name.EndsWith('.')
        && !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*%#".Contains(c))
        && !Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)
        && !name.Equals("index.json", StringComparison.OrdinalIgnoreCase);
    private static string Checked(string? name) => Valid(name) ? name! : throw new LibraryError("名称无效");
    private static string? Media(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".gif" => "image/gif", ".avif" => "image/avif", _ => null
    };
    private static bool Linked(string path) => (new FileInfo(path).LinkTarget != null);
    private static string Url(string group, string file) => "/Avatars/" + Uri.EscapeDataString(group) + "/" + Uri.EscapeDataString(file);
    private static AvatarEntry Entry(string group, string file) =>
        new(Url(group, file), Path.GetFileNameWithoutExtension(file), Url(group, file), group);

    private static void Atomic(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
    private static void Save<T>(string path, T value) => Atomic(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    private static List<string> ReadOrder(string dir)
    {
        var path = Path.Combine(dir, "index.json");
        if (Linked(path)) throw new IOException("排序文件不能为链接");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(path)) ?? [] : [];
    }
    private static List<string> Ordered(string dir, bool groups)
    {
        var paths = groups ? Directory.EnumerateDirectories(dir, "*", Enumeration) : Directory.EnumerateFiles(dir, "*", Enumeration);
        var names = paths.Select(p => Path.GetFileName(p)).Where(n => Valid(n) && (groups || Media(n) != null)).ToHashSet(StringComparer.Ordinal);
        var sorted = ReadOrder(dir).Where(names.Remove).ToList();
        sorted.AddRange(names.Order(StringComparer.Ordinal));
        return sorted;
    }
    private string Group(string? name)
    {
        var dir = Path.Combine(Root, Checked(name));
        if (!Directory.Exists(dir) || Linked(dir)) throw new LibraryError("分组不存在", 404);
        return dir;
    }
    internal List<AvatarGroup> Catalog() => Ordered(Root, true).Select(g =>
        new AvatarGroup(g, Ordered(Group(g), false).Select(f => Entry(g, f)).ToArray())).ToList();

    internal AvatarEntry? Find(string id) => Catalog().SelectMany(g => g.Items).FirstOrDefault(a => a.Id == id);

    internal (FileStream Stream, string Type)? Open(string group, string file)
    {
        if (!Valid(group) || !Valid(file) || Media(file) is not string type) return null;
        foreach (var folder in new[] { group, Removed + group })
        {
            var dir = Path.Combine(Root, folder);
            if (Linked(dir)) continue;
            foreach (var name in new[] { file, Removed + file })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && !Linked(path))
                    return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), type);
            }
        }
        return null;
    }

    private static void RequireAvailable(string dir, string name, string? except = null)
    {
        if (Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n != except).Any(existing =>
            string.Equals(existing, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existing, Removed + name, StringComparison.OrdinalIgnoreCase)))
            throw new LibraryError("名称已被使用", 409);
    }

    internal AvatarEntry Add(string group, string name, byte[] png)
    {
        var dir = Group(group);
        var file = Checked(Checked(name) + ".png");
        RequireAvailable(dir, file);
        var order = Ordered(dir, false);
        Atomic(Path.Combine(dir, file), png);
        order.Add(file);
        Save(Path.Combine(dir, "index.json"), order);
        return Entry(group, file);
    }

    internal void Edit(LibraryEdit edit)
    {
        var group = Checked(edit.Group);
        if (edit.Action == "createGroup")
        {
            RequireAvailable(Root, group);
            var groups = Ordered(Root, true);
            var dir = Directory.CreateDirectory(Path.Combine(Root, group));
            Save(Path.Combine(dir.FullName, "index.json"), Array.Empty<string>());
            groups.Add(group);
            Save(Path.Combine(Root, "index.json"), groups);
            return;
        }
        var current = Group(group);
        var groupAction = edit.Action is "renameGroup" or "deleteGroup" or "moveGroup" or "reorderGroup";
        var dirToOrder = groupAction ? Root : current;
        var order = Ordered(dirToOrder, groupAction);
        var name = groupAction ? group : Checked(edit.Name);
        var index = order.IndexOf(name);
        if (index < 0) throw new LibraryError("项目不存在", 404);
        switch (edit.Action)
        {
            case "renameGroup":
            case "renameAvatar":
                var replacement = groupAction ? Checked(edit.Name) : Checked(Checked(edit.Target) + Path.GetExtension(name));
                if (replacement == name) return;
                RequireAvailable(dirToOrder, replacement, name);
                // Record the order even for files added directly on disk before moving them.
                Save(Path.Combine(dirToOrder, "index.json"), order);
                Save(Path.Combine(Root, ".rename.json"), groupAction
                    ? new[] { name, replacement } : new[] { name, replacement, group });
                FinishRename();
                return;
            case "reorderGroup":
            case "reorderAvatar":
                var anchor = Checked(edit.Target);
                if (!order.Contains(anchor)) throw new LibraryError("排序目标已变更，请刷新后重试", 409);
                if (anchor == name) return;
                order.RemoveAt(index);
                order.Insert(order.IndexOf(anchor) + (edit.After ? 1 : 0), name);
                break;
            case "deleteGroup":
                Directory.Move(current, Path.Combine(Root, Removed + group));
                order.RemoveAt(index);
                break;
            case "deleteAvatar":
                File.Move(Path.Combine(current, name), Path.Combine(current, Removed + name));
                order.RemoveAt(index);
                break;
            case "moveGroup":
            case "moveAvatar":
                if (edit.Direction is not (-1 or 1)) throw new LibraryError("排序方向无效");
                var to = Math.Clamp(index + edit.Direction, 0, order.Count - 1);
                (order[index], order[to]) = (order[to], order[index]);
                break;
            default: throw new LibraryError("操作无效");
        }
        Save(Path.Combine(dirToOrder, "index.json"), order);
    }
}
