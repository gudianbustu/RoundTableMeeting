using System.Security.Cryptography;
using System.Text.Json;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.Application.AgentCollaboration;

public static class AgentRecoveryCredentials
{
    public static string HashKey(string recoveryKey)
    {
        if (!AgentRecoveryRequestValidator.IsRecoveryKey(recoveryKey))
            throw new ArgumentException("Recovery key format is invalid.", nameof(recoveryKey));
        Span<byte> bytes = stackalloc byte[32];
        Convert.TryFromBase64String(recoveryKey.Replace('-', '+').Replace('_', '/') + "=", bytes, out _);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool VerifyKey(string? recoveryKey, string? expectedHash)
    {
        if (!AgentRecoveryRequestValidator.IsRecoveryKey(recoveryKey)) return false;
        var actual = Convert.FromHexString(HashKey(recoveryKey!));
        Span<byte> expected = stackalloc byte[32];
        expected.Clear();
        try
        {
            // Missing identities still execute a fixed-size digest comparison after parsing the client key.
            var valid = expectedHash is { Length: 64 }
                && Convert.FromHexString(expectedHash, expected, out var consumed, out var written) == System.Buffers.OperationStatus.Done
                && consumed == 64 && written == 32;
            return CryptographicOperations.FixedTimeEquals(actual, expected) & valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static string JoinFingerprint(AgentJoinRequest request)
    {
        if (AgentRecoveryRequestValidator.ValidateJoin(request) is not null
            || request.ReliabilityMode != AgentReliabilityProtocol.ExplicitAck)
            throw new ArgumentException("A valid reliable join request is required.", nameof(request));
        // Identity and operation ID belong to the receipt key. JSON avoids delimiter collisions.
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = AgentReliabilityProtocol.ExplicitAck,
            JoinMode = request.JoinMode ?? AgentReliabilityProtocol.Create,
            request.ContextCharacterLimit,
            RecoveryKeyHash = HashKey(request.RecoveryKey!)
        })));
    }
}
