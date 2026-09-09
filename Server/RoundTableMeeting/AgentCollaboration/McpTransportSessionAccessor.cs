using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class McpTransportSessionAccessor : IMcpTransportSessionAccessor
{
	public McpTransportSessionKey? Current { get; private set; }

	public void Bind(McpServer server)
	{
		McpServer session = server;
		Current = FromSessionId(session.SessionId);
	}

	public static McpTransportSessionKey? FromSessionId(string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId)) return null;
		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
		return McpTransportSessionKey.FromDigest(digest);
	}
}
