namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public enum AuthenticateStatus { Success, InvalidCredentials, ValidationFailed }

public sealed record AuthenticateResult(AuthenticateStatus Status, CurrentUserDto? User = null, IReadOnlyDictionary<string, string[]>? Errors = null);
