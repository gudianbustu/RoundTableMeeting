using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingUnreadActivityTests
{
    [Fact]
    public async Task CountsOnlyIncomingVisibleMessagesAfterEachDeviceWatermark()
    {
        using var original = new TestApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Authentication:SeedAccounts:2:Username"] = "carol", ["Authentication:SeedAccounts:2:Nickname"] = "Carol",
                ["Authentication:SeedAccounts:2:Password"] = "test-password-carol"
            })));
        using var owner = factory.CreateClient();
        using var member = factory.CreateClient();
        using var peer = factory.CreateClient();
        foreach (var (client, username) in new[] { (owner, "alice"), (member, "bob"), (peer, "carol") })
        {
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
            using var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "test-password-" + username }, cancellationToken: TestContext.Current.CancellationToken);
            res.EnsureSuccessStatusCode();
        }
        async Task<(Guid Id, Guid Owner, Guid Member, Guid Peer)> Create(string name)
        {
            using var res = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name });
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            var room = body.GetProperty("meeting");
            var code = body.GetProperty("invitation").GetProperty("meetingCode").GetString();
            async Task<Guid> Join(HttpClient client, string displayName)
            {
                using var joined = await client.PostAsJsonAsync("/api/meetings/join", new { meetingCode = code, displayName });
                joined.EnsureSuccessStatusCode();
                return (await joined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
            }
            return (room.GetProperty("meetingId").GetGuid(), room.GetProperty("currentParticipant").GetProperty("participantId").GetGuid(),
                await Join(member, "乙"), await Join(peer, "丙"));
        }
        async Task Send(HttpClient client, Guid id, Guid? recipient = null)
        {
            using var response = await client.PostAsJsonAsync($"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(),
                content = "样本", visibility = recipient.HasValue ? "private" : "public", recipientParticipantId = recipient });
            response.EnsureSuccessStatusCode();
        }
        async Task<UnreadActivityDto> Activity(Guid id, long? after = null) =>
            (await member.GetFromJsonAsync<UnreadActivityDto>($"/api/meetings/{id}/unread-activity" +
                (after.HasValue ? $"?afterSequence={after}" : "")))!;
        var a = await Create("会场甲");
        var b = await Create("会场乙");
        var start = await Activity(a.Id);
        var other = await Activity(b.Id);
        Assert.Equal(a.Member, start.ParticipantId);
        Assert.Empty(start.Scopes);
        await Send(owner, a.Id);
        await Send(peer, a.Id);
        await Send(member, a.Id);
        await Send(owner, a.Id, a.Member);
        await Send(peer, a.Id, a.Member);
        await Send(owner, a.Id, a.Peer);
        await Send(owner, b.Id);
        var snapshot = await Activity(a.Id, start.ThroughSequence);
        Assert.Equal(3, snapshot.Scopes.Count);
        Assert.Equal(2L, Assert.Single(snapshot.Scopes, x => x.Scope == "public" && x.PeerParticipantId is null).Count);
        foreach (var id in new[] { a.Owner, a.Peer })
            Assert.Equal(1L, Assert.Single(snapshot.Scopes, x => x.Scope == "private" && x.PeerParticipantId == id).Count);
        Assert.Empty((await Activity(a.Id, snapshot.ThroughSequence)).Scopes);
        Assert.Equal(1L, Assert.Single((await Activity(b.Id, other.ThroughSequence)).Scopes).Count);
        var replay = await Activity(a.Id, start.ThroughSequence);
        Assert.Equal(snapshot.ThroughSequence, replay.ThroughSequence);
        Assert.Equal(snapshot.Scopes.Sum(x => x.Count), replay.Scopes.Sum(x => x.Count));
        using var removed = await owner.PostAsJsonAsync($"/api/meetings/{a.Id}/participants/{a.Member}/remove", new { operationId = Guid.NewGuid() }, cancellationToken: TestContext.Current.CancellationToken);
        removed.EnsureSuccessStatusCode();
        using var forbidden = await member.GetAsync($"/api/meetings/{a.Id}/unread-activity?afterSequence=0", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }
}
