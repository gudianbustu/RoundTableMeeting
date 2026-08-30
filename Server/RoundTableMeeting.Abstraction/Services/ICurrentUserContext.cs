namespace RoundTableMeeting.Abstraction.Services;

public interface ICurrentUserContext
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }
}
