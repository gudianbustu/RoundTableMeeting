using System;

namespace RoundTableMeeting.Domain.Exceptions;

public sealed class AgentCollaborationConflictException : Exception
{
	public AgentCollaborationConflictException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
	}
}
