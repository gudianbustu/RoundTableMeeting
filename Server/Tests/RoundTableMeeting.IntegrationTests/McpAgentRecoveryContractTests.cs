using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task FrozenRecoveryDtosRoundTripThroughRealSdkWithExplicitOutputSchema()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        var samples = RecoverySamples();
        // Only this isolated host registers the probe. The public server never advertises unfinished tools.
        var probe = McpServerTool.Create((string sample) => samples[sample], new()
        { Name = "probe_recovery_contract", UseStructuredContent = true, SerializerOptions = options });
        probe.ProtocolTool.OutputSchema = AgentToolSchemas.CreateOutputSchema(options,
            samples.Values.Where(value => value.Succeeded).Select(value => value.Data!.GetType()).Distinct().ToArray());
        using var original = new TestApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddMcpServer().WithTools([probe])));
        using var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var tools = await sdk.ListToolsAsync();
        var advertised = Assert.Single(tools, tool => tool.Name == "probe_recovery_contract").ProtocolTool.OutputSchema!.Value;
        Assert.Equal(2, advertised.GetProperty("oneOf").GetArrayLength());
        AssertAllLocalSchemaReferencesResolve(advertised, advertised);
        var validator = JsonSchema.FromText(advertised.GetRawText());
        foreach (var (name, sample) in samples)
        {
            var result = SdkPayload(await sdk.CallToolAsync("probe_recovery_contract", new Dictionary<string, object?> { ["sample"] = name }));
            Assert.Equal("agent-messaging-v1", result.GetProperty("protocolVersion").GetString());
            Assert.Equal(sample.Succeeded, result.GetProperty("succeeded").GetBoolean());
            Assert.Equal(sample.ErrorCode, result.GetProperty("errorCode").GetString());
            Assert.True(validator.Evaluate(result, new() { RequireFormatValidation = true }).IsValid, name + " schema validation");
            var data = result.GetProperty("data");
            if (sample.Data is null) Assert.Equal(JsonValueKind.Null, data.ValueKind);
            else
            {
                // This boundary catches SDK structured-content transformations and missing runtime type fields.
                var expected = JsonSerializer.SerializeToElement(sample.Data, sample.Data.GetType(), options);
                Assert.True(JsonElement.DeepEquals(expected, data), name);
                Assert.NotNull(JsonSerializer.Deserialize(data, sample.Data.GetType(), options));
                var declared = advertised.GetProperty("$defs").GetProperty(sample.Data.GetType().Name);
                AssertRequiredRootFieldsPresent(declared, data);
            }
        }
        var errorData = SdkPayload(await sdk.CallToolAsync("probe_recovery_contract",
            new Dictionary<string, object?> { ["sample"] = "anonymous_error" })).GetProperty("data");
        Assert.Equal(new[] { "nextAction" }, errorData.EnumerateObject().Select(property => property.Name));
        Assert.Equal("stop", errorData.GetProperty("nextAction").GetString());
        var joined = SdkPayload(await sdk.CallToolAsync("probe_recovery_contract",
            new Dictionary<string, object?> { ["sample"] = "joined" })).GetProperty("data");
        Assert.Equal("initial", joined.GetProperty("contextPurpose").GetString());
        Assert.False(joined.GetProperty("initialContext").GetProperty("messages")[0].TryGetProperty("contextPurpose", out _));
        Assert.False(joined.GetProperty("initialContext").GetProperty("messages")[0].TryGetProperty("replayReason", out _));
        Assert.False(joined.TryGetProperty("recoveryKey", out _));
    }

    [Fact]
    public async Task LegacyJoinAndErrorsRetainTheirExactFieldSetsThroughSdk()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Legacy JSON compatibility");
        using var http = CreateAnonymousClient(factory);
        await using var sdk = await CreateSdkAsync(http);
        var result = SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Legacy", ["contextCharacterLimit"] = 12000
        }));
        var data = result.GetProperty("data");
        Assert.Equal(new[] { "agent", "connection", "initialContext", "meeting", "nextAction", "participants", "residentInstruction", "residentInstructionVersion" },
            data.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { "connectionSessionId", "declaredContextCharacterLimit", "initialContextState", "state" },
            data.GetProperty("connection").EnumerateObject().Select(property => property.Name).Order());
        var error = SdkPayload(await sdk.CallToolAsync("get_agent_status", new Dictionary<string, object?> { ["connectionSessionId"] = Guid.NewGuid() }));
        AssertToolFailure(error, "connection_invalid");
    }

    private static Dictionary<string, AgentOperationResult> RecoverySamples()
    {
        var id = Guid.Parse("a153093d-4dd9-469e-97a3-e4b44669503b");
        var now = DateTimeOffset.Parse("2026-09-09T08:00:00+00:00");
        var connection = new AgentConnectionDto(id, "connected", 12000, "prepared")
        { ReliabilityMode = AgentReliabilityProtocol.ExplicitAck, ConnectionGeneration = 1, BindingVersion = 2, LeaseExpiresAt = now.AddMinutes(2) };
        var context = new AgentContextDto("agent-context-v1", 1, 1, 1, 1, 1, 12000, 10, false, false, 0,
            [new(id, id, 1, "human", id, "Alice", "hello", [], now)]);
        var policy = new AgentLeasePolicyDto(120, 30, 30, 30, 300, 60);
        var task = new AgentTaskSummaryDto(id, 1, "failed", "agent_transient_failure")
        {
            Reason = "retry_exhausted", CurrentAttemptId = id, RetryCycle = 0, AutomaticRetryCount = 3,
            RetryExhausted = true, StopReason = "attempts_exhausted", NextAction = "await_explicit_retry"
        };
        var message = new MessageDto(id, id, 2, new(id, MessageAuthorKind.Agent, "Worker"), "done", [], now);
        var samples = new Dictionary<string, object>
        {
            ["joined"] = new AgentJoinResultDto(new(id, "Meeting", null), new(id, "Worker", "agent"), connection, [],
                "resident-instruction-v2", "test fixture", context, "confirm_context")
            { Kind = "joined", JoinDisposition = "created", Replayed = false, InitialDeliveryId = id, ContextPurpose = "initial", LeasePolicy = policy, Capabilities = AgentReliabilityProtocol.Capabilities },
            ["recovery_required"] = new AgentJoinRecoveryRequiredDto(id, true),
            ["resumed"] = new AgentResumeResultDto(connection, false, id, context, task, null, policy, AgentReliabilityProtocol.Capabilities, "confirm_context"),
            ["resumed_without_context"] = new AgentResumeResultDto(connection, true, null, null, task, null, policy, AgentReliabilityProtocol.Capabilities, "get_agent_status"),
            ["confirmed"] = new AgentConfirmContextResultDto(false, 1, "reply_to_mention", TaskId: id, DeliveryAttemptId: id),
            ["renewed"] = new AgentLeaseResultDto(now, now.AddMinutes(2), 30),
            ["failure"] = new AgentFailureResultDto(id, 0, 0, false, true, "attempts_exhausted", null, "await_explicit_retry"),
            ["retry"] = new AgentRetryResultDto(id, 1, 1, false, false, null, now, "wait_for_mention"),
            ["mention"] = new AgentMentionResultDto("mention", id, "delivered", id, 1, id, id, id, id, context, "confirm_context")
            { ConfirmationDeadline = now.AddSeconds(60), Replayed = false, ContextPurpose = "task" },
            ["idle"] = new AgentWaitIdleDto("idle", "wait_for_mention"),
            ["reply"] = new AgentReplyResultDto(message, false, "wait_for_mention") { Kind = "replied" },
            ["status"] = new AgentStatusResultDto(id, id, connection, new Dictionary<string, int> { ["failed"] = 1 }, task, 1, 1)
            { Kind = "agent_status", InitialDeliveryId = id, RequestedTask = task, NextAction = "await_explicit_retry" }
        }.ToDictionary(pair => pair.Key, pair => AgentOperationResult.Success(pair.Value, "Contract sample."));
        samples["legacy_error"] = AgentOperationResult.Failure("connection_invalid", "Invalid connection.");
        samples["anonymous_error"] = AgentOperationResult.Failure("resume_rejected", "Resume rejected.", new("stop"));
        samples["retry_error"] = AgentOperationResult.Failure("retry_exhausted", "Task blocked.", new("await_explicit_retry")
        { ServerTime = now, TaskSummary = task, RetryExhausted = true, CurrentBindingVersion = 2 });
        return samples;
    }

    private static void AssertRequiredRootFieldsPresent(JsonElement schema, JsonElement data)
    {
        if (schema.TryGetProperty("required", out var required))
            foreach (var property in required.EnumerateArray()) Assert.True(data.TryGetProperty(property.GetString()!, out _), property.GetString());
    }

    private static void AssertAllLocalSchemaReferencesResolve(JsonElement root, JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("$ref", out var reference))
            {
                var target = root;
                foreach (var segment in reference.GetString()![2..].Split('/'))
                {
                    var name = segment.Replace("~1", "/").Replace("~0", "~");
                    target = target.ValueKind == JsonValueKind.Array ? target[int.Parse(name)] : target.GetProperty(name);
                }
            }
            foreach (var child in node.EnumerateObject()) AssertAllLocalSchemaReferencesResolve(root, child.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) AssertAllLocalSchemaReferencesResolve(root, child);
    }
}
