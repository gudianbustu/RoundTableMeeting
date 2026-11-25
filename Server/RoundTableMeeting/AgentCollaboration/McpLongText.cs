using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.AgentCollaboration;

public sealed record TextPartReceipt(Guid DraftId, int Index, int PartCount);
public sealed record MessageTextPage(string Text, long? NextByteOffset, long TotalBytes);

public sealed class McpLongText(IAttachmentService files, IAttachmentStore store, IMeetingAttachmentRepository repository,
    RealtimeChatOptions chat, IOptions<AttachmentOptions> limits, IMessageLookupService messages, IMessageRepository sent)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Manifest(int PartCount, long TotalBytes);

    private static Guid Key(Guid id, string role) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"roundtable-text-v1/{id:N}/{role}")).AsSpan(0, 16));

    public async Task<TextPartReceipt> Stage(AgentConnectionSession connection, Guid draftId, int index, int partCount,
        long totalBytes, string content, CancellationToken ct)
    {
        if (draftId == Guid.Empty || partCount is < 1 or > 8192 || index < 0 || index >= partCount ||
            totalBytes < partCount || totalBytes > limits.Value.MaxFileBytes || totalBytes > partCount * 262144L)
            throw new McpOperationException("invalid_text_part");
        if (await sent.FindBySubmissionAsync(connection.MeetingId, connection.ParticipantId, draftId, ct) is not null)
            throw new McpOperationException("text_draft_published");
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length is < 1 or > 262144 || bytes.Length > totalBytes) throw new McpOperationException("invalid_text_part");
        var descriptor = new Manifest(partCount, totalBytes);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        using var metadata = new MemoryStream(manifestBytes, false);
        await files.UploadAsync(connection.MeetingId, Key(draftId, "manifest"), "draft.json", metadata, ct);
        using var part = new MemoryStream(bytes, false);
        await files.UploadAsync(connection.MeetingId, Key(draftId, $"part/{index}"), "part.txt", part, ct);
        return new(draftId, index, partCount);
    }

    private async Task<MeetingAttachment> Piece(AgentConnectionSession c, Guid draftId, string role, CancellationToken ct) =>
        await repository.FindUploadAsync(c.MeetingId, c.ParticipantId, Key(draftId, role), ct)
        ?? throw new McpOperationException("text_draft_incomplete");

    public async Task<SubmitMessageRequest> Prepare(AgentConnectionSession c, SubmitMessageRequest request, Guid? draftId, CancellationToken ct)
    {
        if (draftId is not Guid draft)
        {
            if (Utf8.GetByteCount(request.Content) <= chat.MaxContentLength) return request;
            using var input = new MemoryStream(Utf8.GetBytes(request.Content), false);
            return await Attach(c, request, input, ct);
        }
        request = request with { SourceTextDraftId = draft };
        // A committed message outlives temporary parts. The append service verifies the full destination and draft fingerprint.
        var published = await sent.FindBySubmissionAsync(c.MeetingId, c.ParticipantId, request.ClientSubmissionId, ct);
        if (published is not null)
        {
            return request with { Content = published.Content, OriginalTextAttachmentId = published.OriginalTextAttachmentId,
                AttachmentIds = published.OriginalTextAttachmentId is Guid id ? [.. request.AttachmentIds ?? [], id] : request.AttachmentIds };
        }
        var header = await Piece(c, draft, "manifest", ct);
        await using var source = store.Open(c.MeetingId, header.StorageKey);
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(source, cancellationToken: ct)
            ?? throw new McpOperationException("invalid_text_manifest");
        if (manifest.PartCount is < 1 or > 8192 || manifest.TotalBytes < 1 || manifest.TotalBytes > limits.Value.MaxFileBytes)
            throw new McpOperationException("invalid_text_manifest");
        await using var assembled = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        for (var index = 0; index < manifest.PartCount; index++)
        {
            var entry = await Piece(c, draft, $"part/{index}", ct);
            if (assembled.Length + entry.SizeBytes > manifest.TotalBytes) throw new McpOperationException("text_draft_size_mismatch");
            await using var piece = store.Open(c.MeetingId, entry.StorageKey);
            await piece.CopyToAsync(assembled, ct);
        }
        if (assembled.Length != manifest.TotalBytes) throw new McpOperationException("text_draft_size_mismatch");
        assembled.Position = 0;
        if (assembled.Length > chat.MaxContentLength) return await Attach(c, request, assembled, ct);
        using var reader = new StreamReader(assembled, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return request with { Content = await reader.ReadToEndAsync(ct) };
    }

    private async Task<SubmitMessageRequest> Attach(AgentConnectionSession c, SubmitMessageRequest request, Stream text, CancellationToken ct)
    {
        var prefix = new byte[Math.Min(192, chat.MaxContentLength)];
        await text.ReadExactlyAsync(prefix, ct);
        var preview = Prefix(prefix, out _);
        text.Position = 0;
        var attachment = await files.UploadAsync(c.MeetingId, Key(request.ClientSubmissionId, "original"), "消息全文.txt", text, ct);
        return request with { Content = preview, AttachmentIds = [.. request.AttachmentIds ?? [], attachment.AttachmentId],
            OriginalTextAttachmentId = attachment.AttachmentId };
    }

    private static string Prefix(byte[] bytes, out int consumed)
    {
        consumed = 0;
        while (consumed < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(consumed), out _, out var length);
            if (status == OperationStatus.NeedMoreData) break;
            if (status != OperationStatus.Done) throw new McpOperationException("invalid_utf8_offset");
            consumed += length;
        }
        return Utf8.GetString(bytes, 0, consumed);
    }

    public async Task<MessageTextPage> Read(AgentConnectionSession c, Guid messageId, long offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 4 or > 65536) throw new McpOperationException("invalid_text_range");
        var message = (await messages.GetMessageAsync(c.MeetingId, messageId, ct)).Data
            ?? throw new McpOperationException("message_unavailable");
        if (message.OriginalTextAttachmentId is not Guid id) throw new McpOperationException("no_original_text");
        var file = await files.OpenAsync(c.MeetingId, id, ct);
        await using var stream = file.Content;
        if (offset > file.SizeBytes) throw new McpOperationException("invalid_text_range");
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(limit, file.SizeBytes - offset)];
        await stream.ReadExactlyAsync(bytes, ct);
        var text = Prefix(bytes, out var length);
        var next = offset + length;
        return new(text, next < file.SizeBytes ? next : null, file.SizeBytes);
    }
}
