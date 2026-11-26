using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Transactions;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceCliObservesActualPendingThenCompensationAndRecoversOnANewTransport(bool resumeReceipt)
    {
        using var original = new TestApplicationFactory();
        var registry = new ActivationFaultRegistry();
        var fault = new CompensationTransactionFault();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentConnectionRegistry>(); services.AddSingleton<IAgentConnectionRegistry>(registry);
                services.RemoveAll<IMeetingTransactionRunner>(); services.AddScoped<MeetingTransactionRunner>();
                services.AddScoped<IMeetingTransactionRunner, CompensationTransactionRunner>(); services.AddSingleton(fault);
            });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reference pending recovery");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var failing = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key };
        object operation = join;
        if (resumeReceipt)
        {
            var joined = (await ProcessCallAsync(first, "join_meeting", join)).GetProperty("data");
            operation = new { connectionSessionId = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid(),
                recoveryKey = key, operationId = Guid.NewGuid() };
        }
        fault.Armed = true;
        registry.Arm(0);
        var tool = resumeReceipt ? "resume_agent" : "join_meeting";
        Assert.Equal("agent_operation_unavailable", (await ActivationCallAsync(failing, tool, operation)).GetProperty("errorCode").GetString());
        Assert.True(fault.Triggered);
        AgentConnectionRegistration pending;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var c = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AgentConnectionState.Activating, c.State);
            pending = new(c.MeetingId, c.ParticipantId, c.ID, c.McpTransportBinding)
            { ConnectionGeneration = c.ConnectionGeneration, BindingVersion = c.BindingVersion, ReliabilityMode = c.ReliabilityMode };
        }
        // A real Node CLI talks HTTP/SSE to the complete production application. Only the
        // activation/compensation dependencies are fault-injected; no protocol result is faked.
        await using var bridge = await ReferenceHttpBridge.Start(factory);
        await using var cli = new ReferenceCliProcess(bridge.Address, meeting.MeetingCode, initialState: new
        { version = 1, url = new Uri(bridge.Address, "/mcp").ToString(), join, connectionSessionId = pending.ConnectionSessionId });
        await cli.WaitEvent("activation_pending");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AgentCollaborationService>();
            Assert.True(await service.ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await service.ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
        }
        var receipt = await ActivationCallAsync(failing, tool, operation);
        if (resumeReceipt) Assert.Equal("operation_superseded", receipt.GetProperty("errorCode").GetString());
        else Assert.Equal("recovery_required", receipt.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("connection_invalid", (await ActivationCallAsync(failing, "resume_agent", new
        { connectionSessionId = pending.ConnectionSessionId, recoveryKey = key, operationId = Guid.NewGuid() })).GetProperty("errorCode").GetString());
        await cli.WaitEvent("resumed");
        await cli.WaitEvent("initial_confirmed");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var c = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(pending.ConnectionSessionId, c.ID);
            Assert.Equal(pending.BindingVersion + 1, c.BindingVersion);
            Assert.Equal(AgentConnectionState.Connected, c.State);
            Assert.Equal(resumeReceipt ? 3 : 2, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker pending recovery question");
        await cli.WaitEvent("replied");
        await cli.Command("quit");
        Assert.Equal(0, await cli.Exit());
        cli.AssertPrivateOutput();
    }

    private sealed class ReferenceHttpBridge(WebApplication app, HttpClient upstream, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public static async Task<ReferenceHttpBridge> Start(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var upstream = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
            app.Run(async context =>
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
                if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(context.Request.Body);
                foreach (var header in context.Request.Headers)
                {
                    if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers)) context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Response.Headers.Remove("transfer-encoding");
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
            try { await app.StartAsync(); return new(app, upstream, new Uri(app.Urls.Single())); }
            catch { upstream.Dispose(); await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { upstream.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
