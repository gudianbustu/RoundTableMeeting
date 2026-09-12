using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedMentionPreservesIndependentBudgetsAndMixedHistory(bool oversizedTrigger)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Independent collaboration context");
        using var http = CreateAnonymousClient(factory);
        await using var alpha = await CreateSdkAsync(http);
        await using var beta = await CreateSdkAsync(http);
        var clients = new[] { alpha, beta };
        var ids = new Guid[2];
        for (var i = 0; i < 2; i++)
        {
            var joined = (await ProcessCallAsync(clients[i], "join_meeting", new { meetingCode = meeting.MeetingCode,
                displayName = i == 0 ? "Alpha" : "Beta", contextCharacterLimit = i == 0 ? 1000 : 12000,
                reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
            ids[i] = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            await ProcessCallAsync(clients[i], "confirm_context", new { connectionSessionId = ids[i], kind = "initial",
                initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        }
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Alpha first");
        var root = await CrashMentionAsync(alpha, ids[0]);
        var taskId = root.GetProperty("taskId").GetGuid();
        var attempt = root.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(alpha, "confirm_context", new { connectionSessionId = ids[0], kind = "task", taskId, deliveryAttemptId = attempt });
        await ProcessCallAsync(alpha, "reply_to_mention", new { connectionSessionId = ids[0], taskId, deliveryAttemptId = attempt,
            replyIdempotencyKey = root.GetProperty("replyIdempotencyKey").GetGuid(), content = "ordinary Agent history" });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            db.Add(MeetingMessage.CreateSystem(meeting.MeetingId, 3, "System", "ordinary System history", Guid.NewGuid(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await PostHumanMessageAsync(browser, meeting.MeetingId, new string('中', 5000));
        var content = "@Alpha @Beta " + (oversizedTrigger ? new string('文', 2000) : "shared trigger");
        await PostHumanMessageAsync(browser, meeting.MeetingId, content);
        var small = await CrashMentionAsync(alpha, ids[0]);
        var large = await CrashMentionAsync(beta, ids[1]);
        var a = small.GetProperty("context");
        var b = large.GetProperty("context");
        Assert.Equal(2, a.GetProperty("candidateFirstSequence").GetInt64());
        Assert.Equal(1, b.GetProperty("candidateFirstSequence").GetInt64());
        Assert.Equal(5, a.GetProperty("candidateLastSequence").GetInt64());
        Assert.Equal(5, b.GetProperty("candidateLastSequence").GetInt64());
        Assert.Equal(1000, a.GetProperty("declaredCharacterLimit").GetInt32());
        Assert.Equal(12000, b.GetProperty("declaredCharacterLimit").GetInt32());
        Assert.True(a.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, a.GetProperty("truncatedMessageCount").GetInt32());
        Assert.Equal(oversizedTrigger, a.GetProperty("overLimit").GetBoolean());
        Assert.Equal(content, Assert.Single(a.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
        Assert.False(b.GetProperty("truncated").GetBoolean());
        Assert.False(b.GetProperty("overLimit").GetBoolean());
        var history = b.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, history.Select(message => message.GetProperty("sequence").GetInt64()));
        Assert.Equal("ordinary Agent history", history[1].GetProperty("content").GetString());
        Assert.Equal("ordinary System history", history[2].GetProperty("content").GetString());
        Assert.Equal(content, history[4].GetProperty("content").GetString());
        Assert.Equal(small.GetProperty("triggerMessageId").GetGuid(), large.GetProperty("triggerMessageId").GetGuid());
        Assert.NotEqual(small.GetProperty("taskId").GetGuid(), large.GetProperty("taskId").GetGuid());
        Assert.Equal(small.GetProperty("collaboration").GetProperty("chainId").GetGuid(), large.GetProperty("collaboration").GetProperty("chainId").GetGuid());
    }
}
