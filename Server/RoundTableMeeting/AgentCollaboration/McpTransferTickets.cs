using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
namespace RoundTableMeeting.AgentCollaboration;
public sealed record McpTransferTicket(string Url, string Method, string BearerToken, DateTimeOffset ExpiresAt, long MaxFileBytes);
public sealed record McpTransferBinding(McpTransportSessionKey Transport, Guid ConnectionId, string Method, Guid? AttachmentId, DateTimeOffset ExpiresAt);
public sealed class McpTransferTickets(TimeProvider time, IOptions<AttachmentOptions> options)
{
    private readonly Dictionary<string, McpTransferBinding> tickets = [];
    private readonly object sync = new();
    public McpTransferTicket Issue(McpTransportSessionKey transport, Guid connectionId, string method, Guid? attachmentId)
    {
        lock (sync)
        {
            foreach (var old in tickets.Where(p => p.Value.ExpiresAt <= time.GetUtcNow()).Select(p => p.Key).ToArray()) tickets.Remove(old);
            if (tickets.Count >= 4096 || tickets.Values.Count(t => t.ConnectionId == connectionId) >= 16) throw new McpOperationException("transfer_ticket_limit");
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = time.GetUtcNow().AddMinutes(5);
            tickets.Add(secret, new(transport, connectionId, method, attachmentId, expires));
            return new("/mcp-files", method, secret, expires, options.Value.MaxFileBytes);
        }
    }
    public McpTransferBinding? Find(string token)
    {
        lock (sync) return tickets.TryGetValue(token, out var ticket) && ticket.ExpiresAt > time.GetUtcNow() ? ticket : null;
    }
}
