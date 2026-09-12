using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task TwoBranchesOfOneHumanRootConvergeOnThirdAgentInSequenceOrder()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Converging branches");
        using var http = CreateAnonymousClient(factory);
        await using var alpha = await CreateSdkAsync(http);
        await using var beta = await CreateSdkAsync(http);
        await using var gamma = await CreateSdkAsync(http);
        var clients = new[] { alpha, beta, gamma };
        var names = new[] { "Alpha", "Beta", "Gamma" };
        var ids = new Guid[3];
        for (var i = 0; i < clients.Length; i++)
        {
            var joined = (await ProcessCallAsync(clients[i], "join_meeting", new { meetingCode = meeting.MeetingCode,
                displayName = names[i], contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
                operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
            ids[i] = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            await ProcessCallAsync(clients[i], "confirm_context", new { connectionSessionId = ids[i], kind = "initial",
                initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        }
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Alpha @Beta please review");
        var parentIds = new Guid[2];
        var replyIds = new Guid[2];
        Guid? chainId = null;
        for (var i = 0; i < 2; i++)
        {
            var root = await CrashMentionAsync(clients[i], ids[i]);
            parentIds[i] = root.GetProperty("taskId").GetGuid();
            var rootMessageId = root.GetProperty("triggerMessageId").GetGuid();
            chainId ??= rootMessageId;
            Assert.Equal(chainId, rootMessageId);
            var attempt = root.GetProperty("deliveryAttemptId").GetGuid();
            await ProcessCallAsync(clients[i], "confirm_context", new { connectionSessionId = ids[i], kind = "task", taskId = parentIds[i], deliveryAttemptId = attempt });
            var reply = (await ProcessCallAsync(clients[i], "reply_to_mention", new { connectionSessionId = ids[i], taskId = parentIds[i],
                deliveryAttemptId = attempt, replyIdempotencyKey = root.GetProperty("replyIdempotencyKey").GetGuid(), content = "@Gamma review branch" })).GetProperty("data");
            replyIds[i] = reply.GetProperty("message").GetProperty("messageId").GetGuid();
        }
        Assert.NotEqual(parentIds[0], parentIds[1]);
        var children = new HashSet<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var child = await CrashMentionAsync(gamma, ids[2]);
            var taskId = child.GetProperty("taskId").GetGuid();
            Assert.True(children.Add(taskId));
            Assert.Equal(chainId, child.GetProperty("collaboration").GetProperty("chainId").GetGuid());
            Assert.Equal(parentIds[i], child.GetProperty("collaboration").GetProperty("parentTaskId").GetGuid());
            Assert.Equal(replyIds[i], child.GetProperty("triggerMessageId").GetGuid());
            Assert.Equal(i + 2, child.GetProperty("triggerSequence").GetInt64());
            var attempt = child.GetProperty("deliveryAttemptId").GetGuid();
            await ProcessCallAsync(gamma, "confirm_context", new { connectionSessionId = ids[2], kind = "task", taskId, deliveryAttemptId = attempt });
            await ProcessCallAsync(gamma, "reply_to_mention", new { connectionSessionId = ids[2], taskId, deliveryAttemptId = attempt,
                replyIdempotencyKey = child.GetProperty("replyIdempotencyKey").GetGuid(), content = "Branch reviewed" });
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await db.AgentReplyTasks.ToArrayAsync();
        Assert.Equal(4, tasks.Length);
        Assert.All(tasks, task => { Assert.Equal(chainId, task.ChainId); Assert.Equal(AgentReplyTaskState.Replied, task.State); });
        Assert.Equal(5, await db.MeetingMessages.CountAsync());
    }
}
