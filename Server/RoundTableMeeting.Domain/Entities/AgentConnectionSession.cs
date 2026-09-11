using System;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentConnectionSession : BaseEntity
{
	private const long MaximumSequence = 9007199254740991L;

	public const string StreamableHttpTransport = "mcp-streamable-http";

	public Guid MeetingId { get; private set; }

	public Guid ParticipantId { get; private set; }

	public string Transport { get; private set; } = "mcp-streamable-http";

	public AgentConnectionState State { get; private set; }

	public int DeclaredContextCharacterLimit { get; private set; }

	public string McpTransportBinding { get; private set; } = string.Empty;

	public DateTimeOffset ConnectedAtUtc { get; private set; }

	public DateTimeOffset LastActivityAtUtc { get; private set; }

	public DateTimeOffset? DisconnectedAtUtc { get; private set; }

	public DateTimeOffset CreatedAtUtc { get; private set; }

	public AgentConnectionSession()
	{
	}

	private AgentConnectionSession(Guid meetingId, Guid participantId, int declaredContextCharacterLimit, string mcpTransportBinding, DateTimeOffset connectedAtUtc)
	{
		if (meetingId == Guid.Empty)
		{
			throw new ArgumentException("Meeting is required.", "meetingId");
		}
		if (participantId == Guid.Empty)
		{
			throw new ArgumentException("Participant is required.", "participantId");
		}
		if (declaredContextCharacterLimit < 1)
		{
			throw new ArgumentOutOfRangeException("declaredContextCharacterLimit");
		}
		McpTransportBinding = NormalizeBinding(mcpTransportBinding);
		MeetingId = meetingId;
		ParticipantId = participantId;
		DeclaredContextCharacterLimit = declaredContextCharacterLimit;
		State = AgentConnectionState.Connected;
		ConnectedAtUtc = connectedAtUtc;
		LastActivityAtUtc = connectedAtUtc;
		CreatedAtUtc = connectedAtUtc;
	}

	public static AgentConnectionSession Create(Guid meetingId, Guid participantId, int declaredContextCharacterLimit, string mcpTransportBinding, DateTimeOffset connectedAtUtc)
	{
		return new AgentConnectionSession(meetingId, participantId, declaredContextCharacterLimit, mcpTransportBinding, connectedAtUtc);
	}

	public void Touch(DateTimeOffset activityAtUtc)
	{
		EnsureConnected();
		EnsureActivityTime(activityAtUtc);
		LastActivityAtUtc = activityAtUtc;
	}

	public bool Disconnect(DateTimeOffset disconnectedAtUtc)
	{
		if (State is AgentConnectionState.Disconnected or AgentConnectionState.Superseded)
		{
			return false;
		}
		EnsureActivityTime(disconnectedAtUtc);
		State = AgentConnectionState.Disconnected;
		DisconnectedAtUtc = disconnectedAtUtc;
		LastActivityAtUtc = disconnectedAtUtc;
		return true;
	}

	private void EnsureConnected()
	{
		if (State != AgentConnectionState.Connected)
		{
			throw new InvalidOperationException("The connection is disconnected.");
		}
	}

	private void EnsureActivityTime(DateTimeOffset value)
	{
		if (value < LastActivityAtUtc)
		{
			throw new ArgumentOutOfRangeException("value");
		}
	}

	private static string NormalizeBinding(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("MCP transport binding is required.", "value");
		}
		string text = value.Trim();
		if (text.Length > 128)
		{
			throw new ArgumentException("MCP transport binding is too long.", "value");
		}
		return text;
	}
}
