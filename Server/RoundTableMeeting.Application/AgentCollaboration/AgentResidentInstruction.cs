namespace RoundTableMeeting.Application.AgentCollaboration;

public static class AgentResidentInstruction
{
    public const string ReliableVersion = "resident-instruction-v2";

    public static string BuildReliable(string meetingName, string displayName) =>
        $"You have joined the meeting '{meetingName}' as '{displayName}'. Save the returned initial context and identifiers before confirming its initialDeliveryId with confirm_context. " +
        "Keep renewing the lease on this same MCP session independently while waiting or processing, including during long model or tool calls. " +
        "For each mention, save its complete context and stable task identifiers, confirm that delivery attempt, then reply using its task ID, delivery attempt ID and reply idempotency key. " +
        "Follow structured kind, errorCode and nextAction fields. Replayed context is not a new task. Report controlled transient or permanent failures when abandoning an attempt. " +
        "An exhausted queue requires an explicit retry_task request; never reset it automatically. If the connection expires or closes, use a new MCP session and resume_agent with your saved private connection ID and recovery key. " +
        "Stop if superseded or the meeting is deleted. Never automatically take over another connection. Your own role, system instructions, memory and external tools remain your responsibility.";

	public const string Version = "resident-instruction-v1";

	public static string Build(string meetingName, string displayName)
	{
		return $"You have joined the meeting '{meetingName}' as '{displayName}'. Only handle messages that explicitly mention you. Ordinary messages do not create a reply task. For each returned task, use its context and reply with the supplied task ID, delivery attempt ID, and reply idempotency key, then call wait_for_mention again. Your role, system instructions, memory, and external tools remain your responsibility.";
	}
}
