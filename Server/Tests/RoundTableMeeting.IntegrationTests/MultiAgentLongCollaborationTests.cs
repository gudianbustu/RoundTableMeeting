using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ThreeSdkAgentsDrainMoreThanFortyDerivedTasksIncludingBranchesAndSelfMentions()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Finite collaboration workload");
        using var http = CreateAnonymousClient(factory);
        await using var alpha = await CreateSdkAsync(http);
        await using var beta = await CreateSdkAsync(http);
        await using var gamma = await CreateSdkAsync(http);
        var clients = new[] { alpha, beta, gamma };
        var names = new[] { "Alpha", "Beta", "Gamma" };
        var ids = new Guid[3];
        for (var i = 0; i < clients.Length; i++)
        {
            var joined = (await ProcessCallAsync(clients[i], "join_meeting", new
            {
                meetingCode = meeting.MeetingCode, displayName = names[i], contextCharacterLimit = 12000,
                reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43)
            })).GetProperty("data");
            ids[i] = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            await ProcessCallAsync(clients[i], "confirm_context", new { connectionSessionId = ids[i], kind = "initial",
                initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        }
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Alpha begin");
        var pending = new Queue<(int Agent, Guid? Parent)>();
        pending.Enqueue((0, null));
        var processed = new HashSet<Guid>();
        Guid? chainId = null;
        long[] lastSequence = [0, 0, 0];
        var rounds = 0;
        while (pending.TryDequeue(out var work))
        {
            for (var i = 0; i < clients.Length; i++)
                await ProcessCallAsync(clients[i], "renew_agent_lease", new { connectionSessionId = ids[i] });
            var task = await CrashMentionAsync(clients[work.Agent], ids[work.Agent]);
            var taskId = task.GetProperty("taskId").GetGuid();
            Assert.True(processed.Add(taskId));
            var source = task.GetProperty("collaboration");
            chainId ??= source.GetProperty("chainId").GetGuid();
            Assert.Equal(chainId, source.GetProperty("chainId").GetGuid());
            Assert.Equal(work.Parent, source.GetProperty("parentTaskId").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null : source.GetProperty("parentTaskId").GetGuid());
            var sequence = task.GetProperty("triggerSequence").GetInt64();
            Assert.True(sequence > lastSequence[work.Agent]);
            lastSequence[work.Agent] = sequence;
            var attemptId = task.GetProperty("deliveryAttemptId").GetGuid();
            await ProcessCallAsync(clients[work.Agent], "confirm_context", new { connectionSessionId = ids[work.Agent],
                kind = "task", taskId, deliveryAttemptId = attemptId });

            // Only the test host bounds this workload; the platform has no round limit.
            int[] targets = rounds < 40
                ? rounds % 7 == 0 ? [(work.Agent + 1) % 3, (work.Agent + 2) % 3]
                    : [rounds % 5 == 0 ? work.Agent : (work.Agent + 1) % 3]
                : [];
            var content = targets.Length == 0 ? "Workload finished by test host."
                : string.Join(" ", targets.Select(index => "@" + names[index])) + " continue";
            await ProcessCallAsync(clients[work.Agent], "reply_to_mention", new { connectionSessionId = ids[work.Agent],
                taskId, deliveryAttemptId = attemptId, replyIdempotencyKey = task.GetProperty("replyIdempotencyKey").GetGuid(), content });
            foreach (var target in targets) pending.Enqueue((target, taskId));
            rounds++;
        }
        Assert.True(rounds > 40);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await db.AgentReplyTasks.Where(x => x.MeetingId == meeting.MeetingId).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(rounds, tasks.Length);
        Assert.All(tasks, task => { Assert.Equal(AgentReplyTaskState.Replied, task.State); Assert.Equal(chainId, task.ChainId); });
        Assert.Single(tasks, task => task.ParentTaskId is null);
        Assert.Contains(tasks.GroupBy(task => task.ParentTaskId), group => group.Key is not null && group.Count() == 2);
        Assert.Contains(tasks, task => task.ParentTaskId is Guid parentId
            && tasks.Single(parent => parent.ID == parentId).AgentParticipantId == task.AgentParticipantId);
        Assert.Equal(rounds + 1, await db.MeetingMessages.CountAsync(x => x.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
