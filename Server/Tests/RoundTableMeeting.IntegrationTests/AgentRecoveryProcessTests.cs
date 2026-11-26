using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealServerCrashAndResumePreserveAnExistingFailureDecision(bool permanent)
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Process failure decision");
        using var oldHttp = host.Client();
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new
        {
            meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key,
        })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Failure decision must survive");
        var mention = (await ProcessCallAsync(oldSdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 5 })).GetProperty("data");
        var taskId = mention.GetProperty("taskId").GetGuid();
        var attemptId = mention.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        await ProcessCallAsync(oldSdk, "report_task_failure", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId,
            failureCode = permanent ? "agent_permanent_failure" : "agent_transient_failure" });
        async Task<string> ReadDecision()
        {
            await using var db = host.Database();
            var task = await db.AgentReplyTasks.SingleAsync();
            Assert.Equal(AgentReplyTaskState.Failed, task.State);
            Assert.Equal(permanent, task.RetryExhausted);
            Assert.Equal(permanent, task.NextRetryAtUtc is null);
            return JsonSerializer.Serialize(new { task.ID, task.FailedAtUtc, task.FailureCode, task.CurrentAttemptId,
                task.RetryCycle, task.AutomaticRetryCount, task.RetryExhausted, task.NextRetryAtUtc });
        }
        var decision = await ReadDecision();
        await host.CrashAsync();
        await host.StartAsync();
        Assert.Equal(decision, await ReadDecision());
        using var newHttp = host.Client();
        await using var newSdk = await CreateSdkAsync(newHttp);
        await ProcessCallAsync(newSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        Assert.Equal(decision, await ReadDecision());
        if (permanent)
        {
            var blocked = SdkPayload(await newSdk.CallToolAsync("wait_for_mention", new Dictionary<string, object?>
                { ["connectionSessionId"] = id, ["waitTimeoutSeconds"] = 1 }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(blocked.GetProperty("succeeded").GetBoolean());
            Assert.Equal("retry_exhausted", blocked.GetProperty("errorCode").GetString());
            Assert.Equal(decision, await ReadDecision());
        }
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("delivered")]
    [InlineData("processing")]
    [InlineData("replied")]
    public async Task RealServerCrashPreservesContextAndRecoversTasksWithoutDuplicateReplies(string cut)
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Process recovery");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "Initial history");
        var oldSession = new ProcessSessionCapture();
        using var oldHttp = host.Client(handler: oldSession);
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new
        {
            meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key,
        })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var initialId = joined.GetProperty("initialDeliveryId").GetGuid();
        JsonElement mention = default;
        Guid taskId = default;
        Guid attemptId = default;
        Guid replyKey = default;
        if (cut != "initial")
        {
            await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = initialId });
            await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Process crash question");
            mention = (await ProcessCallAsync(oldSdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 5 })).GetProperty("data");
            Assert.Equal("mention", mention.GetProperty("kind").GetString());
            taskId = mention.GetProperty("taskId").GetGuid();
            attemptId = mention.GetProperty("deliveryAttemptId").GetGuid();
            replyKey = mention.GetProperty("replyIdempotencyKey").GetGuid();
            if (cut is "processing" or "replied")
                await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
            if (cut == "replied")
                await ProcessCallAsync(oldSdk, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId,
                    replyIdempotencyKey = replyKey, content = "Process recovery reply" });
        }
        // Verify the durable cut before killing the real process; no graceful DELETE runs.
        await using (var before = host.Database())
        {
            Assert.Equal(AgentConnectionState.Connected, (await before.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
            if (cut != "initial") Assert.Equal(cut, (await before.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State.ToString().ToLowerInvariant());
        }
        await host.CrashAsync();
        await host.StartAsync();
        Assert.NotNull(oldSession.SessionId);
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
        {
            staleRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", oldSession.SessionId);
            staleRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            staleRequest.Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", id = 1001,
                method = "tools/call", @params = new { name = "get_agent_status", arguments = new { connectionSessionId = id } } });
            using var staleResponse = await oldHttp.SendAsync(staleRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, staleResponse.StatusCode);
        }
        await using (var after = host.Database())
        {
            var connection = await after.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(id, connection.ID);
            Assert.Equal(AgentConnectionState.Disconnected, connection.State);
            Assert.Equal("server_restart", connection.DisconnectReason);
            var initial = await after.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(initialId, initial.ID);
            if (cut is "delivered" or "processing")
            {
                var task = await after.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(taskId, task.ID);
                Assert.Equal(AgentReplyTaskState.Failed, task.State);
                Assert.Equal("server_restart", task.FailureCode);
                Assert.Equal(0, task.AutomaticRetryCount);
                Assert.False(task.RetryExhausted);
                Assert.Equal(attemptId, task.CurrentAttemptId);
                Assert.NotNull((await after.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).InvalidatedAtUtc);
            }
            if (cut == "replied") Assert.Equal(AgentReplyTaskState.Replied, (await after.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        }
        using var newHttp = host.Client();
        await using var newSdk = await CreateSdkAsync(newHttp);
        var resumed = (await ProcessCallAsync(newSdk, "resume_agent", new { connectionSessionId = id,
            recoveryKey = key, operationId = Guid.NewGuid() })).GetProperty("data");
        Assert.Equal(id, resumed.GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
        Assert.Equal(2, resumed.GetProperty("connection").GetProperty("bindingVersion").GetInt64());
        if (cut == "initial")
        {
            Assert.Equal(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined.GetProperty("initialContext").GetRawText())),
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(resumed.GetProperty("initialContext").GetRawText())));
            await ProcessCallAsync(newSdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = initialId });
        }
        else if (cut == "replied")
        {
            var replay = await ProcessCallAsync(newSdk, "reply_to_mention", new { connectionSessionId = id, taskId,
                deliveryAttemptId = attemptId, replyIdempotencyKey = replyKey, content = "Process recovery reply" });
            Assert.True(replay.GetProperty("data").GetProperty("replayed").GetBoolean());
        }
        else
        {
            JsonElement next = default;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            do
            {
                var response = SdkPayload(await newSdk.CallToolAsync("wait_for_mention", new Dictionary<string, object?>
                    { ["connectionSessionId"] = id, ["waitTimeoutSeconds"] = 5 }, cancellationToken: TestContext.Current.CancellationToken));
                if (response.GetProperty("succeeded").GetBoolean()) { next = response.GetProperty("data"); break; }
                Assert.Equal("retry_pending", response.GetProperty("errorCode").GetString());
                await Task.Delay(50, TestContext.Current.CancellationToken);
            } while (DateTimeOffset.UtcNow < deadline);
            Assert.Equal("mention", next.GetProperty("kind").GetString());
            Assert.Equal(taskId, next.GetProperty("taskId").GetGuid());
            Assert.Equal(replyKey, next.GetProperty("replyIdempotencyKey").GetGuid());
            var nextAttempt = next.GetProperty("deliveryAttemptId").GetGuid();
            Assert.NotEqual(attemptId, nextAttempt);
            var stale = SdkPayload(await newSdk.CallToolAsync("reply_to_mention", new Dictionary<string, object?>
                { ["connectionSessionId"] = id, ["taskId"] = taskId, ["deliveryAttemptId"] = attemptId,
                    ["replyIdempotencyKey"] = replyKey, ["content"] = "Process recovery reply" }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(stale.GetProperty("succeeded").GetBoolean());
            await ProcessCallAsync(newSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = nextAttempt });
            await ProcessCallAsync(newSdk, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = nextAttempt,
                replyIdempotencyKey = replyKey, content = "Process recovery reply" });
        }
        await using var final = host.Database();
        Assert.Equal(cut == "initial" ? 1 : 3, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await final.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, (await final.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConnectionGeneration);
    }

    private static async Task<JsonElement> ProcessCallAsync(McpClient sdk, string name, object args)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = SdkPayload(await sdk.CallToolAsync(name, arguments).AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(result.GetProperty("succeeded").GetBoolean(), name + ": " + result.GetProperty("errorCode").ToString());
        return result;
    }

    private sealed class ProcessSessionCapture : DelegatingHandler
    {
        public string? SessionId { get; private set; }
        public ProcessSessionCapture() : base(new HttpClientHandler { UseCookies = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) SessionId ??= values.Single();
            return response;
        }
    }

    private sealed class RecoveryProcessHost : IAsyncDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RoundTableMeetingProcessTests"));
        private readonly string directory;
        private readonly Uri address;
        private Process? process;
        private Task? stdout;
        private Task? stderr;
        private readonly bool faultInjection;
        private readonly IReadOnlyDictionary<string, string>? configuration;
        public RecoveryProcessHost(bool faultInjection = false, IReadOnlyDictionary<string, string>? configuration = null)
        {
            this.faultInjection = faultInjection;
            this.configuration = configuration;
            directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            address = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        public HttpClient Client(bool cookies = false, HttpMessageHandler? handler = null) => new(handler ?? new HttpClientHandler { UseCookies = cookies })
            { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
        public RoundTableMeetingDbContext Database() => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "state.db")};Pooling=False;Foreign Keys=True").Options);
        public async Task StartAsync()
        {
            File.Delete(Path.Combine(directory, "crash-arm"));
            File.Delete(Path.Combine(directory, "crash-reached"));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            if (faultInjection)
            {
                // Run the production entry point with the test dependency graph so the optional
                // hosting-startup probe can be loaded. The published application never contains it.
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add("--depsfile");
                start.ArgumentList.Add(Path.ChangeExtension(typeof(McpAgentContractTests).Assembly.Location, ".deps.json"));
                start.ArgumentList.Add("--runtimeconfig");
                start.ArgumentList.Add(Path.ChangeExtension(typeof(McpAgentContractTests).Assembly.Location, ".runtimeconfig.json"));
                start.Environment["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = typeof(McpAgentContractTests).Assembly.GetName().Name;
                start.Environment["RTM_TEST_CRASH_DIRECTORY"] = directory;
            }
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = address.ToString();
            start.Environment["Database__ConnectionString"] = $"Data Source={Path.Combine(directory, "state.db")};Pooling=False;Foreign Keys=True";
            start.Environment["Authentication__AllowedOrigins__0"] = "http://localhost:5173";
            start.Environment["Authentication__InitialAdminUsername"] = "alice";
            start.Environment["Authentication__SeedAccounts__0__Username"] = "alice";
            start.Environment["Authentication__SeedAccounts__0__Nickname"] = "Alice";
            start.Environment["Authentication__SeedAccounts__0__Password"] = "test-password-alice";
            if (configuration is not null)
                foreach (var entry in configuration) start.Environment[entry.Key] = entry.Value;
            process = Process.Start(start) ?? throw new InvalidOperationException("Process host did not start.");
            // Drain without retaining request/session data or including it in test failures.
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            using var http = Client();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException("Process host exited: " + process.ExitCode);
                try { using var response = await http.GetAsync("/api/auth/me"); if (response.StatusCode == HttpStatusCode.Unauthorized) return; }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            throw new TimeoutException("Process host was not ready.");
        }
        public void ArmCrash(string boundary, bool afterCommit)
        {
            if (!faultInjection) throw new InvalidOperationException("The test fault probe is not enabled.");
            File.WriteAllText(Path.Combine(directory, "crash-arm"), boundary + (afterCommit ? ":after" : ":before"));
        }
        public async Task WaitForCrashPointAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(Path.Combine(directory, "crash-reached"))) return;
                if (process is null || process.HasExited) throw new InvalidOperationException("Host exited before the requested crash point.");
                await Task.Delay(20);
            }
            throw new TimeoutException("The requested transaction crash point was not reached.");
        }
        private static async Task DrainAsync(StreamReader reader) { while (await reader.ReadLineAsync() is not null) { } }
        public async Task CrashAsync()
        {
            if (process is null) return;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout!, stderr!);
            process.Dispose();
            process = null;
        }
        public async ValueTask DisposeAsync()
        {
            await CrashAsync();
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != root) throw new InvalidOperationException("Unexpected cleanup directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
