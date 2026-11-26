using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AccountAccess;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class ContainerCompatibilityTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory factory;

    public ContainerCompatibilityTests(TestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ScopedFrameworkServicesResolveAndShareDbContext()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var statusService = services.GetRequiredService<ISystemStatusService>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var initializer = services.GetRequiredService<IDatabaseInitializer>();
        var availabilityChecker = services.GetRequiredService<IDatabaseAvailabilityChecker>();
        var authenticationService = services.GetRequiredService<IAuthenticationService>();
        var accountSeeder = services.GetRequiredService<IAccountSeeder>();
        var dbContext = services.GetRequiredService<RoundTableMeetingDbContext>();

        Assert.Same(dbContext, services.GetRequiredService<RoundTableMeetingDbContext>());
        Assert.NotNull(statusService);
        Assert.NotNull(unitOfWork);
        Assert.NotNull(initializer);
        Assert.NotNull(availabilityChecker);
        Assert.IsType<AuthenticationService>(authenticationService);
        Assert.IsType<AccountSeeder>(accountSeeder);
        using var otherScope = factory.Services.CreateScope();
        Assert.NotSame(statusService, otherScope.ServiceProvider.GetRequiredService<ISystemStatusService>());
        Assert.NotSame(unitOfWork, otherScope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.NotSame(initializer, otherScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>());
        Assert.NotSame(availabilityChecker, otherScope.ServiceProvider.GetRequiredService<IDatabaseAvailabilityChecker>());
        Assert.True(await availabilityChecker.IsAvailableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IndependentScopesReceiveIndependentDbContexts()
    {
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        var firstContext = first.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();

        Assert.NotSame(firstContext, secondContext);
    }

    [Fact]
    public async Task ConfigureServicesReplacesConfigurationAndLoadsTestApplicationPart()
    {
        using var client = factory.CreateClient();
        Assert.True(File.Exists(factory.DatabasePath));

        var response = await client.GetAsync("/__test/application-part", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("loaded", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

}
