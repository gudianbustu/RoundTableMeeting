using System.Security.Cryptography;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.Application.AgentCollaboration;

/// <summary>Validates new protocol fields before identity lookup or ownership changes.</summary>
public static class AgentRecoveryRequestValidator
{
    public static AgentOperationResult? ValidateJoin(AgentJoinRequest request)
    {
        var mode = request.ReliabilityMode ?? AgentReliabilityProtocol.Legacy;
        var joinMode = request.JoinMode ?? AgentReliabilityProtocol.Create;
        var reliable = mode == AgentReliabilityProtocol.ExplicitAck;
        if (mode is not (AgentReliabilityProtocol.Legacy or AgentReliabilityProtocol.ExplicitAck)
            || joinMode is not (AgentReliabilityProtocol.Create or AgentReliabilityProtocol.Takeover)
            || request.OperationId == Guid.Empty
            || (request.RecoveryKey is not null && !IsRecoveryKey(request.RecoveryKey))
            || (reliable && (request.OperationId is null || request.RecoveryKey is null)))
            return Invalid(reliable);

        if (!reliable && (joinMode == AgentReliabilityProtocol.Takeover
            || request.OperationId is not null || request.RecoveryKey is not null))
            return AgentOperationResult.Failure("reliability_mode_required",
                "These parameters require reliable mode; remove them or use a reliable client.");
        return null;
    }

    public static AgentOperationResult? ValidateResume(AgentResumeRequest request)
        => IsId(request.ConnectionSessionId) && IsId(request.OperationId) && IsRecoveryKey(request.RecoveryKey)
            ? null : Invalid(reliable: true);

    public static AgentOperationResult? ValidateConfirmation(AgentConfirmContextRequest request)
    {
        var valid = IsId(request.ConnectionSessionId) && request.Kind switch
        {
            "initial" => IsId(request.InitialDeliveryId) && request.TaskId is null && request.DeliveryAttemptId is null,
            "task" => request.InitialDeliveryId is null && IsId(request.TaskId) && IsId(request.DeliveryAttemptId),
            _ => false
        };
        return valid ? null : Invalid(reliable: true);
    }

    public static AgentOperationResult? ValidateRetry(AgentRetryRequest request)
        => IsId(request.ConnectionSessionId) && IsId(request.TaskId) && IsId(request.OperationId)
            ? null : Invalid(reliable: true);

    public static bool IsRecoveryKey(string? key)
    {
        // Exactly 32 bytes, canonical base64url without padding. Reject alternate encodings.
        if (key is null || key.Length != 43 || key.Any(c =>
            !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c is '-' or '_')))
            return false;
        Span<byte> bytes = stackalloc byte[32];
        try
        {
            if (!Convert.TryFromBase64String(key.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written)
                || written != 32)
                return false;
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == key;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsId(Guid? id) => id is not null && id != Guid.Empty;

    private static AgentOperationResult Invalid(bool reliable)
        => reliable
            ? AgentOperationResult.Failure("agent_request_invalid", "The Agent request parameters are invalid.", new("stop"))
            : AgentOperationResult.Failure("agent_request_invalid", "The Agent request parameters are invalid.");
}
