using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

/// <summary>Immutable result receipt. Reading it never allocates a new binding or retry cycle.</summary>
public sealed class AgentRecoveryOperation : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public AgentRecoveryOperationKind OperationKind { get; private set; }
    public Guid OperationId { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public string? NormalizedDisplayName { get; private set; }
    public Guid? ConnectionSessionId { get; private set; }
    public Guid? TaskId { get; private set; }
    public Guid? ResultConnectionId { get; private set; }
    public long? ResultConnectionGeneration { get; private set; }
    public long? ResultBindingVersion { get; private set; }
    public string? TargetTransportBinding { get; private set; }
    public int? ResultRetryCycle { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AgentRecoveryOperation() { }

    public static AgentRecoveryOperation ForJoin(Guid meetingId, Guid participantId, string normalizedDisplayName,
        Guid operationId, string fingerprint, Guid resultConnectionId, long generation, long bindingVersion,
        string targetTransportBinding, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(normalizedDisplayName) || normalizedDisplayName.Length > 128)
            throw new ArgumentException("A normalized display name is required.", nameof(normalizedDisplayName));
        var receipt = Create(meetingId, participantId, operationId, fingerprint, createdAtUtc);
        receipt.OperationKind = AgentRecoveryOperationKind.Join;
        receipt.NormalizedDisplayName = normalizedDisplayName;
        receipt.SetConnectionResult(resultConnectionId, generation, bindingVersion, targetTransportBinding);
        return receipt;
    }

    public static AgentRecoveryOperation ForResume(Guid meetingId, Guid participantId, Guid connectionSessionId,
        Guid operationId, string fingerprint, long generation, long bindingVersion, string targetTransportBinding,
        DateTimeOffset createdAtUtc)
    {
        var receipt = Create(meetingId, participantId, operationId, fingerprint, createdAtUtc);
        receipt.OperationKind = AgentRecoveryOperationKind.Resume;
        receipt.ConnectionSessionId = connectionSessionId;
        receipt.SetConnectionResult(connectionSessionId, generation, bindingVersion, targetTransportBinding);
        return receipt;
    }

    public static AgentRecoveryOperation ForRetry(Guid meetingId, Guid participantId, Guid taskId, Guid operationId,
        string fingerprint, int resultRetryCycle, DateTimeOffset createdAtUtc)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task is required.", nameof(taskId));
        ArgumentOutOfRangeException.ThrowIfLessThan(resultRetryCycle, 1);
        var receipt = Create(meetingId, participantId, operationId, fingerprint, createdAtUtc);
        receipt.OperationKind = AgentRecoveryOperationKind.Retry;
        receipt.TaskId = taskId;
        receipt.ResultRetryCycle = resultRetryCycle;
        return receipt;
    }

    private static AgentRecoveryOperation Create(Guid meetingId, Guid participantId, Guid operationId,
        string fingerprint, DateTimeOffset createdAtUtc)
    {
        if (meetingId == Guid.Empty || participantId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("Meeting, participant and operation are required.");
        if (fingerprint is not { Length: 64 } || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Request fingerprint must be a SHA-256 digest.", nameof(fingerprint));
        return new()
        {
            MeetingId = meetingId, ParticipantId = participantId, OperationId = operationId,
            RequestFingerprint = fingerprint.ToUpperInvariant(), CreatedAtUtc = createdAtUtc
        };
    }

    private void SetConnectionResult(Guid connectionId, long generation, long bindingVersion, string targetTransportBinding)
    {
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection is required.", nameof(connectionId));
        if (generation is < 1 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(generation));
        if (bindingVersion is < 1 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(bindingVersion));
        if (string.IsNullOrWhiteSpace(targetTransportBinding) || targetTransportBinding.Length > 128)
            throw new ArgumentException("Target transport binding is required.", nameof(targetTransportBinding));
        ResultConnectionId = connectionId;
        ResultConnectionGeneration = generation;
        ResultBindingVersion = bindingVersion;
        TargetTransportBinding = targetTransportBinding;
    }
}
