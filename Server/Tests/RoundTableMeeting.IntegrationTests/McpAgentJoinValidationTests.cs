using System.Text.Json;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("unknown_mode", false)]
    [InlineData("empty_mode", false)]
    [InlineData("unknown_join_mode", true)]
    [InlineData("missing_operation", true)]
    [InlineData("empty_operation", true)]
    [InlineData("missing_key", true)]
    [InlineData("padded_key", true)]
    [InlineData("noncanonical_key", true)]
    [InlineData("short_key", true)]
    [InlineData("long_key", true)]
    [InlineData("illegal_key", true)]
    [InlineData("legacy_invalid_key", false)]
    [InlineData("legacy_empty_operation", false)]
    [InlineData("legacy_valid_recovery", false)]
    [InlineData("malformed_uuid", true)]
    [InlineData("malformed_budget", true)]
    public async Task ProductionJoinValidationSeparatesBusinessErrorsFromSdkRejectionWithoutCreatingIdentity(string scenario, bool reliable)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Join validation");
        using var http = CreateAnonymousClient(factory);
        await using var sdk = await CreateSdkAsync(http);
        var args = new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000,
            ["reliabilityMode"] = "explicit_ack_v1", ["operationId"] = Guid.NewGuid(), ["recoveryKey"] = new string('A', 43)
        };
        switch (scenario)
        {
            case "unknown_mode": args["reliabilityMode"] = "unknown"; break;
            case "empty_mode": args["reliabilityMode"] = ""; break;
            case "unknown_join_mode": args["joinMode"] = "unknown"; break;
            case "missing_operation": args.Remove("operationId"); break;
            case "empty_operation": args["operationId"] = Guid.Empty; break;
            case "missing_key": args.Remove("recoveryKey"); break;
            case "padded_key": args["recoveryKey"] = new string('A', 43) + "="; break;
            case "noncanonical_key": args["recoveryKey"] = new string('A', 42) + "B"; break;
            case "short_key": args["recoveryKey"] = new string('A', 42); break;
            case "long_key": args["recoveryKey"] = new string('A', 44); break;
            case "illegal_key": args["recoveryKey"] = new string('+', 43); break;
            case "legacy_invalid_key": args["reliabilityMode"] = "legacy_v1"; args["recoveryKey"] = "bad"; break;
            case "legacy_empty_operation": args["reliabilityMode"] = "legacy_v1"; args["operationId"] = Guid.Empty; break;
            case "legacy_valid_recovery": args["reliabilityMode"] = "legacy_v1"; break;
            case "malformed_uuid": args["operationId"] = "not-a-uuid"; break;
            case "malformed_budget": args["contextCharacterLimit"] = "not-a-number"; break;
        }
        var response = await sdk.CallToolAsync("join_meeting", args);
        if (scenario is "malformed_uuid" or "malformed_budget") Assert.True(response.IsError);
        else
        {
            var payload = SdkPayload(response);
            var tool = (await sdk.ListToolsAsync()).Single(t => t.Name == "join_meeting");
            Assert.True(JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText())
                .Evaluate(payload, new() { RequireFormatValidation = true }).IsValid);
            Assert.False(payload.GetProperty("succeeded").GetBoolean());
            Assert.Equal(scenario == "legacy_valid_recovery" ? "reliability_mode_required" : "agent_request_invalid",
                payload.GetProperty("errorCode").GetString());
            var data = payload.GetProperty("data");
            if (reliable)
            {
                Assert.Equal("stop", data.GetProperty("nextAction").GetString());
                Assert.False(data.TryGetProperty("connection", out _));
                Assert.False(data.TryGetProperty("queueHead", out _));
            }
            else Assert.Equal(JsonValueKind.Null, data.ValueKind);
            Assert.DoesNotContain(meeting.MeetingCode, payload.GetRawText());
            Assert.DoesNotContain(new string('A', 43), payload.GetRawText());
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Empty(await db.AgentConnectionSessions.ToArrayAsync());
        Assert.Empty(await db.AgentContextCursors.ToArrayAsync());
        Assert.Empty(await db.AgentInitialContextDeliveries.ToArrayAsync());
        Assert.Empty(await db.AgentRecoveryOperations.ToArrayAsync());
    }
}
