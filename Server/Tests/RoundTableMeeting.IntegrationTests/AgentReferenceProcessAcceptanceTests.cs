using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("wait_for_mention")]
    [InlineData("confirm_context:task")]
    public async Task ReferenceProcessReplaysLostProductionResponsesWithoutGeneratingOrReplyingTwice(string dropped)
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reference response loss");
        await using var agent = new ReferenceCliProcess(browser.BaseAddress!, meeting.MeetingCode,
            new() { ["AGENT_DROP_RESPONSES"] = dropped });
        await agent.WaitEvent("initial_confirmed");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker private-human-body response loss");
        await agent.WaitEvent("replied");
        await agent.Command("quit");
        Assert.Equal(0, await agent.Exit());
        Assert.Equal(1, agent.Count("fault_response_dropped"));
        Assert.Equal(1, agent.Count("replaying_request"));
        Assert.Equal(1, agent.Count("generation_started"));
        Assert.Equal(1, agent.Count("replied"));
        await using var db = host.Database();
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentReplyTaskState.Replied, task.State);
        Assert.Single(await db.AgentTaskDeliveryAttempts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        agent.AssertPrivateOutput();
    }

    [Fact]
    public async Task ReferenceProcessKeepsBlockedHeadAliveAcrossLeaseCyclesThenAcceptsTerminalRetry()
    {
        const int leaseSeconds = 6;
        await using var host = new RecoveryProcessHost(configuration: new Dictionary<string, string>
        {
            ["AgentCollaboration__ConnectionLeaseSeconds"] = leaseSeconds.ToString(),
            ["AgentCollaboration__ReliableWaitTimeoutSeconds"] = "1",
            ["AgentCollaboration__LeaseRenewIntervalSeconds"] = "1",
            ["AgentCollaboration__LeaseSafetyMarginSeconds"] = "1",
            ["AgentCollaboration__RecoveryScanIntervalSeconds"] = "1",
        });
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reference blocked renewal");
        await using var agent = new ReferenceCliProcess(browser.BaseAddress!, meeting.MeetingCode,
            new() { ["AGENT_FAULT"] = "permanent_failure" });
        await agent.WaitEvent("initial_confirmed");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker private-human-body first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second must wait");
        await agent.WaitEvent("blocked");
        async Task<(string Decision, DateTimeOffset Lease)> Facts()
        {
            await using var db = host.Database();
            var tasks = await db.AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToListAsync();
            Assert.Equal(2, tasks.Count);
            Assert.Equal(AgentReplyTaskState.Failed, tasks[0].State);
            Assert.True(tasks[0].RetryExhausted);
            Assert.Equal(AgentReplyTaskState.Queued, tasks[1].State);
            var connection = await db.AgentConnectionSessions.SingleAsync();
            Assert.Equal(AgentConnectionState.Connected, connection.State);
            Assert.Equal(1, connection.BindingVersion);
            Assert.Equal(2, await db.MeetingMessages.CountAsync());
            return (JsonSerializer.Serialize(new { tasks[0].ID, tasks[0].FailedAtUtc, tasks[0].FailureCode,
                tasks[0].RetryExhausted, tasks[0].RetryCycle, tasks[0].AutomaticRetryCount, tasks[0].NextRetryAtUtc }),
                connection.LeaseExpiresAtUtc!.Value);
        }
        var before = await Facts();
        await Task.Delay(TimeSpan.FromSeconds(leaseSeconds * 2 + 1), TestContext.Current.CancellationToken);
        var after = await Facts();
        Assert.Equal(before.Decision, after.Decision);
        Assert.True(after.Lease > before.Lease.AddSeconds(leaseSeconds));
        Assert.True(agent.Count("lease_renewed") >= 8);
        Assert.Equal(0, agent.Count("generation_started"));
        await agent.Command("retry");
        await agent.WaitEvent("retry_scheduled");
        await agent.WaitEvent("replied", 2);
        await agent.Command("quit");
        Assert.Equal(0, await agent.Exit());
        await using var final = host.Database();
        Assert.All(await final.AgentReplyTasks.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), t => Assert.Equal(AgentReplyTaskState.Replied, t.State));
        Assert.Equal(4, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        agent.AssertPrivateOutput();
    }

    [Fact]
    public async Task ReferenceProcessStopsItsOldLoopAfterAnActualOnlineTakeover()
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reference online takeover");
        await using var agent = new ReferenceCliProcess(browser.BaseAddress!, meeting.MeetingCode);
        await agent.WaitEvent("initial_confirmed");
        using var http = host.Client();
        await using var sdk = await CreateSdkAsync(http);
        var replacement = (await ProcessCallAsync(sdk, "join_meeting", new
        {
            meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 8000,
            reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43),
        })).GetProperty("data");
        Assert.Equal(1, await agent.Exit());
        Assert.Contains("[reliable-reference-agent] failed connection_superseded", agent.Output);
        Assert.Equal(0, agent.Count("resumed"));
        await using var db = host.Database();
        var current = await db.AgentConnectionSessions.SingleAsync(c => c.State == AgentConnectionState.Connected, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(replacement.GetProperty("connection").GetProperty("connectionSessionId").GetGuid(), current.ID);
        Assert.Equal(2, current.ConnectionGeneration);
        Assert.Single(await db.AgentConnectionSessions.Where(c => c.State == AgentConnectionState.Superseded).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        agent.AssertPrivateOutput();
    }

    [Fact]
    public async Task ReferenceProcessReportsFailedRemoteDeleteAndDoesNotClaimDurableOfflineState()
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reference close failure");
        await using var agent = new ReferenceCliProcess(browser.BaseAddress!, meeting.MeetingCode, failDelete: true);
        await agent.WaitEvent("initial_confirmed");
        await agent.Command("quit");
        Assert.Equal(1, await agent.Exit());
        Assert.Equal(1, agent.Count("transport_close_failed"));
        Assert.Equal(0, agent.Count("transport_closed"));
        await using var db = host.Database();
        // The injected remote 503 prevents DELETE from reaching the real host. Local exit is
        // not durable disconnect evidence; the owner remains connected until lease convergence.
        Assert.Equal(AgentConnectionState.Connected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        agent.AssertPrivateOutput();
    }

    private sealed class ReferenceCliProcess : IAsyncDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RoundTableMeetingReferenceTests"));
        private readonly string directory;
        private readonly string stateFile;
        private readonly Process process;
        private readonly ConcurrentQueue<string> output = new();
        private readonly Task stdout;
        private readonly Task stderr;
        public string[] Output => output.ToArray();

        public ReferenceCliProcess(Uri address, string meetingCode, Dictionary<string, string>? environment = null, bool failDelete = false, object? initialState = null)
        {
            directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            stateFile = Path.Combine(directory, "private", "state.json");
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceAgent");
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (failDelete)
            {
                var preload = Path.Combine(directory, "fail-delete.mjs");
                File.WriteAllText(preload, "const original = globalThis.fetch; globalThis.fetch = (url, options) => options?.method === 'DELETE' ? Promise.resolve(new Response('', { status: 503 })) : original(url, options);\n");
                start.ArgumentList.Add("--import");
                start.ArgumentList.Add(new Uri(preload).AbsoluteUri);
            }
            if (initialState is not null)
            {
                var stateModule = new Uri(Path.Combine(fixture, "reliable-state.mjs")).AbsoluteUri;
                var preload = Path.Combine(directory, "seed-state.mjs");
                File.WriteAllText(preload, "import { PrivateState } from " + JsonSerializer.Serialize(stateModule) + ";\n"
                    + "const store = new PrivateState(process.env.AGENT_STATE_FILE); await store.acquire();\n"
                    + "try { await store.save(" + JsonSerializer.Serialize(initialState) + "); } finally { await store.release(); }\n");
                start.ArgumentList.Add("--import");
                start.ArgumentList.Add(new Uri(preload).AbsoluteUri);
            }
            start.ArgumentList.Add(Path.Combine(fixture, "reliable.mjs"));
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("AGENT_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
            start.Environment["MCP_URL"] = new Uri(address, "/mcp").ToString();
            start.Environment["MEETING_CODE"] = meetingCode;
            start.Environment["AGENT_NAME"] = "Worker";
            start.Environment["AGENT_STATE_FILE"] = stateFile;
            start.Environment["AGENT_WAIT_SECONDS"] = "1";
            start.Environment["AGENT_REPLY"] = "private-reference-reply-body";
            if (environment is not null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
            process = Process.Start(start) ?? throw new InvalidOperationException("Reference CLI failed to start.");
            stdout = Drain(process.StandardOutput);
            stderr = Drain(process.StandardError);
        }
        private async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) output.Enqueue(line); }
        public int Count(string eventName) => output.Count(l => l == "[reliable-reference-agent] " + eventName || l.StartsWith("[reliable-reference-agent] " + eventName + " ", StringComparison.Ordinal));
        public async Task WaitEvent(string eventName, int count = 1)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (Count(eventName) < count)
            {
                if (process.HasExited || DateTimeOffset.UtcNow >= deadline)
                    throw new InvalidOperationException("Reference CLI did not reach event: " + eventName + "; events: " + string.Join(", ", Output));
                await Task.Delay(25);
            }
        }
        public async Task Command(string command) { await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync(); }
        public async Task<int> Exit()
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        public void AssertPrivateOutput()
        {
            using var state = JsonDocument.Parse(File.ReadAllText(stateFile));
            var text = string.Join('\n', Output);
            foreach (var property in new[] { "recoveryKey", "meetingCode", "operationId" })
                Assert.DoesNotContain(state.RootElement.GetProperty("join").GetProperty(property).GetString()!, text, StringComparison.Ordinal);
            Assert.DoesNotContain(state.RootElement.GetProperty("connectionSessionId").GetString()!, text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-human-body", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-reference-reply-body", text, StringComparison.Ordinal);
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr);
            process.Dispose();
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != root) throw new InvalidOperationException("Unexpected reference test directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
