using RoundTableMeeting.Abstraction.Enums;
namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record MeetingOperationResult<T>(MeetingOperationStatus Status, T? Data = default, IReadOnlyDictionary<string,string[]>? Errors = null, bool IsCreated = false);
