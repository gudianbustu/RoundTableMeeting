namespace RoundTableMeeting.Domain.Exceptions;

public sealed class MeetingJoinConflictException(string message, Exception? innerException = null) : Exception(message, innerException);
