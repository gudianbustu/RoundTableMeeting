namespace RoundTableMeeting.Abstraction.Contracts.UserManagement;

public enum UserManagementStatus
{
    Success,
    ValidationFailed,
    UsernameConflict,
    Unauthenticated,
    Forbidden,
    LastAdminRequired,
    UserNotFound
}

public sealed record UserManagementResult(
    UserManagementStatus Status,
    UserSummaryDto? User = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
