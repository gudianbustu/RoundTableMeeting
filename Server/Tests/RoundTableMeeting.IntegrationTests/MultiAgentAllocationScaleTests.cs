using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using MessageAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;
using RoundTableMeeting.Repository.Sqlite.Database;
using Xunit.Abstractions;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MultiAgentAllocationScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TenAgentsWithTenThousandHistoricalTasksDoNotMaterializeHistoryForOneHundredEvents()
    {
        var probe = new AllocationProbe();
        using var original = new TestApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.ConfigureDbContext<RoundTableMeetingDbContext>(options => options.AddInterceptors(probe));
        }));
        using var client = factory.CreateClient();
        var small = await RunAsync(1);
        var large = await RunAsync(10000);
        Assert.Equal(small.Commands, large.Commands);
        Assert.Equal(small.TaskRows, large.TaskRows);
        Assert.InRange(large.TaskRows, 1, 2100);
        output.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; SQLite=real file, FK enabled; targets=10; events=100");
        output.WriteLine($"Small: history/target=1, SQL readers={small.Commands}, task rows={small.TaskRows}, consumer ms={small.Elapsed.TotalMilliseconds:F0}");
        output.WriteLine($"Large: history/target=10000, SQL readers={large.Commands}, task rows={large.TaskRows}, consumer ms={large.Elapsed.TotalMilliseconds:F0}");
        Assert.True(large.Elapsed.TotalMilliseconds <= small.Elapsed.TotalMilliseconds * 5 + 2000,
            "Large history caused excessive database scanning despite bounded materialization; compare the recorded timings.");

        async Task<(int Commands, int TaskRows, TimeSpan Elapsed)> RunAsync(int history)
        {
            var now = DateTimeOffset.UtcNow;
            Guid meetingId;
            Guid humanId;
            MeetingParticipant[] agents;
            await using (var seedScope = factory.Services.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                var user = UserAccount.Create("scale-" + Guid.NewGuid().ToString("N"), "Owner");
                user.InitializePasswordHash("fixture");
                var meeting = Meeting.Create("Allocation scale", null, user.ID, now);
                var human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", now);
                meetingId = meeting.ID; humanId = human.ID;
                agents = Enumerable.Range(0, 10).Select(i => MeetingParticipant.CreateAgent(meetingId, "Agent" + i, now)).ToArray();
                db.AddRange(user, meeting, human, AgentCollaborationBoundary.Create(meetingId, 0));
                db.AddRange(agents);
                db.AddRange(agents.Select(agent => AgentContextCursor.Create(meetingId, agent.ID, history, now)));
                await db.SaveChangesAsync();
                var content = string.Join(" ", agents.Select(agent => "@" + agent.DisplayName));
                for (var start = 1; start <= history; start += 250)
                {
                    var messages = Enumerable.Range(start, Math.Min(250, history - start + 1))
                        .Select(sequence => MeetingMessage.CreateParticipant(meetingId, sequence, humanId,
                            MessageAuthorKind.Human, "Owner", content, Guid.NewGuid(), now)).ToArray();
                    db.AddRange(messages);
                    var mentions = messages.SelectMany(message => agents.Select((agent, index) =>
                        MessageMention.Create(meetingId, message.ID, agent.ID, agent.DisplayName, index * 8, 7))).ToArray();
                    db.AddRange(mentions);
                    await db.SaveChangesAsync();
                    var sequences = messages.ToDictionary(message => message.ID, message => message.Sequence);
                    foreach (var mention in mentions)
                    {
                        var sequence = sequences[mention.MessageId];
                        var task = AgentReplyTask.Create(Guid.NewGuid(), meetingId, mention.TargetParticipantId,
                            mention.MessageId, sequence, mention.ID, MessageAuthorKind.Human, humanId, sequence - 1, Guid.NewGuid(), now);
                        task.Cancel(now);
                        db.Add(task);
                    }
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear();
                }
                Assert.Equal(history * 10, await db.AgentReplyTasks.CountAsync(task => task.MeetingId == meetingId));
            }
            var elapsed = TimeSpan.Zero;
            var commands = 0;
            var taskRows = 0;
            for (var index = 1; index <= 100; index++)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                var content = string.Join(" ", agents.Select(agent => "@" + agent.DisplayName));
                var message = MeetingMessage.CreateParticipant(meetingId, history + index, humanId,
                    MessageAuthorKind.Human, "Owner", content, Guid.NewGuid(), now);
                db.Add(message);
                db.AddRange(agents.Select((agent, position) => MessageMention.Create(meetingId, message.ID, agent.ID,
                    agent.DisplayName, position * 8, 7)));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                probe.Enabled = true;
                probe.Commands = 0; probe.TaskRows = 0;
                var watch = Stopwatch.StartNew();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<AgentMentionTaskConsumer>().ConsumeAsync(
                        new MessageAppendedEnvelope(Guid.NewGuid(), message.ID, meetingId, message.Sequence, now));
                }
                finally { watch.Stop(); probe.Enabled = false; }
                elapsed += watch.Elapsed; commands += probe.Commands; taskRows += probe.TaskRows;
            }
            await using var finalScope = factory.Services.CreateAsyncScope();
            var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal((history + 100) * 10, await final.AgentReplyTasks.CountAsync(task => task.MeetingId == meetingId));
            Assert.All(await final.AgentContextCursors.Where(cursor => cursor.MeetingId == meetingId).ToArrayAsync(),
                cursor => Assert.Equal(history + 100, cursor.AllocatedThroughSequence));
            Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
            return (commands, taskRows, elapsed);
        }
    }

    private sealed class AllocationProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        public bool Enabled;
        public int Commands;
        public int TaskRows;
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Enabled && entity is AgentReplyTask) TaskRows++;
            return entity;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Commands++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
