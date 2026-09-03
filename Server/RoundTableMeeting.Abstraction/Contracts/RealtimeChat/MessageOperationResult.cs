namespace RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
public enum MessageOperationStatus { Created, Replayed, Success, SubmissionMismatch, MeetingNotFound, Unauthenticated, ValidationFailed, Conflict }
public sealed record MessageOperationResult<T>(MessageOperationStatus Status, T? Data = default, IReadOnlyDictionary<string,string[]>? Errors = null);
