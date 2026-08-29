namespace RoundTableMeeting.Abstraction.Persistence;

public interface IDatabaseAvailabilityChecker
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
