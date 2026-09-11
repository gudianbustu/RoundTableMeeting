namespace RoundTableMeeting.Abstraction.Services;

public interface IAdministratorAccessService
{
    Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default);
}
