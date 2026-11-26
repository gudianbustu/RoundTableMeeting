using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RoundTableMeeting.Avatars;

public sealed partial class PublicAvatarLibrary
{
    internal async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        if (Linked(Root)) throw new IOException("头像库目录不能为链接");
        using var lease = await AcquireAsync(CancellationToken.None);
        var seed = Path.Combine(AppContext.BaseDirectory, "AvatarSeed");
        var ledger = Path.Combine(Root, ".installed.json");
        var installed = File.Exists(ledger)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllBytes(ledger))! : [];
        var groups = Ordered(Root, true);
        using var db = new SqliteConnection(connection);
        db.Open();
        using (var transaction = db.BeginTransaction())
        {
            foreach (var group in Ordered(seed, true))
            {
                var source = Path.Combine(seed, group);
                foreach (var file in Ordered(source, false))
                {
                    var key = group + "/" + file;
                    if (!installed.Add(key)) continue;
                    // A removed group stays retired when a later package adds more files to it.
                    if (Directory.Exists(Path.Combine(Root, Removed + group))) continue;
                    var dest = EnsureGroup(group, groups);
                    var path = Path.Combine(dest, file);
                    if (!File.Exists(path))
                    {
                        RequireAvailable(dest, file);
                        var order = Ordered(dest, false);
                        Atomic(path, File.ReadAllBytes(Path.Combine(source, file)));
                        order.Add(file);
                        Save(Path.Combine(dest, "index.json"), order);
                    }
                }
            }
            // Old built-in identifiers only participate in the upgrade.
            if (!File.Exists(ledger))
            {
                EnsureGroup("自定义", groups);
                foreach (var old in BuiltinAvatars.All)
                {
                    var group = old.Category switch { "tool" => "工具", "character" => "Q 版伙伴", "ai-girl" => "AI娘", _ => "模型" };
                    Rewrite(db, transaction, old.ImageUrl, Url(group, old.Name + ".png"), false);
                }
            }
            transaction.Commit();
        }
        Save(Path.Combine(Root, "index.json"), groups);
        Save(ledger, installed);
        ImportUploads(db);
    }

    private string EnsureGroup(string group, List<string> groups)
    {
        var dir = Path.Combine(Root, group);
        if (!groups.Contains(group))
        {
            RequireAvailable(Root, group);
            Directory.CreateDirectory(dir);
            groups.Add(group);
        }
        return Group(group);
    }

    private void ImportUploads(SqliteConnection db)
    {
        // Commit one export at a time. A pending filename makes retry after a crash deterministic.
        var pending = Path.Combine(Root, ".import.json");
        while (true)
        {
            using var transaction = db.BeginTransaction();
            using var query = db.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT Id, Name, Png FROM AvatarAssets WHERE OwnerId IS NULL ORDER BY Name, Id LIMIT 1";
            string id, name;
            byte[] png;
            using (var row = query.ExecuteReader())
            {
                if (!row.Read()) { File.Delete(pending); return; }
                id = row.GetString(0); name = row.GetString(1); png = (byte[])row[2];
            }
            var groups = Ordered(Root, true);
            var dest = EnsureGroup("自定义", groups);
            var previous = File.Exists(pending) ? JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(pending)) : null;
            string file;
            if (previous is { Length: 2 } && previous[0] == id) file = Checked(previous[1]);
            else
            {
                var stem = Valid(name) && name.Length <= 100 ? name : "头像";
                file = stem + ".png";
                for (int suffix = 2; ; suffix++)
                {
                    try { RequireAvailable(dest, file); break; }
                    catch (LibraryError) { file = $"{stem} ({suffix}).png"; }
                }
                Save(pending, new[] { id, file });
            }
            var order = Ordered(dest, false);
            Atomic(Path.Combine(dest, file), png);
            if (!order.Contains(file)) order.Add(file);
            Save(Path.Combine(dest, "index.json"), order);
            Save(Path.Combine(Root, "index.json"), groups);
            Rewrite(db, transaction, $"/api/avatars/{id}/image", Url("自定义", file), false);
            using var remove = db.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM AvatarAssets WHERE Id=$id AND OwnerId IS NULL";
            remove.Parameters.AddWithValue("$id", id);
            remove.ExecuteNonQuery();
            transaction.Commit();
            File.Delete(pending);
        }
    }

    private void FinishRename()
    {
        var journal = Path.Combine(Root, ".rename.json");
        if (!File.Exists(journal)) return;
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(journal))!;
        var old = Checked(names[0]);
        var next = Checked(names[1]);
        var source = Path.Combine(Root, old);
        var dest = Path.Combine(Root, next);
        if (Directory.Exists(source)) Directory.Move(source, dest);
        if (!Directory.Exists(dest)) throw new IOException("分组改名未完成");
        using var db = new SqliteConnection(connection);
        db.Open();
        using var transaction = db.BeginTransaction();
        Rewrite(db, transaction, Url(old, ""), Url(next, ""), true);
        var order = ReadOrder(Root).Select(g => g == old ? next : g).ToList();
        Save(Path.Combine(Root, "index.json"), order);
        transaction.Commit();
        File.Delete(journal);
    }

    private static void Rewrite(SqliteConnection db, SqliteTransaction tx, string old, string next, bool prefix)
    {
        // Update color slots first, before the selection trigger observes the new URL.
        foreach (var table in new[] { "AvatarColorAssignments", "AvatarInvitations", "AvatarSelections" })
        {
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = prefix
                ? $"UPDATE {table} SET Url=$next || substr(Url,length($old)+1) WHERE substr(Url,1,length($old))=$old"
                : $"UPDATE {table} SET Url=$next WHERE Url=$old";
            update.Parameters.AddWithValue("$old", old);
            update.Parameters.AddWithValue("$next", next);
            update.ExecuteNonQuery();
        }
    }
}
