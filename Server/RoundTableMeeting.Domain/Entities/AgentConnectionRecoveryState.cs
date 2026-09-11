using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentConnectionSession
{
    public string ReliabilityMode { get; private set; } = "legacy_v1";
    public long ConnectionGeneration { get; private set; } = 1;
    public long BindingVersion { get; private set; } = 1;
    public string? RecoveryKeyHash { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public string? DisconnectReason { get; private set; }
    public Guid? SupersededByConnectionId { get; private set; }

    public static AgentConnectionSession CreateActivating(Guid meetingId, Guid participantId, int characterLimit,
        string binding, long generation, string reliabilityMode, string? recoveryKeyHash, DateTimeOffset now)
    {
        if (generation is < 1 or > MaximumSequence) throw new ArgumentOutOfRangeException(nameof(generation));
        if (reliabilityMode is not ("legacy_v1" or "explicit_ack_v1")) throw new ArgumentException("Invalid reliability mode.");
        if (reliabilityMode == "explicit_ack_v1"
            ? recoveryKeyHash is not { Length: 64 } || recoveryKeyHash.Any(character => !Uri.IsHexDigit(character))
            : recoveryKeyHash is not null) throw new ArgumentException("Recovery digest does not match the mode.");
        var connection = Create(meetingId, participantId, characterLimit, binding, now);
        connection.ConnectionGeneration = generation;
        connection.ReliabilityMode = reliabilityMode;
        connection.RecoveryKeyHash = recoveryKeyHash?.ToUpperInvariant();
        connection.State = AgentConnectionState.Activating;
        return connection;
    }

    public bool Activate(long expectedBindingVersion, DateTimeOffset now, int leaseSeconds)
    {
        if (State != AgentConnectionState.Activating || BindingVersion != expectedBindingVersion) return false;
        EnsureActivityTime(now);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        var expires = ReliabilityMode == "explicit_ack_v1" ? now.AddSeconds(leaseSeconds) : (DateTimeOffset?)null;
        State = AgentConnectionState.Connected;
        ConnectedAtUtc = now;
        LastActivityAtUtc = now;
        DisconnectedAtUtc = null;
        DisconnectReason = null;
        LeaseExpiresAtUtc = expires;
        return true;
    }

    public bool BeginResume(long expectedBindingVersion, string newBinding, DateTimeOffset now)
    {
        if (ReliabilityMode != "explicit_ack_v1" || State is not (AgentConnectionState.Connected or AgentConnectionState.Disconnected)
            || BindingVersion != expectedBindingVersion) return false;
        EnsureActivityTime(now);
        var normalized = NormalizeBinding(newBinding);
        if (normalized == McpTransportBinding) throw new ArgumentException("Resume requires a new transport binding.");
        if (BindingVersion == MaximumSequence) throw new InvalidOperationException("Binding version is exhausted.");
        BindingVersion++;
        McpTransportBinding = normalized;
        State = AgentConnectionState.Activating;
        LastActivityAtUtc = now;
        DisconnectedAtUtc = null;
        DisconnectReason = null;
        LeaseExpiresAtUtc = null;
        return true;
    }

    public bool Renew(long expectedBindingVersion, DateTimeOffset now, int leaseSeconds, int? waitSeconds = null, int safetySeconds = 0)
    {
        if (State != AgentConnectionState.Connected || ReliabilityMode != "explicit_ack_v1" || BindingVersion != expectedBindingVersion
            || LeaseExpiresAtUtc is not DateTimeOffset expires || now >= expires) return false;
        EnsureActivityTime(now);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(safetySeconds);
        if (waitSeconds is < 1) throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        var next = now.AddSeconds(leaseSeconds);
        if (waitSeconds is int wait)
        {
            var waitExpires = now.AddSeconds(checked((long)wait + safetySeconds));
            if (waitExpires > next) next = waitExpires;
        }
        LeaseExpiresAtUtc = next > expires ? next : expires;
        LastActivityAtUtc = now;
        return true;
    }

    public bool DisconnectBinding(long expectedBindingVersion, string reason, DateTimeOffset now)
    {
        if (BindingVersion != expectedBindingVersion || State is not (AgentConnectionState.Activating or AgentConnectionState.Connected)) return false;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64) throw new ArgumentException("Disconnect reason is required.");
        Disconnect(now);
        DisconnectReason = reason;
        return true;
    }

    public bool Supersede(Guid replacementConnectionId, DateTimeOffset now)
    {
        if (State == AgentConnectionState.Superseded) return false;
        if (replacementConnectionId == Guid.Empty || replacementConnectionId == ID) throw new ArgumentException("A replacement connection is required.");
        EnsureActivityTime(now);
        State = AgentConnectionState.Superseded;
        SupersededByConnectionId = replacementConnectionId;
        DisconnectedAtUtc = now;
        LastActivityAtUtc = now;
        DisconnectReason = "connection_taken_over";
        return true;
    }
}
