using System.Text.Json;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    private static readonly Lazy<JsonSchema> BusinessErrorSchema = new(() =>
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return JsonSchema.FromText(RoundTableMeeting.AgentCollaboration.AgentToolSchemas.CreateOutputSchema(options,
            typeof(RoundTableMeeting.Abstraction.Contracts.AgentCollaboration.AgentLeaseResultDto)).GetRawText());
    });

    private static void AssertBusinessErrorSchema(JsonElement payload)
    {
        if (payload.GetProperty("succeeded").GetBoolean()) return;
        Assert.True(BusinessErrorSchema.Value.Evaluate(payload, new() { RequireFormatValidation = true }).IsValid,
            "Business error must conform to the advertised MCP envelope.");
        Assert.Equal(new[] { "data", "errorCode", "message", "protocolVersion", "succeeded" },
            payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("errorCode").GetString()));
        var data = payload.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Null) return;
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("nextAction").GetString()));
        var fields = new HashSet<string>(["nextAction", "serverTime", "taskSummary", "nextRetryAt", "retryAfterSeconds", "retryExhausted", "currentBindingVersion"]);
        Assert.All(data.EnumerateObject(), field => Assert.Contains(field.Name, fields));
        CheckFields(data);
        static void CheckFields(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    Assert.DoesNotContain(property.Name, new[] { "recoveryKey", "recoveryKeyHash", "meetingCode",
                        "mcpTransportBinding", "transportBinding", "targetTransportBinding", "content", "initialContext", "context" });
                    CheckFields(property.Value);
                }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckFields(item);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionJoinValidationErrorsHaveModeCorrectSchemasAndNeverEchoPrivateInputs(bool reliable)
    {
        using var factory = new TestApplicationFactory();
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Join error envelopes");
        using var http = factory.CreateClient();
        await using var sdk = await CreateSdkAsync(http);
        var schema = JsonSchema.FromText((await sdk.ListToolsAsync()).Single(t => t.Name == "join_meeting").ProtocolTool.OutputSchema!.Value.GetRawText());
        var request = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = reliable ? "explicit_ack_v1" : "legacy_v1", operationId = reliable ? (Guid?)Guid.NewGuid() : null,
            recoveryKey = reliable ? new string('A', 43) : null };
        foreach (var (input, code) in new[]
        {
            (request with { displayName = " " }, "agent_name_required"),
            (request with { displayName = new string('N', 65) }, "agent_name_invalid"),
            (request with { contextCharacterLimit = 0 }, "context_limit_invalid"),
            (request with { displayName = "Alice" }, "agent_name_in_use"),
        })
        {
            var result = await ActivationCallAsync(sdk, "join_meeting", input);
            CheckPrivateErrorEnvelope(result, schema, code, reliable ? "stop" : null, meeting.MeetingCode, request.recoveryKey);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Empty(await db.AgentConnectionSessions.ToListAsync());
        Assert.Empty(await db.AgentRecoveryOperations.ToListAsync());
        CheckPrivateLog(factory, meeting.MeetingCode, request.recoveryKey);
    }

    [Fact]
    public async Task ProductionReplyValidationAndDurableDisconnectHaveSafeErrorsWithoutChangingTheTask()
    {
        using var factory = new TestApplicationFactory();
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reply error envelopes");
        using var http = factory.CreateClient();
        await using var sdk = await CreateSdkAsync(http);
        var schemas = (await sdk.ListToolsAsync()).ToDictionary(t => t.Name, t => JsonSchema.FromText(t.ProtocolTool.OutputSchema!.Value.GetRawText()));
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode, displayName = "Worker",
            contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker private-error-human-body");
        var mention = (await ProcessCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data");
        var reply = new { connectionSessionId = id, taskId = mention.GetProperty("taskId").GetGuid(),
            deliveryAttemptId = mention.GetProperty("deliveryAttemptId").GetGuid(),
            replyIdempotencyKey = mention.GetProperty("replyIdempotencyKey").GetGuid(), content = "private-error-reply-body" };
        async Task<string> Facts()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new { Task = await db.AgentReplyTasks.SingleAsync(),
                Attempt = await db.AgentTaskDeliveryAttempts.SingleAsync(), Cursor = await db.AgentContextCursors.SingleAsync(),
                Messages = await db.MeetingMessages.CountAsync() });
        }
        var before = await Facts();
        var confirmationRequired = await ActivationCallAsync(sdk, "reply_to_mention", reply);
        CheckPrivateErrorEnvelope(confirmationRequired, schemas["reply_to_mention"], "context_confirmation_required", "confirm_context", key, meeting.MeetingCode, reply.content);
        foreach (var content in new[] { " ", "private-error-reply-body" + new string('x', 50000) })
            CheckPrivateErrorEnvelope(await ActivationCallAsync(sdk, "reply_to_mention", reply with { content = content }), schemas["reply_to_mention"],
                "message_validation_failed", "stop", key, meeting.MeetingCode, "private-error-reply-body");
        Assert.Equal(before, await Facts());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync();
            // Reproduce the committed-disconnect / not-yet-retired-registry cut.
            Assert.True(connection.DisconnectBinding(connection.BindingVersion, "transport_closed", DateTimeOffset.UtcNow));
            db.Update(connection);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(RoundTableMeeting.Domain.Enums.AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync()).State);
        }
        foreach (var tool in new[] { "get_agent_status", "renew_agent_lease", "wait_for_mention" })
            CheckPrivateErrorEnvelope(await ActivationCallAsync(sdk, tool, new { connectionSessionId = id }), schemas[tool],
                "connection_disconnected", "resume_agent", key, meeting.MeetingCode);
        Assert.Equal(before, await Facts());
        CheckPrivateLog(factory, key, meeting.MeetingCode, "private-error-human-body", "private-error-reply-body");
    }

    private static void CheckPrivateErrorEnvelope(JsonElement result, JsonSchema schema, string code, string? nextAction, params string?[] secrets)
    {
        Assert.False(result.GetProperty("succeeded").GetBoolean());
        Assert.Equal(code, result.GetProperty("errorCode").GetString());
        Assert.True(schema.Evaluate(result, new() { RequireFormatValidation = true }).IsValid);
        var data = result.GetProperty("data");
        if (nextAction is null) Assert.Equal(JsonValueKind.Null, data.ValueKind);
        else
        {
            Assert.Equal(nextAction, data.GetProperty("nextAction").GetString());
            Assert.Equal(new[] { "nextAction" }, data.EnumerateObject().Select(p => p.Name));
        }
        foreach (var secret in secrets.Where(s => s is not null)) Assert.DoesNotContain(secret!, result.GetRawText(), StringComparison.Ordinal);
    }
    private static void CheckPrivateLog(TestApplicationFactory factory, params string?[] secrets)
    {
        var logs = string.Join('\n', factory.LogSink.Events.Select(e => e.RenderMessage() + " " + e.Exception + " " + string.Join(' ', e.Properties)));
        foreach (var secret in secrets.Where(s => s is not null)) Assert.DoesNotContain(secret!, logs, StringComparison.Ordinal);
    }
}
