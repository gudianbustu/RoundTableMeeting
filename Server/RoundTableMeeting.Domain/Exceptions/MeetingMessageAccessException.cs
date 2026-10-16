namespace RoundTableMeeting.Domain.Exceptions;

public sealed class MeetingMessageAccessException(string code) : Exception("Meeting message access was denied.")
{
    public string Code { get; } = code;
}
