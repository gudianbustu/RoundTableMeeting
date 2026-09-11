using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentConnectionRegistry : IAgentConnectionRegistry
{
    private readonly Dictionary<McpTransportSessionKey, AgentConnectionRegistration> registrations = [];
    private readonly Dictionary<McpTransportSessionKey, AgentConnectionRegistration> terminated = [];
    private readonly Dictionary<McpTransportSessionKey, CancellationTokenSource> bindingCancellations = [];
    private readonly Dictionary<McpTransportSessionKey, ReservationEntry> reservations = [];
    private readonly object sync = new();

    public async ValueTask<IAsyncDisposable> ReserveTransportAsync(
        McpTransportSessionKey transportSession, CancellationToken cancellationToken = default)
    {
        if (transportSession.IsEmpty) throw new ArgumentException("Transport session is required.", nameof(transportSession));
        ReservationEntry entry;
        lock (sync)
        {
            if (!reservations.TryGetValue(transportSession, out entry!))
                reservations.Add(transportSession, entry = new());
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
            return new ReservationLease(this, transportSession, entry);
        }
        catch
        {
            ReleaseReservation(transportSession, entry, acquired: false);
            throw;
        }
    }

    public bool TryActivate(McpTransportSessionKey transportSession,
        AgentConnectionRegistration registration, AgentConnectionRegistration? expectedPrevious)
    {
        if (!IsValid(transportSession, registration)
            || registration.ConnectionGeneration <= 0 || registration.BindingVersion <= 0)
            return false;
        CancellationTokenSource? retired = null;
        lock (sync)
        {
            if (!reservations.TryGetValue(transportSession, out var reservation) || reservation.Closed)
                return false;
            if (registrations.TryGetValue(transportSession, out var active))
                return active == registration;
            if (terminated.ContainsKey(transportSession)) return false;
            var previous = registrations.FirstOrDefault(pair => SameParticipant(pair.Value, registration));
            if (previous.Value is not null)
            {
                if (expectedPrevious != previous.Value || !IsNewer(registration, previous.Value))
                    return false;
                registrations.Remove(previous.Key);
                terminated[previous.Key] = previous.Value;
                bindingCancellations.Remove(previous.Key, out retired);
            }
            else if (expectedPrevious is not null && !IsNewer(registration, expectedPrevious))
                return false;
            registrations.Add(transportSession, registration);
            bindingCancellations.Add(transportSession, new());
        }
        CancelBinding(retired);
        return true;
    }

    public bool Retire(AgentConnectionRegistration registration)
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            var pair = registrations.FirstOrDefault(pair => pair.Value == registration);
            if (pair.Value is null) return false;
            registrations.Remove(pair.Key);
            terminated[pair.Key] = pair.Value;
            bindingCancellations.Remove(pair.Key, out cancellation);
        }
        CancelBinding(cancellation);
        return true;
    }

    public CancellationToken GetBindingCancellationToken(
        McpTransportSessionKey transportSession, AgentConnectionRegistration registration)
    {
        lock (sync)
            return registrations.TryGetValue(transportSession, out var active) && active == registration
                ? bindingCancellations[transportSession].Token
                : new CancellationToken(canceled: true);
    }

    public void TerminateMeeting(Guid meetingId)
    {
        List<CancellationTokenSource> cancellations = [];
        lock (sync)
        {
            foreach (var pair in registrations.Where(pair => pair.Value.MeetingId == meetingId).ToArray())
            {
                registrations.Remove(pair.Key);
                terminated[pair.Key] = pair.Value;
                if (bindingCancellations.Remove(pair.Key, out var cancellation)) cancellations.Add(cancellation);
            }
        }
        foreach (var cancellation in cancellations) CancelBinding(cancellation);
    }

    public bool ContainsTransport(McpTransportSessionKey transportSession)
    {
        lock (sync) return registrations.ContainsKey(transportSession) || terminated.ContainsKey(transportSession);
    }

    public bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionSessionId, string transportBinding)
    {
        lock (sync)
            return registrations.Values.Any(registration =>
                registration.MeetingId == meetingId && registration.ParticipantId == participantId
                && registration.ConnectionSessionId == connectionSessionId && registration.TransportBinding == transportBinding);
    }

    public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId)
    {
        lock (sync) return registrations.Values.Where(registration => registration.MeetingId == meetingId).ToArray();
    }

    public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey transportSession)
    {
        AgentConnectionRegistration? registration;
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            // Fence an in-flight activation even if its database commit precedes registration.
            if (reservations.TryGetValue(transportSession, out var reservation)) reservation.Closed = true;
            terminated.Remove(transportSession);
            registrations.Remove(transportSession, out registration);
            bindingCancellations.Remove(transportSession, out cancellation);
        }
        CancelBinding(cancellation);
        return registration is null ? [] : [registration];
    }

    public bool TryGetRegistration(McpTransportSessionKey transportSession, out AgentConnectionRegistration registration)
    {
        lock (sync)
            return registrations.TryGetValue(transportSession, out registration!)
                || terminated.TryGetValue(transportSession, out registration!);
    }

    public bool TryRegister(McpTransportSessionKey transportSession, AgentConnectionRegistration registration)
    {
        if (!IsValid(transportSession, registration)) return false;
        lock (sync)
        {
            if ((reservations.TryGetValue(transportSession, out var reservation) && reservation.Closed)
                || registrations.ContainsKey(transportSession) || terminated.ContainsKey(transportSession)
                || registrations.Values.Any(existing => SameParticipant(existing, registration)))
                return false;
            registrations.Add(transportSession, registration);
            bindingCancellations.Add(transportSession, new());
            return true;
        }
    }

    public bool Unregister(McpTransportSessionKey transportSession, Guid connectionSessionId)
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            if (!registrations.TryGetValue(transportSession, out var registration)
                || registration.ConnectionSessionId != connectionSessionId) return false;
            registrations.Remove(transportSession);
            bindingCancellations.Remove(transportSession, out cancellation);
        }
        CancelBinding(cancellation);
        return true;
    }

    private static bool SameParticipant(AgentConnectionRegistration left, AgentConnectionRegistration right) =>
        left.MeetingId == right.MeetingId && left.ParticipantId == right.ParticipantId;

    private static bool IsValid(McpTransportSessionKey session, AgentConnectionRegistration registration) =>
        !session.IsEmpty && registration.MeetingId != Guid.Empty && registration.ParticipantId != Guid.Empty
        && registration.ConnectionSessionId != Guid.Empty && !string.IsNullOrWhiteSpace(registration.TransportBinding)
        && registration.ConnectionGeneration is >= 0 and <= 9007199254740991
        && registration.BindingVersion is >= 0 and <= 9007199254740991;

    private static bool IsNewer(AgentConnectionRegistration next, AgentConnectionRegistration previous) =>
        SameParticipant(next, previous) && next.TransportBinding != previous.TransportBinding
        && (next.ConnectionSessionId == previous.ConnectionSessionId
            ? next.ConnectionGeneration == previous.ConnectionGeneration && next.BindingVersion > previous.BindingVersion
            : next.ConnectionGeneration > previous.ConnectionGeneration);

    private static void CancelBinding(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        // A consumer callback cannot undo an ownership change or suppress other cancellations.
        catch (AggregateException) { }
        finally { cancellation.Dispose(); }
    }

    private void ReleaseReservation(McpTransportSessionKey session, ReservationEntry entry, bool acquired)
    {
        lock (sync)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.Users == 0)
            {
                reservations.Remove(session);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class ReservationEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
        public bool Closed { get; set; }
    }

    private sealed class ReservationLease(
        AgentConnectionRegistry owner, McpTransportSessionKey session, ReservationEntry entry) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.ReleaseReservation(session, entry, acquired: true);
            return ValueTask.CompletedTask;
        }
    }
}
