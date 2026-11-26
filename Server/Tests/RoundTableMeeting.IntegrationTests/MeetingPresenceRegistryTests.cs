using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingPresenceRegistryTests
{
    [Fact]
    public async Task ReconnectWithinGracePeriodDoesNotPublishOfflineTransition()
    {
        var hub = new RecordingHubContext();
        var registry = new MeetingPresenceRegistry(new RealtimeChatOptions { PresenceDisconnectGraceSeconds = 1 }, hub, TimeProvider.System);
        var meetingId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        Assert.True(registry.Register("first", meetingId, participantId, () => { }));
        Assert.True(registry.ScheduleOffline("first", TestContext.Current.CancellationToken));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(registry.Register("second", meetingId, participantId, () => { }));
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        Assert.Empty(hub.Proxy.Messages);
        Assert.Contains(participantId, registry.GetOnline(meetingId));
    }

    [Fact]
    public async Task RepeatedDisconnectsProduceOnlyOneOfflineTransition()
    {
        var hub = new RecordingHubContext();
        var registry = new MeetingPresenceRegistry(new RealtimeChatOptions { PresenceDisconnectGraceSeconds = 1 }, hub, TimeProvider.System);
        var meetingId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        registry.Register("first", meetingId, participantId, () => { });
        registry.Register("second", meetingId, participantId, () => { });
        registry.ScheduleOffline("first", TestContext.Current.CancellationToken);
        registry.ScheduleOffline("second", TestContext.Current.CancellationToken);
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.Single(hub.Proxy.Messages);
        Assert.Equal("participantPresenceChanged", hub.Proxy.Messages[0].Method);
    }

    private sealed class RecordingHubContext : IHubContext<MeetingChatHub>
    {
        public RecordingProxy Proxy { get; } = new();
        public IHubClients Clients => new RecordingClients(Proxy);
        public IGroupManager Groups { get; } = new RecordingGroups();
    }

    private sealed class RecordingClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class RecordingProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Messages { get; } = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Messages.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
