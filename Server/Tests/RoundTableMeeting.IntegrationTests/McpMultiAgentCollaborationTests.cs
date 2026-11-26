using System.Text.Json;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProductionSdkAgentsCollaborateAcrossBothModesWithoutDuplicateReplies(bool reliableA, bool reliableB)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Mixed collaboration");
        using var http = CreateAnonymousClient(factory);
        await using var alpha = await CreateSdkAsync(http);
        await using var beta = await CreateSdkAsync(http);
        var tools = await alpha.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(9, tools.Count);
        Assert.Equal(new[] { "confirm_context", "get_agent_status", "join_meeting", "renew_agent_lease", "reply_to_mention", "report_task_failure", "resume_agent", "retry_task", "wait_for_mention" },
            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        var schemas = tools.ToDictionary(tool => tool.Name, tool => JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText()));

        async Task<JsonElement> Call(McpClient client, string name, object args)
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
            var payload = SdkPayload(await client.CallToolAsync(name, arguments).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(schemas[name].Evaluate(payload, new() { RequireFormatValidation = true }).IsValid, name + " output schema");
            Assert.True(payload.GetProperty("succeeded").GetBoolean(), payload.GetProperty("errorCode").ToString());
            return payload.GetProperty("data");
        }
        async Task<Guid> Join(McpClient client, string name, bool reliable)
        {
            var args = new Dictionary<string, object?>
            {
                ["meetingCode"] = meeting.MeetingCode, ["displayName"] = name, ["contextCharacterLimit"] = reliable ? 1000 : 12000
            };
            if (reliable)
            {
                args["reliabilityMode"] = "explicit_ack_v1";
                args["operationId"] = Guid.NewGuid(); args["recoveryKey"] = new string(name == "Alpha" ? 'A' : 'B', 42) + "A";
            }
            var result = await Call(client, "join_meeting", args);
            Assert.Equal(reliable ? "resident-instruction-v3-reliable" : "resident-instruction-v3-legacy",
                result.GetProperty("residentInstructionVersion").GetString());
            Assert.Contains("Human and Agent", result.GetProperty("residentInstruction").GetString());
            Assert.Equal(reliable, result.TryGetProperty("capabilities", out var capabilities));
            if (reliable) Assert.Contains(capabilities.EnumerateArray(), item => item.GetString() == "multi_agent_collaboration_v1");
            var id = result.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            if (reliable) await Call(client, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = result.GetProperty("initialDeliveryId").GetGuid() });
            return id;
        }
        async Task<JsonElement> Wait(McpClient client, Guid id, bool reliable)
        {
            var mention = await Call(client, "wait_for_mention", new { connectionSessionId = id });
            Assert.Equal("mention", mention.GetProperty("kind").GetString());
            if (reliable) await Call(client, "confirm_context", new { connectionSessionId = id, kind = "task",
                taskId = mention.GetProperty("taskId").GetGuid(), deliveryAttemptId = mention.GetProperty("deliveryAttemptId").GetGuid() });
            return mention;
        }
        object ReplyArgs(Guid id, JsonElement task, string content) => new
        {
            connectionSessionId = id, taskId = task.GetProperty("taskId").GetGuid(),
            deliveryAttemptId = task.GetProperty("deliveryAttemptId").GetGuid(),
            replyIdempotencyKey = task.GetProperty("replyIdempotencyKey").GetGuid(), content
        };

        var a = await Join(alpha, "Alpha", reliableA);
        var b = await Join(beta, "Beta", reliableB);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Alpha please ask Beta");
        var root = await Wait(alpha, a, reliableA);
        var rootId = root.GetProperty("triggerMessageId").GetGuid();
        Assert.Equal(rootId, root.GetProperty("collaboration").GetProperty("chainId").GetGuid());
        Assert.Equal("human", root.GetProperty("collaboration").GetProperty("sourceAuthorKind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("collaboration").GetProperty("parentTaskId").ValueKind);
        if (reliableA)
        {
            var rootStatus = await Call(alpha, "get_agent_status", new { connectionSessionId = a, taskId = root.GetProperty("taskId").GetGuid() });
            Assert.Equal(root.GetProperty("collaboration").GetRawText(), rootStatus.GetProperty("requestedTask").GetProperty("collaboration").GetRawText());
        }
        var args = ReplyArgs(a, root, "😀 @Beta review this @Beta");
        var accepted = await Call(alpha, "reply_to_mention", args);
        var replay = await Call(alpha, "reply_to_mention", args);
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(accepted.GetProperty("message").GetProperty("messageId").GetGuid(), replay.GetProperty("message").GetProperty("messageId").GetGuid());
        var child = await Wait(beta, b, reliableB);
        var source = child.GetProperty("collaboration");
        Assert.Equal(rootId, source.GetProperty("chainId").GetGuid());
        Assert.Equal(root.GetProperty("taskId").GetGuid(), source.GetProperty("parentTaskId").GetGuid());
        Assert.Equal("agent", source.GetProperty("sourceAuthorKind").GetString());
        var status = await Call(beta, "get_agent_status", new { connectionSessionId = b });
        Assert.Equal(source.GetRawText(), status.GetProperty("queueHead").GetProperty("collaboration").GetRawText());
        if (reliableB)
        {
            var requested = await Call(beta, "get_agent_status", new { connectionSessionId = b, taskId = child.GetProperty("taskId").GetGuid() });
            Assert.Equal(source.GetRawText(), requested.GetProperty("requestedTask").GetProperty("collaboration").GetRawText());
            var blocked = SdkPayload(await beta.CallToolAsync("wait_for_mention", new Dictionary<string, object?> { ["connectionSessionId"] = b }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("task_in_progress", blocked.GetProperty("errorCode").GetString());
            Assert.True(schemas["wait_for_mention"].Evaluate(blocked).IsValid);
            Assert.Equal(source.GetRawText(), blocked.GetProperty("data").GetProperty("taskSummary").GetProperty("collaboration").GetRawText());
            var forbidden = SdkPayload(await beta.CallToolAsync("get_agent_status", new Dictionary<string, object?>
                { ["connectionSessionId"] = b, ["taskId"] = root.GetProperty("taskId").GetGuid() }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(forbidden.GetProperty("succeeded").GetBoolean());
            Assert.Equal("task_not_available", forbidden.GetProperty("errorCode").GetString());
            Assert.DoesNotContain("collaboration", forbidden.GetRawText());
        }
        await Call(beta, "reply_to_mention", ReplyArgs(b, child, "@Alpha reviewed"));
        if (reliableB)
        {
            await using var resumedBeta = await CreateSdkAsync(http);
            var resumed = await Call(resumedBeta, "resume_agent", new { connectionSessionId = b,
                recoveryKey = new string('B', 42) + "A", operationId = Guid.NewGuid() });
            Assert.Equal(source.GetRawText(), resumed.GetProperty("lastRepliedTask").GetProperty("collaboration").GetRawText());
            Assert.Contains(resumed.GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "multi_agent_collaboration_v1");
        }
        var back = await Wait(alpha, a, reliableA);
        Assert.Equal(rootId, back.GetProperty("collaboration").GetProperty("chainId").GetGuid());
        Assert.Equal(child.GetProperty("taskId").GetGuid(), back.GetProperty("collaboration").GetProperty("parentTaskId").GetGuid());
        await Call(alpha, "reply_to_mention", ReplyArgs(a, back, "Review recorded."));
        var empty = await Call(alpha, "get_agent_status", new { connectionSessionId = a });
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("queueHead").ValueKind);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(3, await db.AgentReplyTasks.CountAsync(task => task.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(4, await db.MeetingMessages.CountAsync(message => message.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
