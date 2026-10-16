using System.Security.Cryptography;
namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentConnectionSession
{
    // Versioned adapter tag persisted in the existing transport column. Lease storage remains compatible.
    public const string ParticipantSequenceTransport = "mcp-streamable-http-participant-v2";
    public static AgentConnectionSession CreateParticipantSequence(Guid meetingId, Guid participantId, string binding, long generation, DateTimeOffset now)
    {
        var connection = CreateActivating(meetingId, participantId, 100000, binding, generation,
            "explicit_ack_v1", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now);
        connection.Transport = ParticipantSequenceTransport;
        return connection;
    }
}
