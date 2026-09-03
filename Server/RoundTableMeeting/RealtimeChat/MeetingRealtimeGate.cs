using System.Collections.Concurrent;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MeetingRealtimeGate : IMeetingRealtimeGate
{
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var entry = entries.GetOrAdd(meetingId, static _ => new Entry());
            lock (entry.Sync)
            {
                if (!entries.TryGetValue(meetingId, out var current) || !ReferenceEquals(current, entry)) continue;
                if (entry.Closing || entry.Closed) return ValueTask.FromResult<IMeetingRealtimeSharedLease?>(null);
                entry.Readers++;
                return ValueTask.FromResult<IMeetingRealtimeSharedLease?>(new SharedLease(this, meetingId, entry));
            }
        }
    }

    public async ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var ownsClosing = false;
        Entry? entry = null;
        try
        {
            while (true)
            {
                entry ??= entries.GetOrAdd(meetingId, static _ => new Entry());
                Task waitTask;
                lock (entry.Sync)
                {
                    if (!entries.TryGetValue(meetingId, out var current) || !ReferenceEquals(current, entry)) { entry = null; continue; }
                    if (entry.Closed) return null;
                    if (!entry.Closing)
                    {
                        entry.Closing = true;
                        ownsClosing = true;
                    }
                    if (ownsClosing && entry.Readers == 0) return new ExclusiveLease(entry);
                    waitTask = entry.Changed.Task;
                }
                await waitTask.WaitAsync(timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (ownsClosing && entry is not null) ReleaseClosing(entry);
            return null;
        }
        catch
        {
            if (ownsClosing && entry is not null) ReleaseClosing(entry);
            throw;
        }
    }

    public void Forget(Guid meetingId) => entries.TryRemove(meetingId, out _);

    private static void Signal(Entry entry)
    {
        var current = entry.Changed;
        entry.Changed = NewSignal();
        current.TrySetResult();
    }

    private static void ReleaseClosing(Entry entry)
    {
        lock (entry.Sync)
        {
            entry.Closing = false;
            Signal(entry);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry
    {
        public object Sync { get; } = new();
        public int Readers { get; set; }
        public bool Closing { get; set; }
        public bool Closed { get; set; }
        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }

    private sealed class SharedLease(MeetingRealtimeGate gate, Guid meetingId, Entry entry) : IMeetingRealtimeSharedLease
    {
        private Entry? owner = entry;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.Sync)
            {
                current.Readers--;
                Signal(current);
                if (current.Readers == 0 && !current.Closing && !current.Closed) gate.entries.TryRemove(meetingId, out _);
            }
        }
    }

    private sealed class ExclusiveLease(Entry entry) : IMeetingRealtimeExclusiveLease
    {
        private Entry? owner = entry;
        private bool committed;
        public void CommitClosed()
        {
            var current = owner ?? throw new ObjectDisposedException(nameof(ExclusiveLease));
            lock (current.Sync)
            {
                current.Closed = true;
                current.Closing = false;
                committed = true;
                Signal(current);
            }
        }
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null || committed) return;
            ReleaseClosing(current);
        }
    }
}
