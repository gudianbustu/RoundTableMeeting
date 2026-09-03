namespace RoundTableMeeting.Domain.Exceptions;

public sealed class MessageAppendConflictException : Exception
{
    public MessageAppendConflictException(string message, Exception? innerException = null) : base(message, innerException) { }
}
