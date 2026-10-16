using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
namespace RoundTableMeeting.RealtimeChat;

public sealed class LocalAttachmentStore : IAttachmentStore
{
    private readonly string root;
    private readonly long maximum;
    private readonly ConcurrentDictionary<string, byte> active = new();
    public LocalAttachmentStore(IOptions<AttachmentOptions> options, IWebHostEnvironment environment)
    {
        maximum = options.Value.MaxFileBytes;
        root = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (root.Equals(webRoot, StringComparison.OrdinalIgnoreCase) || root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attachment storage must be outside the public web root.");
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, Guid.NewGuid().ToString("N") + ".probe");
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch { throw new InvalidOperationException("Attachment storage is unavailable."); }
    }
    private string DirectoryFor(Guid meetingId) => Path.Combine(root, meetingId.ToString("N"));
    private string PathFor(Guid meetingId, string key)
    {
        if (key.Length != 32 || !Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid storage key.");
        return Path.Combine(DirectoryFor(meetingId), key);
    }
    public async Task<StagedAttachmentFile> WriteAsync(Guid meetingId, Stream source, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N"); var destination = PathFor(meetingId, key); var temporary = destination + ".upload";
        active.TryAdd(destination, 0);
        try
        {
            Directory.CreateDirectory(DirectoryFor(meetingId));
            long total = 0; var prefix = new byte[16]; var prefixLength = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct); if (read == 0) break;
                    if (read > maximum - total) throw new AttachmentOperationException("attachment_too_large", 413, maximum);
                    var take = Math.Min(prefix.Length - prefixLength, read);
                    buffer.AsSpan(0, take).CopyTo(prefix.AsSpan(prefixLength)); prefixLength += take;
                    total += read; hash.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await file.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            return new(key, total, Convert.ToHexString(hash.GetHashAndReset()), DetectMediaType(prefix.AsSpan(0, prefixLength)));
        }
        catch { File.Delete(temporary); File.Delete(destination); throw; }
        finally { active.TryRemove(destination, out _); }
    }
    private static string DetectMediaType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (data.StartsWith("%PDF-"u8)) return "application/pdf";
        return "application/octet-stream";
    }
    public Stream Open(Guid meetingId, string key) => new FileStream(PathFor(meetingId, key), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
    public void Delete(Guid meetingId, string key) => File.Delete(PathFor(meetingId, key));
    public Task DeleteMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var directory = DirectoryFor(meetingId);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Task.CompletedTask;
    }
    public IReadOnlyList<OrphanAttachmentFile> ListOldFiles(DateTimeOffset before)
    {
        var result = new List<OrphanAttachmentFile>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var meeting)) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var key = Path.GetFileName(file);
                if (key.EndsWith(".upload", StringComparison.Ordinal)) key = key[..^7];
                if (!Guid.TryParseExact(key, "N", out _) || active.ContainsKey(PathFor(meeting, key))) continue;
                if (File.GetLastWriteTimeUtc(file) >= before.UtcDateTime) continue;
                if (file.EndsWith(".upload", StringComparison.Ordinal)) File.Delete(file);
                else result.Add(new(meeting, key));
            }
        }
        return result;
    }
}
