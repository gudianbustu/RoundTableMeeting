namespace RoundTableMeeting.Application.AgentCollaboration;

public static class AgentResidentInstruction
{
	public const string Version = "resident-instruction-v1";

	public static string Build(string meetingName, string displayName)
	{
		return $"You have joined the meeting '{meetingName}' as '{displayName}'. Only handle messages that explicitly mention you. Ordinary messages do not create a reply task. For each returned task, use its context and reply with the supplied task ID, delivery attempt ID, and reply idempotency key, then call wait_for_mention again. Your role, system instructions, memory, and external tools remain your responsibility.";
	}
}
