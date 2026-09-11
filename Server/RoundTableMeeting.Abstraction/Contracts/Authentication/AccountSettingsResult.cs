namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public enum AccountSettingsStatus { Success, Unauthenticated, ValidationFailed }
public sealed record AccountSettingsResult(AccountSettingsStatus Status, CurrentUserDto? User = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
