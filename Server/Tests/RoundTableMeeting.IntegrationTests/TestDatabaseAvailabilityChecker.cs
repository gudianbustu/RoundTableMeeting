using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Persistence;

namespace RoundTableMeeting.IntegrationTests;

public sealed class TestDatabaseAvailabilityChecker(
    ILogger<TestDatabaseAvailabilityChecker> logger) : IDatabaseAvailabilityChecker
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Test database availability check failed.");
        return Task.FromResult(false);
    }
}
