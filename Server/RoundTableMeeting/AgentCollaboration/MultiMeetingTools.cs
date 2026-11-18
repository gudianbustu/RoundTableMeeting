using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.AgentCollaboration;

// Site-wide tool configuration; the private room handle also works after a transport reconnect.
public sealed class MultiMeetingTools(IServiceScopeFactory scopes, string directory, string? address, IDataProtectionProvider protection)
{
    private readonly IDataProtector handles = protection.CreateProtector("RoundTableMeeting.MultiMeeting.Room.v1");
    private int rotation;

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

    public sealed record Admission(string State, string Room);

    [McpServerTool(Name = "attend")]
    [Description("Join or resume one invitation. Keep its private room handle for next, reply, skip, transfers and leave. Other meetings stay joined.")]
    public async Task<Admission> Attend(Guid meetingCode, string displayName, string invitationKey, CancellationToken ct)
    {
        if (meetingCode == Guid.Empty || !Guid.TryParse(invitationKey, out var key) || key == Guid.Empty) throw new McpException("invalid_invitation");
        var id = new Identity(meetingCode, MeetingParticipant.NormalizeDisplayName(displayName), key);
        await Bind(id).Attend(ct);
        return new("joined", handles.Protect(JsonSerializer.Serialize(id)));
    }

    [McpServerTool(Name = "next")]
    [Description("Wait for a message in any of the supplied rooms. Use the room from the result when replying. On idle call again with all active rooms.")]
    public Task<SimpleMeetingTools.Delivery> Next(string[] rooms, McpServer host, IProgress<ProgressNotificationValue> progress, CancellationToken ct)
    {
        if (rooms is not { Length: > 0 }) throw new McpException("rooms_required");
        var active = rooms.Distinct().Select(room => (Room: room, Tools: Resolve(room))).ToArray();
        var start = (int)((uint)Interlocked.Increment(ref rotation) % active.Length);
        async Task<SimpleMeetingTools.Delivery> Poll()
        {
            SimpleMeetingTools.Delivery result = new("idle");
            for (var i = 0; i < active.Length; i++)
            {
                var entry = active[(start + i) % active.Length];
                SimpleMeetingTools.Delivery delivery;
                try { delivery = await entry.Tools.Poll(ct); }
                catch (McpException e) { return new("room_error", Room: entry.Room, Error: e.Message); }
                if (delivery.State == "message" && result.State != "message") result = delivery with { Room = entry.Room };
                else if (delivery.State == "more" && result.State == "idle") result = delivery;
            }
            return result;
        }
        return SimpleMeetingTools.WaitForMessage(Poll, host, progress, ct);
    }

    [McpServerTool(Name = "reply")]
    [Description("Reply in the room that delivered the pending message.")]
    public Task<string> Reply(string room, Guid messageId, string content, CancellationToken ct, Guid[]? attachmentIds = null)
        => Resolve(room).Reply(messageId, content, ct, attachmentIds);

    [McpServerTool(Name = "skip")]
    [Description("Skip the pending message in this room only.")]
    public Task<string> Skip(string room, Guid messageId, CancellationToken ct) => Resolve(room).Skip(messageId, ct);

    [McpServerTool(Name = "attachment_transfer")]
    [Description("Request a transfer ticket for this room only. Download via GET using attachmentId, or upload via POST with name and uploadRequestId; use bearerToken in Authorization at this origin.")]
    public Task<McpTransferTicket> Transfer(string room, CancellationToken ct, Guid? attachmentId = null)
        => Resolve(room).Transfer(ct, attachmentId);

    [McpServerTool(Name = "leave")]
    [Description("On explicit request, leave only the specified room. Continue waiting in other rooms.")]
    public Task<string> Leave(string room, CancellationToken ct) => Resolve(room).Leave(ct);
}
