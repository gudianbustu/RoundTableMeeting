namespace RoundTableMeeting.Domain.Enums;

public enum AgentTaskFailureCode
{
    ConfirmationTimeout,
    TransportClosed,
    ConnectionLeaseExpired,
    ConnectionResumed,
    ConnectionTakenOver,
    ServerRestart,
    AgentTransientFailure,
    AgentPermanentFailure,
    ContextUnavailable,
    InternalFailure
}

public enum AgentTaskStopReason
{
    AttemptsExhausted,
    PermanentFailure
}
