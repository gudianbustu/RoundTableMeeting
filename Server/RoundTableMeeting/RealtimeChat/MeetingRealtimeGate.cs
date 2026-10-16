using System.Collections.Concurrent;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MeetingRealtimeGate : IMeetingRealtimeGate, IMeetingFileTransferGate
{
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    public async ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries.GetOrAdd(meetingId, static _ => new Entry());
            Task wait;
            lock (entry.Sync)
            {
                if (!entries.TryGetValue(meetingId, out var current) || !ReferenceEquals(current, entry)) continue;
                if (entry.Closed || entry.Closing && entry.RejectReaders) return null;
                if (!entry.Closing)
                {
                    entry.Readers++;
                    return new SharedLease(this, meetingId, entry);
                }
                wait = entry.Changed.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
        => AcquireExclusiveAsync(meetingId, timeout, true, cancellationToken);

    public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveMutationAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
        => AcquireExclusiveAsync(meetingId, timeout, false, cancellationToken);

    private async ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveAsync(Guid meetingId, TimeSpan timeout, bool rejectReaders, CancellationToken cancellationToken)
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
                        entry.RejectReaders = rejectReaders;
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

    public IMeetingFileTransferLease? Acquire(Guid meetingId)
    {
        while (true)
        {
            var entry = entries.GetOrAdd(meetingId, static _ => new Entry());
            lock (entry.Sync)
            {
                if (!entries.TryGetValue(meetingId, out var current) || !ReferenceEquals(current, entry)) continue;
                if (entry.Closed || entry.Closing) return null;
                var lease = new FileLease(this, meetingId, entry);
                entry.Files.Add(lease);
                return lease;
            }
        }
    }
    public async Task CancelAndDrainAsync(Guid meetingId, CancellationToken ct = default)
    {
        var entry = entries.GetOrAdd(meetingId, static _ => new Entry());
        FileLease[] leases;
        lock (entry.Sync) { entry.Closed = true; leases = entry.Files.ToArray(); Signal(entry); }
        foreach (var lease in leases) lease.Cancel();
        while (true)
        {
            Task changed;
            lock (entry.Sync) { if (entry.Files.Count == 0) return; changed = entry.Changed.Task; }
            await changed.WaitAsync(ct);
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
        public HashSet<FileLease> Files { get; } = [];
        public bool Closing { get; set; }
        public bool RejectReaders { get; set; }
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
                if (current.Readers == 0 && current.Files.Count == 0 && !current.Closing && !current.Closed) gate.entries.TryRemove(meetingId, out _);
            }
        }
    }

    private sealed class FileLease(MeetingRealtimeGate gate, Guid meetingId, Entry entry) : IMeetingFileTransferLease
    {
        private Entry? owner = entry;
        private readonly CancellationTokenSource source = new();
        public CancellationToken CancellationToken => source.Token;
        public void Cancel() { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.Sync)
            {
                current.Files.Remove(this); Signal(current);
                if (current.Readers == 0 && current.Files.Count == 0 && !current.Closing && !current.Closed)
                    gate.entries.TryRemove(meetingId, out _);
            }
            source.Dispose();
        }
    }

    private sealed class ExclusiveLease(Entry entry) : IMeetingRealtimeExclusiveLease
    {
        private Entry? owner = entry;
        private bool committed;
        public void CommitClosed()
        {
            var current = owner ?? throw new ObjectDisposedException(nameof(ExclusiveLease));
            FileLease[] leases;
            lock (current.Sync)
            {
                leases = current.Files.ToArray();
                current.Closed = true;
                current.Closing = false;
                committed = true;
                Signal(current);
            }
            foreach (var lease in leases) lease.Cancel();
        }
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null || committed) return;
            ReleaseClosing(current);
        }
    }
}
