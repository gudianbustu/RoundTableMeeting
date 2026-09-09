namespace RoundTableMeeting.Abstraction.Services;

public interface IMcpTransportSessionAccessor
{
	McpTransportSessionKey? Current { get; }
}
