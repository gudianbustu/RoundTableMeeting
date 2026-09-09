using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentTaskSignalRegistry(AgentCollaborationOptions options) : IAgentTaskSignalRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<(Guid MeetingId, Guid ParticipantId), Entry> signals = [];

    public void Signal(Guid meetingId, Guid agentParticipantId)
    {
        if (meetingId == Guid.Empty || agentParticipantId == Guid.Empty) return;
        lock (sync)
        {
            var entry = GetEntry((meetingId, agentParticipantId));
            if (entry.Waiters == 0) entry.Pending = Math.Min(options.WakeSignalCapacity, entry.Pending + 1);
            else
            {
                entry.Changed.TrySetResult();
                entry.Changed = NewSignal();
            }
        }
    }

    public void TerminateMeeting(Guid meetingId)
    {
        lock (sync)
        {
            foreach (var key in signals.Keys.Where(key => key.MeetingId == meetingId).ToArray())
            {
                var entry = signals[key];
                signals.Remove(key);
                entry.Changed.TrySetResult();
            }
        }
    }

    public async Task<bool> WaitAsync(Guid meetingId, Guid agentParticipantId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (meetingId == Guid.Empty || agentParticipantId == Guid.Empty || timeout <= TimeSpan.Zero) return false;
        var key = (meetingId, agentParticipantId);
        Entry entry;
        Task changed;
        lock (sync)
        {
            entry = GetEntry(key);
            if (entry.Pending > 0) { entry.Pending--; return true; }
            entry.Waiters++;
            changed = entry.Changed.Task;
        }
        try
        {
            await changed.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
        finally
        {
            lock (sync)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && entry.Pending == 0
                    && signals.TryGetValue(key, out var current) && ReferenceEquals(entry, current))
                    signals.Remove(key);
            }
        }
    }

    private Entry GetEntry((Guid MeetingId, Guid ParticipantId) key)
    {
        if (!signals.TryGetValue(key, out var entry)) signals[key] = entry = new Entry();
        return entry;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Entry
    {
        public int Pending;
        public int Waiters;
        public TaskCompletionSource Changed = NewSignal();
    }
}
