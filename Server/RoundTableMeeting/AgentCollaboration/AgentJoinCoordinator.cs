using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentJoinCoordinator : IAgentJoinCoordinator
{
    private readonly Dictionary<string, Entry> locks = new(StringComparer.Ordinal);
    private readonly object sync = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        Guid meetingId,
        string normalizedDisplayName,
        CancellationToken cancellationToken = default)
    {
        var key = $"{meetingId:D}:{normalizedDisplayName}";
        Entry entry;
        lock (sync)
        {
            if (!locks.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                locks.Add(key, entry);
            }
            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
            return new Lease(this, key, entry);
        }
        catch
        {
            Release(key, entry, releaseGate: false);
            throw;
        }
    }

    private void Release(string key, Entry entry, bool releaseGate)
    {
        if (releaseGate) entry.Gate.Release();
        lock (sync)
        {
            entry.Users--;
            if (entry.Users == 0
                && locks.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                locks.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Lease(AgentJoinCoordinator coordinator, string key, Entry entry) : IAsyncDisposable
    {
        private AgentJoinCoordinator? owner = coordinator;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return ValueTask.CompletedTask;
            current.Release(key, entry, releaseGate: true);
            return ValueTask.CompletedTask;
        }
    }
}
