using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentCollaborationOptions
{
	public const string SectionName = "AgentCollaboration";

	[Range(1, int.MaxValue)]
	public int MinimumContextCharacterLimit { get; set; } = 256;

	[Range(1, int.MaxValue)]
	public int DefaultContextCharacterLimit { get; set; } = 12000;

	[Range(1, int.MaxValue)]
	public int MaximumContextCharacterLimit { get; set; } = 120000;

	[Range(1, int.MaxValue)]
	public int WaitTimeoutSeconds { get; set; } = 300;

	[Range(1, int.MaxValue)]
	public int ReplyCharacterLimit { get; set; } = 12000;

	[Range(1, 86400)]
	public int JoinRateLimitWindowSeconds { get; set; } = 300;

	[Range(1, int.MaxValue)]
	public int JoinRateLimitPerSource { get; set; } = 100;

	[Range(1, int.MaxValue)]
	public int JoinRateLimitPerMeetingCode { get; set; } = 30;

	[Range(1, int.MaxValue)]
	public int JoinRateLimitPerName { get; set; } = 30;

	[Range(1, int.MaxValue)]
	public int JoinRateLimitPerConnection { get; set; } = 10;

	[Range(1, int.MaxValue)]
	public int WakeSignalCapacity { get; set; } = 256;
}
