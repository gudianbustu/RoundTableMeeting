using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.RealtimeChat;
using System.Net.Http.Json;
using System.Text.Json;

namespace RoundTableMeeting.IntegrationTests;

public sealed class CurrentMcpPresenceTests
{
    [Fact]
    public async Task InfoSeparatesMembershipFromHumanAndAgentConnectionSnapshots()
    {
        using var original = new TestApplicationFactory(); var clock = new ManualTimeProvider();
        var notifications = new PresenceRecorder();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<RoundTableMeeting.Abstraction.Services.IAgentActivityNotifier>(notifications); }));
        using var human = factory.CreateClient(); using var client = factory.CreateClient();
        await Post(human, "/api/auth/login", new { username = "alice", password = "test-password-alice" });
        var created = await Post(human, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Connection snapshots" });
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var details = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}");
        var humanId = details.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        var observer = await CurrentMcpTests.Initialize(client);
        var observerJoin = await CurrentMcpTests.Call(client, observer.Session, "join_meeting", new { meetingCode = code, displayName = "Observer" });
        var connection = observerJoin.GetProperty("connectionSessionId").GetGuid();
        var observerId = observerJoin.GetProperty("participantId").GetGuid();
        async Task<JsonElement> Info() => await CurrentMcpTests.Call(client, observer.Session, "get_meeting_info", new { connectionSessionId = connection });
        static JsonElement Participant(JsonElement info, Guid participant) => info.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("participantId").GetGuid() == participant);
        static void State(JsonElement info, Guid participant, string membership, string connectionState)
        {
            var p = Participant(info, participant);
            Assert.Equal(membership, p.GetProperty("membershipState").GetString());
            Assert.Equal(connectionState, p.GetProperty("connectionState").GetString());
        }
        State(await Info(), humanId, "joined", "offline");
        State(await Info(), observerId, "joined", "online");
        var presence = factory.Services.GetRequiredService<MeetingPresenceRegistry>();
        presence.Register("presence-test-human", id, humanId, () => { });
        State(await Info(), humanId, "joined", "online");
        presence.UnregisterImmediately("presence-test-human");
        State(await Info(), humanId, "joined", "offline");
        var target = await CurrentMcpTests.Initialize(client);
        var targetJoin = await CurrentMcpTests.Call(client, target.Session, "join_meeting", new { meetingCode = code, displayName = "Target" });
        var targetId = targetJoin.GetProperty("participantId").GetGuid();
        var targetConnection = targetJoin.GetProperty("connectionSessionId").GetGuid();
        State(await Info(), targetId, "joined", "online");
        await CurrentMcpTests.Call(client, target.Session, "disconnect_agent", new { connectionSessionId = targetConnection });
        State(await Info(), targetId, "joined", "offline");
        var restored = await CurrentMcpTests.Initialize(client);
        var rejoin = await CurrentMcpTests.Call(client, restored.Session, "join_meeting", new { meetingCode = code, displayName = "Target", joinMode = "reconnect" });
        State(await Info(), targetId, "joined", "online");
        await CurrentMcpTests.Call(client, restored.Session, "leave_meeting", new { connectionSessionId = rejoin.GetProperty("connectionSessionId").GetGuid(), operationId = Guid.NewGuid() });
        State(await Info(), targetId, "left", "offline");
        var expiring = await CurrentMcpTests.Initialize(client);
        var expiryJoin = await CurrentMcpTests.Call(client, expiring.Session, "join_meeting", new { meetingCode = code, displayName = "Expiring" });
        clock.Advance(TimeSpan.FromMinutes(4));
        await CurrentMcpTests.Call(client, observer.Session, "renew_agent_lease", new { connectionSessionId = connection });
        clock.Advance(TimeSpan.FromMinutes(2));
        var expiringId = expiryJoin.GetProperty("participantId").GetGuid();
        // No API call or resubscription should be needed to announce expiration.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!notifications.Offline.Contains(expiringId)) await Task.Delay(50, deadline.Token);
        Assert.DoesNotContain(observerId, notifications.Offline);
        State(await Info(), expiringId, "joined", "offline");
        State(await Info(), observerId, "joined", "online");
    }
    private sealed class PresenceRecorder : RoundTableMeeting.Abstraction.Services.IAgentActivityNotifier
    {
        public System.Collections.Concurrent.ConcurrentBag<Guid> Offline { get; } = new();
        public Task NotifyAsync(RoundTableMeeting.Abstraction.Contracts.RealtimeChat.AgentActivityDto activity, bool notifyPresence = false, CancellationToken cancellationToken = default)
        {
            if (notifyPresence && activity.State == "disconnected") Offline.Add(activity.ParticipantId);
            return Task.CompletedTask;
        }
    }
    private static async Task<JsonElement> Post(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost:5173");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
