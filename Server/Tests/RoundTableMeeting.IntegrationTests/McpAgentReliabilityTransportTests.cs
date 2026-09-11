using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task SdkConcurrentBoundRequestCompletesBeforeSuspendedWaitAndCancellationKeepsSession()
    {
        using var original = new TestApplicationFactory();
        var signals = new ObservedTaskSignals();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>();
            services.AddSingleton<IAgentTaskSignalRegistry>(signals);
        }));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "SDK concurrent requests");
        using var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "SdkWorker", ["contextCharacterLimit"] = 12000
        }));
        Assert.True(joined.GetProperty("succeeded").GetBoolean());
        var connectionId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var args = new Dictionary<string, object?> { ["connectionSessionId"] = connectionId };
        using var cancellation = new CancellationTokenSource();
        var waitId = new RequestId("suspended-wait");
        var wait = sdk.SendRequestAsync<CallToolRequestParams, CallToolResult>("tools/call", new()
        {
            Name = "wait_for_mention",
            Arguments = new Dictionary<string, JsonElement> { ["connectionSessionId"] = JsonSerializer.SerializeToElement(connectionId) }
        }, requestId: waitId, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Exercise the actual SDK dispatcher, scoped transport accessor and database while wait is held.
            var status = SdkPayload(await sdk.CallToolAsync("get_agent_status", args).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(status.GetProperty("succeeded").GetBoolean());
            Assert.Equal(connectionId, status.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
            Assert.False(wait.IsCompleted);
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        // Aborting the HTTP call is not proof that the server cancelled its preserved request.
        await sdk.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = waitId });
        await signals.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var afterCancellation = SdkPayload(await sdk.CallToolAsync("get_agent_status", args));
        Assert.True(afterCancellation.GetProperty("succeeded").GetBoolean());
        Assert.Equal("connected", afterCancellation.GetProperty("data").GetProperty("connection").GetProperty("state").GetString());
    }

    [Fact]
    public async Task SdkSessionCloseEndsSuspendedWaitAndRunsTheActualDisconnectHook()
    {
        using var original = new TestApplicationFactory();
        var signals = new ObservedTaskSignals();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>();
            services.AddSingleton<IAgentTaskSignalRegistry>(signals);
        }));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "SDK close while waiting");
        using var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var sdk = await CreateSdkAsync(http);
        Task<CallToolResult>? wait = null;
        try
        {
            var joined = SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
            {
                ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "SdkWorker", ["contextCharacterLimit"] = 12000
            }));
            var connectionId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            wait = sdk.CallToolAsync("wait_for_mention", new Dictionary<string, object?> { ["connectionSessionId"] = connectionId }).AsTask();
            await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(wait.IsCompleted);
            await sdk.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await signals.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await EventuallyAsync(factory, async db => await db.AgentConnectionSessions.AnyAsync(item =>
                item.ID == connectionId && item.State == AgentConnectionState.Disconnected));
            Assert.Empty(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId));
        }
        finally
        {
            await sdk.DisposeAsync();
            if (wait is not null)
            {
                // A closed transport can surface cancellation or a protocol transport error; always settle the request.
                var completion = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(wait, completion);
                _ = await Record.ExceptionAsync(async () => await wait);
            }
        }
    }

    private static Task<McpClient> CreateSdkAsync(HttpClient http)
        => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, http));

    private static JsonElement SdkPayload(CallToolResult result)
    {
        Assert.False(result.IsError == true);
        var payload = result.StructuredContent is JsonElement structured ? structured
            : JsonSerializer.Deserialize<JsonElement>(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        AssertBusinessErrorSchema(payload);
        return payload;
    }

    private sealed class ObservedTaskSignals : IAgentTaskSignalRegistry
    {
        private readonly AgentTaskSignalRegistry inner = new(new AgentCollaborationOptions());
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Signal(Guid meetingId, Guid agentParticipantId) => inner.Signal(meetingId, agentParticipantId);
        public void TerminateMeeting(Guid meetingId) => inner.TerminateMeeting(meetingId);
        public async Task<bool> WaitAsync(Guid meetingId, Guid agentParticipantId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var pending = inner.WaitAsync(meetingId, agentParticipantId, timeout, cancellationToken);
            if (pending.IsCompleted) return await pending;
            Entered.TrySetResult();
            try { return await pending; }
            finally { Exited.TrySetResult(); }
        }
    }
}
