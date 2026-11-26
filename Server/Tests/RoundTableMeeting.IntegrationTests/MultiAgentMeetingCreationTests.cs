using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task MeetingBoundaryCreationRollsBackWithMeetingAndReplaysWithoutReplacement()
    {
        using var factory = new TestApplicationFactory();
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER test_reject_boundary BEFORE INSERT ON AgentCollaborationBoundaries
            BEGIN SELECT RAISE(ABORT, 'test boundary insert failure'); END;
            """, cancellationToken: TestContext.Current.CancellationToken);
        var creationRequestId = Guid.NewGuid();
        async Task<HttpResponseMessage> CreateAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
            { Content = JsonContent.Create(new { creationRequestId, name = "Atomic boundary" }) };
            request.Headers.Add("Origin", "http://localhost:5173");
            return await browser.SendAsync(request);
        }
        using var failure = await CreateAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Empty(await db.Meetings.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.MeetingParticipants.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.MeetingCreationReceipts.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.AgentCollaborationBoundaries.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_reject_boundary", cancellationToken: TestContext.Current.CancellationToken);
        using var created = await CreateAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var boundary = await db.AgentCollaborationBoundaries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, boundary.AgentTriggerStartSequence);
        Assert.Equal((await db.Meetings.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ID, boundary.MeetingId);
        using var replay = await CreateAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(boundary.ID, (await db.AgentCollaborationBoundaries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ID);
        Assert.Single(await db.Meetings.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await db.MeetingCreationReceipts.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await db.MeetingParticipants.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
