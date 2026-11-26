using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalRecoveryWriteConflictReturnsSafeEnvelopeAndPreservesOwnershipForUnchangedRetry(bool takeover)
    {
        using var original = new TestApplicationFactory();
        var fault = new ConditionalConflict();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentRecoveryRepository>(); services.AddScoped<AgentRecoveryRepository>();
            services.AddScoped<IAgentRecoveryRepository>(sp =>
            {
                var proxy = DispatchProxy.Create<IAgentRecoveryRepository, ConditionalRecoveryProxy>();
                var implementation = (ConditionalRecoveryProxy)proxy;
                implementation.Core = sp.GetRequiredService<AgentRecoveryRepository>(); implementation.Fault = fault;
                return proxy;
            });
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Conditional recovery conflict");
        using var http = factory.CreateClient();
        await using var oldSdk = await CreateSdkAsync(http);
        await using var newSdk = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new { meetingCode = meeting.MeetingCode, displayName = "Worker",
            contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var name = takeover ? "join_meeting" : "resume_agent";
        object args = takeover ? new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key }
            : new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() };
        var schema = JsonSchema.FromText((await newSdk.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(t => t.Name == name).ProtocolTool.OutputSchema!.Value.GetRawText());
        async Task<string> Facts()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new { Connection = await db.AgentConnectionSessions.SingleAsync(),
                Cursor = await db.AgentContextCursors.SingleAsync(), Initial = await db.AgentInitialContextDeliveries.SingleAsync(),
                Receipts = await db.AgentRecoveryOperations.CountAsync() });
        }
        var before = await Facts();
        fault.Target = takeover ? "SupersedeAsync" : "RebindAsync";
        var result = await ActivationCallAsync(newSdk, name, args);
        CheckPrivateErrorEnvelope(result, schema, "agent_operation_conflict", "stop", key, meeting.MeetingCode);
        Assert.Equal(1, fault.Hits);
        Assert.Equal(before, await Facts());
        fault.Target = null;
        Assert.True((await ActivationCallAsync(newSdk, name, args)).GetProperty("succeeded").GetBoolean());
        CheckPrivateLog(original, key, meeting.MeetingCode);
    }

    public sealed class ConditionalConflict
    {
        public string? Target { get; set; }
        public int Hits { get; set; }
    }
    public class ConditionalRecoveryProxy : DispatchProxy
    {
        public IAgentRecoveryRepository Core { get; set; } = null!;
        public ConditionalConflict Fault { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == Fault.Target) { Fault.Hits++; return Task.FromResult(false); }
            try { return targetMethod.Invoke(Core, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
