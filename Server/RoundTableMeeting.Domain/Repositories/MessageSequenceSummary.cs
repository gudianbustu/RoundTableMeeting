namespace RoundTableMeeting.Domain.Repositories;

public sealed record MessageSequenceSummary(long HighWatermarkSequence, long? FirstSequence, int MessageCount);
