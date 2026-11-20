using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.AgentCollaboration;

// Site-wide tool configuration; the private room handle also works after a transport reconnect.
public sealed class MultiMeetingTools(IServiceScopeFactory scopes, string directory, string? address, IDataProtectionProvider protection, PeerTransport peers, Uri localSite, string pathBase)
{
    private readonly IDataProtector handles = protection.CreateProtector("RoundTableMeeting.MultiMeeting.Room.v1");
    private int rotation;
    private sealed record RemoteRoom(string Site, string Handle);
    private readonly IDataProtector routes = protection.CreateProtector("RoundTableMeeting.PeerRoom.v1", directory);

    internal static MultiMeetingTools ForRequest(HttpContext http)
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoundTableMeeting.Repository.Sqlite.Configuration.SqliteDatabaseOptions>>().Value.ConnectionString;
        var directory = RoundTableMeeting.Repository.Sqlite.Configuration.SqlitePathResolver.GetDatabaseDirectory(
            RoundTableMeeting.Repository.Sqlite.Configuration.SqlitePathResolver.NormalizeConnectionString(db));
        var request = http.Request;
        var local = PeerTransport.Normalize($"{request.Scheme}://{request.Host}{request.PathBase}/");
        return new(services.GetRequiredService<IServiceScopeFactory>(), directory, http.Connection.RemoteIpAddress?.ToString(),
            services.GetRequiredService<IDataProtectionProvider>(), services.GetRequiredService<PeerTransport>(), local, request.PathBase.Value ?? "");
    }

    private RemoteRoom? Remote(string room)
    {
        if (string.IsNullOrEmpty(room)) throw new McpException("invalid_room");
        if (!room.StartsWith("f1.", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<RemoteRoom>(routes.Unprotect(room[3..])) ?? throw new JsonException(); }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        { throw new McpException("invalid_room"); }
    }

    private Task<T> Route<T>(string room, string operation, PeerCommand args, Func<SimpleMeetingTools, Task<T>> local, CancellationToken ct)
        => Remote(room) is { } remote
            ? peers.Call<T>(remote.Site, operation, args with { Room = remote.Handle }, ct)
            : local(Resolve(room));


    private sealed record Identity(Guid Code, string Name, Guid Key);

    private SimpleMeetingTools Bind(Identity id) => new(scopes, directory, address, id.Code.ToString(), id.Name, id.Key);

    private SimpleMeetingTools Resolve(string room)
    {
        try
        {
            var id = JsonSerializer.Deserialize<Identity>(handles.Unprotect(room))!;
            return Bind(id);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or JsonException)
        {
            throw new McpException("invalid_room");
        }
    }

    internal SimpleMeetingTools Local(string room) => Resolve(room);

    public sealed record Admission(string State, string Room);

    [McpServerTool(Name = "attend")]
    [Description("Pass the invited siteUrl to join or resume on that site using this MCP connection. Keep its private room handle for next, reply, skip, transfers and leave. Other meetings stay joined.")]
    public async Task<Admission> Attend(Guid meetingCode, string displayName, string invitationKey, CancellationToken ct, string? siteUrl = null)
    {
        if (meetingCode == Guid.Empty || !Guid.TryParse(invitationKey, out var key) || key == Guid.Empty) throw new McpException("invalid_invitation");
        var id = new Identity(meetingCode, MeetingParticipant.NormalizeDisplayName(displayName), key);
        if (siteUrl is not null)
        {
            var site = PeerTransport.Normalize(siteUrl);
            if (site != localSite && site != peers.PublicSite)
            {
                var admitted = await peers.Call<Admission>(site.AbsoluteUri, "attend", new(MeetingCode: meetingCode, DisplayName: id.Name, InvitationKey: invitationKey), ct);
                return new("joined", "f1." + routes.Protect(JsonSerializer.Serialize(new RemoteRoom(site.AbsoluteUri, admitted.Room))));
            }
        }
        await Bind(id).Attend(ct);
        return new("joined", handles.Protect(JsonSerializer.Serialize(id)));
    }

    [McpServerTool(Name = "next")]
    [Description("Wait for a message in any of the supplied rooms. Use the returned room to reply or skip. Read discussionContext with the message as one turn. Reply once if you have something to add, otherwise skip. Ordinary public Agent replies can continue an open topic. On idle call again with all active rooms.")]
    public Task<SimpleMeetingTools.Delivery> Next(string[] rooms, McpServer host, IProgress<ProgressNotificationValue> progress, CancellationToken ct)
    {
        if (rooms is not { Length: > 0 }) throw new McpException("rooms_required");
        var active = rooms.Distinct().ToArray();
        var start = (int)((uint)Interlocked.Increment(ref rotation) % active.Length);
        async Task<SimpleMeetingTools.Delivery> Poll()
        {
            var ordered = active.Skip(start).Concat(active.Take(start)).ToArray();
            var results = new SimpleMeetingTools.Delivery[ordered.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Length), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
            {
                var room = ordered[i];
                try { results[i] = (await Route(room, "poll", new(), tools => tools.Poll(token), token)) with { Room = room }; }
                catch (McpException e) { results[i] = new("room_error", Room: room, Error: e.Message); }
            });
            return results.FirstOrDefault(r => r.State == "message") ?? results.FirstOrDefault(r => r.State == "room_error")
                ?? results.FirstOrDefault(r => r.State == "more") ?? new("idle");
        }
        return SimpleMeetingTools.WaitForMessage(Poll, host, progress, ct);
    }

    [McpServerTool(Name = "history")]
    [Description("On demand, read earlier context in this room. Omit beforeSequence for the latest page. This does not consume pending messages.")]
    public Task<Abstraction.Contracts.RealtimeChat.MessagePageDto> History(string room, CancellationToken ct, long? beforeSequence = null, int limit = 5)
        => Route(room, "history", new(BeforeSequence: beforeSequence, Limit: limit), tool => tool.History(ct, beforeSequence, limit), ct);

    [McpServerTool(Name = "reply")]
    [Description("Keep messageId from the pending delivery. To answer a specific contribution in a free discussion, pass its ID as replyToMessageId (from discussionContext or history). On discussion_ended discard the draft and call next.")]
    public Task<string> Reply(string room, Guid messageId, string content, CancellationToken ct, Guid[]? attachmentIds = null, Guid? replyToMessageId = null)
        => Route(room, "reply", new(MessageId: messageId, Content: content, AttachmentIds: attachmentIds, ReplyToMessageId: replyToMessageId), tool => tool.Reply(messageId, content, ct, attachmentIds, replyToMessageId), ct);

    [McpServerTool(Name = "send_public_message")]
    [Description("Post a new public message visible to everyone in this meeting, independently of pending deliveries. Use a fresh clientSubmissionId UUID per message and reuse it with the same payload on retry. Returns the message ID.")]
    public Task<Guid> SendPublicMessage(string room, Guid clientSubmissionId, string content, CancellationToken ct, Guid[]? attachmentIds = null)
        => Route(room, "send_public_message", new(ClientSubmissionId: clientSubmissionId, Content: content, AttachmentIds: attachmentIds),
            tool => tool.SendPublicMessage(clientSubmissionId, content, ct, attachmentIds), ct);

    [McpServerTool(Name = "send_private_message")]
    [Description("Start a private message in this room. Specify exactly one of recipientParticipantId or recipientDisplayName (exact meeting nickname). Save a new clientSubmissionId UUID per message; retry with the same UUID and payload. Returns the message ID without consuming any pending delivery.")]
    public Task<Guid> SendPrivateMessage(string room, Guid clientSubmissionId, string content, CancellationToken ct,
        Guid? recipientParticipantId = null, string? recipientDisplayName = null, Guid[]? attachmentIds = null)
        => Route(room, "send_private_message", new(ClientSubmissionId: clientSubmissionId, Content: content,
            RecipientParticipantId: recipientParticipantId, RecipientDisplayName: recipientDisplayName, AttachmentIds: attachmentIds),
            tool => tool.SendPrivateMessage(clientSubmissionId, content, ct, recipientParticipantId, recipientDisplayName, attachmentIds), ct);

    [McpServerTool(Name = "skip")]
    [Description("Skip the pending message in this room only.")]
    public Task<string> Skip(string room, Guid messageId, CancellationToken ct) => Route(room, "skip", new(MessageId: messageId), tool => tool.Skip(messageId, ct), ct);

    [McpServerTool(Name = "attachment_transfer")]
    [Description("Request a transfer ticket for this room only. Download via GET using attachmentId, or upload via POST with name and uploadRequestId; use bearerToken in Authorization at this origin.")]
    public async Task<McpTransferTicket> Transfer(string room, CancellationToken ct, Guid? attachmentId = null)
    {
        if (Remote(room) is not { } remote) return await Resolve(room).Transfer(ct, attachmentId);
        var ticket = await peers.Call<McpTransferTicket>(remote.Site, "transfer", new(Room: remote.Handle, AttachmentId: attachmentId), ct);
        return peers.WrapTicket(remote.Site, ticket, pathBase);
    }

    [McpServerTool(Name = "leave")]
    [Description("On explicit request, leave only the specified room. Continue waiting in other rooms.")]
    public Task<string> Leave(string room, CancellationToken ct) => Route(room, "leave", new(), tool => tool.Leave(ct), ct);
}
