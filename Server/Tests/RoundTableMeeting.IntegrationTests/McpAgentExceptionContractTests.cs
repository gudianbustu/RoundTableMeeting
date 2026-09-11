using System.Text.Json;
using Json.Schema;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedToolExceptionsPreserveTheNegotiatedErrorEnvelope(bool reliable)
    {
        using var original = new TestApplicationFactory();
        var fault = new AgentExceptionSwitch();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentCollaborationService>();
            services.AddScoped<IAgentCollaborationService>(provider => new ExceptionCollaboration(
                provider.GetRequiredService<AgentCollaborationService>(), fault));
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Exception envelope");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var tools = await sdk.ListToolsAsync();
        var schemas = tools.ToDictionary(tool => tool.Name,
            tool => JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText()));
        var joinArguments = new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000,
        };
        if (reliable)
        {
            joinArguments["reliabilityMode"] = "explicit_ack_v1";
            joinArguments["operationId"] = Guid.NewGuid();
            joinArguments["recoveryKey"] = new string('A', 43);
        }
        var joined = SdkPayload(await sdk.CallToolAsync("join_meeting", joinArguments));
        Assert.True(joined.GetProperty("succeeded").GetBoolean());
        var id = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        fault.Enabled = true;
        foreach (var (name, arguments) in ExceptionToolArguments(id))
        {
            var payload = SdkPayload(await sdk.CallToolAsync(name, arguments));
            AssertExceptionEnvelope(payload, reliable);
            Assert.True(schemas[name].Evaluate(JsonSerializer.SerializeToNode(payload)).IsValid, name);
        }
        // A guessed business ID cannot inherit the real connection's mode or diagnostics.
        AssertExceptionEnvelope(SdkPayload(await sdk.CallToolAsync("get_agent_status",
            new Dictionary<string, object?> { ["connectionSessionId"] = Guid.NewGuid() })), reliable: false);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("legacy_v1", false)]
    [InlineData("unknown", false)]
    [InlineData("explicit_ack_v1", true)]
    public async Task JoinExceptionsUseOnlyTheExplicitlyRecognizedMode(string? mode, bool reliable)
    {
        using var original = new TestApplicationFactory();
        var fault = new AgentExceptionSwitch { Enabled = true };
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentCollaborationService>();
            services.AddScoped<IAgentCollaborationService>(provider => new ExceptionCollaboration(
                provider.GetRequiredService<AgentCollaborationService>(), fault));
        }));
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var payload = SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = Guid.NewGuid().ToString(), ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000,
            ["reliabilityMode"] = mode, ["operationId"] = Guid.NewGuid(), ["recoveryKey"] = new string('A', 43),
        }));
        AssertExceptionEnvelope(payload, reliable);
        if (reliable) Assert.Equal("stop", payload.GetProperty("data").GetProperty("nextAction").GetString());
        // New tools have a declared reliable envelope even before binding or authentication.
        foreach (var (name, arguments) in ExceptionToolArguments(Guid.NewGuid()).Where(item =>
            item.Name is not ("wait_for_mention" or "reply_to_mention" or "get_agent_status")))
        {
            var anonymous = SdkPayload(await sdk.CallToolAsync(name, arguments));
            AssertExceptionEnvelope(anonymous, reliable: true);
            Assert.Equal("stop", anonymous.GetProperty("data").GetProperty("nextAction").GetString());
        }
    }

    private static void AssertExceptionEnvelope(JsonElement payload, bool reliable)
    {
        Assert.False(payload.GetProperty("succeeded").GetBoolean());
        Assert.Equal("agent_operation_unavailable", payload.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("sensitive-exception-detail", payload.GetRawText());
        var data = payload.GetProperty("data");
        if (!reliable) { Assert.Equal(JsonValueKind.Null, data.ValueKind); return; }
        Assert.Equal(new[] { "nextAction" }, data.EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static IEnumerable<(string Name, Dictionary<string, object?> Arguments)> ExceptionToolArguments(Guid id)
    {
        yield return ("wait_for_mention", new() { ["connectionSessionId"] = id });
        yield return ("get_agent_status", new() { ["connectionSessionId"] = id });
        yield return ("reply_to_mention", new() { ["connectionSessionId"] = id, ["taskId"] = Guid.NewGuid(),
            ["deliveryAttemptId"] = Guid.NewGuid(), ["replyIdempotencyKey"] = Guid.NewGuid(), ["content"] = "Test" });
        yield return ("resume_agent", new() { ["connectionSessionId"] = id, ["recoveryKey"] = new string('A', 43), ["operationId"] = Guid.NewGuid() });
        yield return ("confirm_context", new() { ["connectionSessionId"] = id, ["kind"] = "initial", ["initialDeliveryId"] = Guid.NewGuid() });
        yield return ("renew_agent_lease", new() { ["connectionSessionId"] = id });
        yield return ("report_task_failure", new() { ["connectionSessionId"] = id, ["taskId"] = Guid.NewGuid(),
            ["deliveryAttemptId"] = Guid.NewGuid(), ["failureCode"] = "agent_permanent_failure" });
        yield return ("retry_task", new() { ["connectionSessionId"] = id, ["taskId"] = Guid.NewGuid(), ["operationId"] = Guid.NewGuid() });
    }

    private sealed class AgentExceptionSwitch { public bool Enabled; }

    private sealed class ExceptionCollaboration(AgentCollaborationService inner, AgentExceptionSwitch fault) : IAgentCollaborationService
    {
        private Task<AgentOperationResult> Run(Func<Task<AgentOperationResult>> action) => fault.Enabled
            ? Task.FromException<AgentOperationResult>(new InvalidOperationException("sensitive-exception-detail")) : action();
        public Task<AgentOperationResult> JoinAsync(AgentJoinRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.JoinAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> WaitAsync(AgentWaitRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.WaitAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> ReplyAsync(AgentReplyRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.ReplyAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> GetStatusAsync(AgentStatusRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.GetStatusAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> ResumeReliableAsync(AgentResumeRequest request, McpTransportSessionKey? transportSession, string? sourceAddress = null, CancellationToken cancellationToken = default)
            => Run(() => inner.ResumeReliableAsync(request, transportSession, sourceAddress, cancellationToken));
        public Task<AgentOperationResult> ConfirmReliableAsync(AgentConfirmContextRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.ConfirmReliableAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> RenewReliableAsync(AgentLeaseRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.RenewReliableAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> ReportFailureReliableAsync(AgentFailureRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.ReportFailureReliableAsync(request, transportSession, cancellationToken));
        public Task<AgentOperationResult> RetryReliableAsync(AgentRetryRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
            => Run(() => inner.RetryReliableAsync(request, transportSession, cancellationToken));
    }
}
