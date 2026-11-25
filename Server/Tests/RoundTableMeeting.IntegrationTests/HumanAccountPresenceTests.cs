using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed class HumanAccountPresenceTests
{
    [Fact]
    public async Task HomepagePresenceSpansJoinedMeetingsUntilLastConnectionExpires()
    {
        using var original = new TestApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["RealtimeChat:PresenceDisconnectGraceSeconds"] = "1" })));
        using var browser = factory.CreateClient();
        browser.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await browser.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        async Task<(Guid Meeting, Guid Participant)> Create(string name)
        {
            using var response = await browser.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name });
            response.EnsureSuccessStatusCode();
            var meeting = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("meeting");
            return (meeting.GetProperty("meetingId").GetGuid(), meeting.GetProperty("currentParticipant").GetProperty("participantId").GetGuid());
        }
        var a = await Create("A");
        using var scope = factory.Services.CreateScope();
        var member = Assert.Single(await scope.ServiceProvider.GetRequiredService<IMeetingParticipantRepository>().ListAsync(a.Meeting));
        var user = member.UserId!.Value;
        var presence = factory.Services.GetRequiredService<HumanAccountPresence>();
        await presence.ConnectAsync(user, "home");
        var b = await Create("B");
        Assert.Contains(a.Participant, await presence.OnlineParticipantsAsync(a.Meeting, default));
        Assert.Contains(b.Participant, await presence.OnlineParticipantsAsync(b.Meeting, default));
        await presence.ConnectAsync(user, "settings");
        await presence.DisconnectAsync(user, "home");
        Assert.True(presence.IsOnline(user));
        await presence.DisconnectAsync(user, "settings");
        Assert.True(presence.IsOnline(user));
        await presence.ConnectAsync(user, "restored");
        await Task.Delay(1200);
        Assert.True(presence.IsOnline(user));
        await presence.DisconnectAsync(user, "restored");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (presence.IsOnline(user)) await Task.Delay(20, limit.Token);
        Assert.Empty(await presence.OnlineParticipantsAsync(a.Meeting, default));
        Assert.Empty(await presence.OnlineParticipantsAsync(b.Meeting, default));
    }
}
