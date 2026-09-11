using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentConnectionBindingTests
{
    [Fact]
    public async Task ReservationSerializesSameTransportButNotOtherTransportsAndCancellationReleasesWaiter()
    {
        var registry = new AgentConnectionRegistry();
        var first = await registry.ReserveTransportAsync(Session(1));
        using var cancellation = new CancellationTokenSource();
        var cancelled = registry.ReserveTransportAsync(Session(1), cancellation.Token).AsTask();
        var next = registry.ReserveTransportAsync(Session(1)).AsTask();
        Assert.False(cancelled.IsCompleted);
        Assert.False(next.IsCompleted);
        await using var unrelated = await registry.ReserveTransportAsync(Session(2)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CloseDuringReservationRejectsActivationAndQueuedActivation()
    {
        var registry = new AgentConnectionRegistry();
        var first = await registry.ReserveTransportAsync(Session(1));
        var next = registry.ReserveTransportAsync(Session(1)).AsTask();
        Assert.Empty(registry.RemoveRegistrations(Session(1)));
        Assert.False(registry.TryActivate(Session(1), Registration(1), null));
        Assert.False(registry.TryRegister(Session(1), Registration(1)));
        await first.DisposeAsync();
        await using var queued = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(registry.TryActivate(Session(1), Registration(1), null));
    }

    [Fact]
    public async Task ResumeCancelsOldBindingAndOldClosureCannotRemoveNewBinding()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        var resumed = original with { BindingVersion = 2, TransportBinding = "binding-2" };
        await using var oldReservation = await registry.ReserveTransportAsync(Session(1));
        Assert.True(registry.TryActivate(Session(1), original, null));
        var oldToken = registry.GetBindingCancellationToken(Session(1), original);
        await using var newReservation = await registry.ReserveTransportAsync(Session(2));
        Assert.True(registry.TryActivate(Session(2), resumed, original));
        Assert.True(oldToken.IsCancellationRequested);
        Assert.True(registry.TryGetRegistration(Session(1), out var tombstone));
        Assert.Equal(original, tombstone);
        Assert.False(registry.TryActivate(Session(1), original, null));
        Assert.False(registry.Retire(original));
        Assert.Empty(registry.RemoveRegistrations(Session(1)));
        Assert.Equal(resumed, Assert.Single(registry.ListRegistrations(original.MeetingId)));
        Assert.False(registry.GetBindingCancellationToken(Session(2), resumed).IsCancellationRequested);
    }

    [Fact]
    public async Task OnlineTakeoverRequiresExactPreviousBindingAndHigherGeneration()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        Assert.True(registry.TryRegister(Session(1), original));
        var next = original with { ConnectionSessionId = Guid.NewGuid(), TransportBinding = "binding-2", ConnectionGeneration = 2 };
        await using var reservation = await registry.ReserveTransportAsync(Session(2));
        Assert.False(registry.TryActivate(Session(2), next, null));
        Assert.False(registry.TryActivate(Session(2), next, original with { BindingVersion = 2 }));
        Assert.False(registry.TryActivate(Session(2), next with { ConnectionGeneration = 1 }, original));
        Assert.True(registry.TryActivate(Session(2), next, original));
        Assert.True(registry.TryActivate(Session(2), next, original));
        Assert.False(registry.TryActivate(Session(2), next with { BindingVersion = 2 }, original));
        Assert.Equal(next, Assert.Single(registry.ListRegistrations(original.MeetingId)));
    }

    [Fact]
    public async Task OfflineResumeStillRejectsGenerationChangeOrReusedBinding()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        var resumed = original with { BindingVersion = 2, TransportBinding = "binding-2" };
        await using var reservation = await registry.ReserveTransportAsync(Session(2));
        Assert.False(registry.TryActivate(Session(2), resumed with { ConnectionGeneration = 2 }, original));
        Assert.False(registry.TryActivate(Session(2), resumed with { TransportBinding = original.TransportBinding }, original));
        Assert.False(registry.TryActivate(Session(2), resumed with { ParticipantId = Guid.NewGuid() }, original));
        Assert.True(registry.TryActivate(Session(2), resumed, original));
    }

    [Fact]
    public async Task OccupiedTargetCannotRetireAnotherParticipantsBinding()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        var other = Registration(2);
        Assert.True(registry.TryRegister(Session(1), original));
        Assert.True(registry.TryRegister(Session(2), other));
        await using var reservation = await registry.ReserveTransportAsync(Session(2));
        Assert.False(registry.TryActivate(Session(2), original with { BindingVersion = 2, TransportBinding = "binding-2" }, original));
        Assert.False(registry.GetBindingCancellationToken(Session(1), original).IsCancellationRequested);
        Assert.True(registry.Retire(other));
        Assert.False(registry.TryActivate(Session(2), original with { BindingVersion = 2, TransportBinding = "binding-2" }, original));
    }

    [Fact]
    public void MeetingTerminationCancelsAllBindingsAndKeepsTombstonesUntilClose()
    {
        var registry = new AgentConnectionRegistry();
        var first = Registration(1);
        var second = Registration(2) with { MeetingId = first.MeetingId };
        var other = Registration(3);
        Assert.True(registry.TryRegister(Session(1), first));
        Assert.True(registry.TryRegister(Session(2), second));
        Assert.True(registry.TryRegister(Session(3), other));
        var token1 = registry.GetBindingCancellationToken(Session(1), first);
        var token2 = registry.GetBindingCancellationToken(Session(2), second);
        using var throwingCallback = token1.Register(() => throw new InvalidOperationException("Consumer failure"));
        registry.TerminateMeeting(first.MeetingId);
        Assert.True(token1.IsCancellationRequested);
        Assert.True(token2.IsCancellationRequested);
        Assert.False(registry.GetBindingCancellationToken(Session(3), other).IsCancellationRequested);
        Assert.True(registry.ContainsTransport(Session(1)));
        Assert.Empty(registry.RemoveRegistrations(Session(1)));
        Assert.False(registry.ContainsTransport(Session(1)));
    }

    [Fact]
    public async Task CancellationCallbacksRunOutsideRegistryLock()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        Assert.True(registry.TryRegister(Session(1), original));
        var token = registry.GetBindingCancellationToken(Session(1), original);
        var callbackRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = token.Register(() =>
        {
            var read = Task.Run(() => registry.ContainsTransport(Session(1)));
            callbackRead.TrySetResult(read.Wait(TimeSpan.FromSeconds(3)));
        });
        Assert.True(await Task.Run(() => registry.Retire(original)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await callbackRead.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReliableActivationRequiresReservationAndSafePositiveVersions()
    {
        var registry = new AgentConnectionRegistry();
        var original = Registration(1);
        Assert.False(registry.TryActivate(Session(1), original, null));
        await using var reservation = await registry.ReserveTransportAsync(Session(1));
        Assert.False(registry.TryActivate(Session(1), original with { BindingVersion = 0 }, null));
        Assert.False(registry.TryActivate(Session(1), original with { ConnectionGeneration = 9007199254740992 }, null));
        Assert.True(registry.TryActivate(Session(1), original, null));
        Assert.True(registry.GetBindingCancellationToken(Session(1), original with { BindingVersion = 2 }).IsCancellationRequested);
    }

    private static AgentConnectionRegistration Registration(int seed) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "binding-" + seed) { ConnectionGeneration = 1, BindingVersion = 1 };

    private static McpTransportSessionKey Session(byte seed) => McpTransportSessionKey.FromDigest(Enumerable.Repeat(seed, 32).ToArray());
}
