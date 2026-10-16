using System.ComponentModel.DataAnnotations;
namespace RoundTableMeeting.Application.RealtimeChat;
public sealed class RealtimeChatOptions
{
    public const string SectionName = "RealtimeChat";
    [Range(1, 1_000_000)] public int MaxContentLength { get; set; } = 1000000;
    [Range(1, 200)] public int HistoryPageSize { get; set; } = 100;
    [Range(1, 200)] public int MaxHistoryPageSize { get; set; } = 200;
    [Range(1, 60)] public int GateCloseTimeoutSeconds { get; set; } = 5;
    [Range(1, 60)] public int PresenceDisconnectGraceSeconds { get; set; } = 5;
    [Range(1, 500)] public int EventDispatcherBatchSize { get; set; } = 50;
    [Range(1, 300)] public int EventLeaseSeconds { get; set; } = 30;
    [Range(1, 300)] public int EventRetryBaseSeconds { get; set; } = 2;
    [Range(1, 3600)] public int EventRetryMaxSeconds { get; set; } = 300;
    [Range(1, 100)] public int EventRetryAlertAfterAttempts { get; set; } = 10;
    public MessageSendRateLimitOptions SendRateLimit { get; set; } = new();
}

public sealed class MessageSendRateLimitOptions
{
    [Range(1, 3600)] public int WindowSeconds { get; set; } = 60;
    [Range(1, int.MaxValue)] public int PerUserLimit { get; set; } = 120;
    [Range(1, int.MaxValue)] public int PerParticipantLimit { get; set; } = 60;
    [Range(1, int.MaxValue)] public int PerSourceLimit { get; set; } = 200;
}
