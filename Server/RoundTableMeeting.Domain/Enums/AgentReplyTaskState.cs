namespace RoundTableMeeting.Domain.Enums;

public enum AgentReplyTaskState
{
	Queued,
	Delivered,
	Processing,
	Replied,
	Failed,
	Cancelled
}
