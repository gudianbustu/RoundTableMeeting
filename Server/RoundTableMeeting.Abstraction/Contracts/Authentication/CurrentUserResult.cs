namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public enum CurrentUserStatus { Success, Unauthenticated }

public sealed record CurrentUserResult(CurrentUserStatus Status, CurrentUserDto? User = null);
