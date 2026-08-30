namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public sealed record CurrentUserDto(Guid UserId, string Username, string Nickname);
