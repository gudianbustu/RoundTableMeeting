namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentJoinRateLimiter
{
	bool TryAcquire(string? sourceAddress, string? meetingCode, string? displayName, McpTransportSessionKey transportSession, out int retryAfterSeconds);
}
