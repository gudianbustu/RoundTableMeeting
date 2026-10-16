namespace RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
public enum MessageOperationStatus { Created, Replayed, Success, SubmissionMismatch, MeetingNotFound, MeetingDeleting, MeetingDeleted, Unauthenticated, ValidationFailed, Conflict, MembershipLeft, MessageNotFound, RecipientUnavailable, ReplyScopeMismatch, AttachmentUnavailable }
public sealed record MessageOperationResult<T>(MessageOperationStatus Status, T? Data = default, IReadOnlyDictionary<string,string[]>? Errors = null);
