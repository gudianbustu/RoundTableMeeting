using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Application.SystemStatus;

namespace RoundTableMeeting.Application.Tests;

public sealed class SystemStatusServiceTests
{
    [Fact]
    public async Task AvailableDatabaseProducesAvailableStatus()
    {
        var service = new SystemStatusService(new FakeChecker(true), new FakeEnvironment("Testing"));
        var result = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("available", result.Status);
        Assert.Equal("available", result.Database.Status);
        Assert.Equal("Testing", result.Environment);
    }

    [Fact]
    public async Task UnavailableDatabaseProducesDegradedStatus()
    {
        var service = new SystemStatusService(new FakeChecker(false), new FakeEnvironment("Testing"));
        var result = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("degraded", result.Status);
        Assert.Equal("unavailable", result.Database.Status);
    }

    [Fact]
    public async Task CancellationIsForwarded()
    {
        using var cancellation = new CancellationTokenSource();
        var checker = new FakeChecker(true);
        var service = new SystemStatusService(checker, new FakeEnvironment("Testing"));
        await service.GetStatusAsync(cancellation.Token);
        Assert.Equal(cancellation.Token, checker.Token);
    }

    [Fact]
    public void StatusDtoContainsOnlyApprovedFields()
    {
        var properties = typeof(RoundTableMeeting.Abstraction.Contracts.SystemStatusDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["Status", "Database", "Environment"], properties);
        Assert.DoesNotContain("MigrationCompleted", properties);
        Assert.DoesNotContain("ConnectionString", properties);
    }

    [Fact]
    public async Task CancellationTokenIsNotReplacedWithDefault()
    {
        using var source = new CancellationTokenSource();
        var checker = new FakeChecker(true);
        var service = new SystemStatusService(checker, new FakeEnvironment("Testing"));

        await service.GetStatusAsync(source.Token);

        Assert.Equal(source.Token, checker.Token);
    }

    private sealed class FakeChecker(bool result) : IDatabaseAvailabilityChecker
    {
        public CancellationToken Token { get; private set; }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) { Token = cancellationToken; return Task.FromResult(result); }
    }

    private sealed class FakeEnvironment(string name) : IRuntimeEnvironment
    {
        public string EnvironmentName { get; } = name;
    }
}
