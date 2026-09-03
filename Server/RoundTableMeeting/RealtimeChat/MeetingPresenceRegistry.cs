using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Application.RealtimeChat;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MeetingPresenceRegistry(RealtimeChatOptions options, IHubContext<MeetingChatHub> hub, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, ConnectionBinding> connections = new();
    private readonly Dictionary<(Guid MeetingId, Guid ParticipantId), CancellationTokenSource> pendingOffline = new();
    private readonly object sync = new();

    public bool Register(string connectionId, Guid meetingId, Guid participantId, Action abort)
    {
        lock (sync)
        {
            var key = (meetingId, participantId);
            var cancelled = pendingOffline.Remove(key, out var pending);
            pending?.Cancel();
            pending?.Dispose();
            connections[connectionId] = new(meetingId, participantId, abort);
            return !cancelled && connections.Values.Count(value => value.MeetingId == meetingId && value.ParticipantId == participantId) == 1;
        }
    }

    public ((Guid MeetingId, Guid ParticipantId) Binding, bool BecameOffline)? UnregisterImmediately(string connectionId)
    {
        lock (sync)
        {
            if (!connections.TryRemove(connectionId, out var binding)) return null;
            var identity = (binding.MeetingId, binding.ParticipantId);
            return (identity, !connections.Values.Any(value => value.MeetingId == identity.MeetingId && value.ParticipantId == identity.ParticipantId));
        }
    }

    public bool ScheduleOffline(string connectionId, CancellationToken cancellationToken = default)
    {
        (Guid MeetingId, Guid ParticipantId) binding;
        CancellationTokenSource delaySource;
        lock (sync)
        {
            if (!connections.TryRemove(connectionId, out var removed)) return false;
            binding = (removed.MeetingId, removed.ParticipantId);
            if (connections.Values.Any(value => value.MeetingId == binding.MeetingId && value.ParticipantId == binding.ParticipantId)) return false;
            if (pendingOffline.Remove(binding, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            delaySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pendingOffline[binding] = delaySource;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PresenceDisconnectGraceSeconds), delaySource.Token);
                lock (sync)
                {
                    if (connections.Values.Any(value => value.MeetingId == binding.MeetingId && value.ParticipantId == binding.ParticipantId)) return;
                }
                await hub.Clients.Group(MeetingChatHub.Group(binding.MeetingId)).SendAsync("participantPresenceChanged", new ParticipantPresenceDto(binding.ParticipantId, "offline", timeProvider.GetUtcNow()), delaySource.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (sync)
                {
                    if (pendingOffline.TryGetValue(binding, out var current) && ReferenceEquals(current, delaySource)) pendingOffline.Remove(binding);
                }
                delaySource.Dispose();
            }
        }, CancellationToken.None);
        return true;
    }

    public IReadOnlyList<Guid> GetOnline(Guid meetingId)
    {
        lock (sync)
            return connections.Values.Where(value => value.MeetingId == meetingId).Select(value => value.ParticipantId)
                .Concat(pendingOffline.Keys.Where(key => key.MeetingId == meetingId).Select(key => key.ParticipantId))
                .Distinct().ToArray();
    }

    public (Guid MeetingId, Guid ParticipantId)? Get(string connectionId) => connections.TryGetValue(connectionId, out var value) ? (value.MeetingId, value.ParticipantId) : null;

    public void RemoveMeeting(Guid meetingId)
    {
        lock (sync)
        {
            foreach (var pair in connections.Where(pair => pair.Value.MeetingId == meetingId).ToArray()) connections.TryRemove(pair.Key, out _);
            foreach (var pair in pendingOffline.Where(pair => pair.Key.MeetingId == meetingId).ToArray())
            {
                pendingOffline.Remove(pair.Key);
                pair.Value.Cancel();
                pair.Value.Dispose();
            }
        }
    }

    public void AbortMeeting(Guid meetingId)
    {
        ConnectionBinding[] bindings;
        lock (sync) bindings = connections.Values.Where(value => value.MeetingId == meetingId).ToArray();
        foreach (var binding in bindings) binding.Abort();
    }

    private sealed record ConnectionBinding(Guid MeetingId, Guid ParticipantId, Action Abort);
}
