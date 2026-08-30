using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.IntegrationTests.Controllers;
using Serilog;
using Serilog.Core;

namespace RoundTableMeeting.IntegrationTests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    internal StructuredLogSink LogSink { get; } = new();
    internal ManualTimeProvider ManualTimeProvider { get; } = new(DateTimeOffset.UtcNow);
    private readonly string databaseDirectory;
    private readonly string databasePath;
    private readonly string staticDirectory;
    private readonly bool ownsDatabaseDirectory;

    public string DatabasePath => databasePath;

    public TestApplicationFactory()
    {
        databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "RoundTableMeetingTests",
            Guid.NewGuid().ToString("N"));
        databasePath = Path.Combine(databaseDirectory, "integration.db");
        staticDirectory = Path.Combine(databaseDirectory, "wwwroot");
        Directory.CreateDirectory(staticDirectory);
        File.WriteAllText(Path.Combine(staticDirectory, "origin-safe-asset.txt"), "static asset is public");
        ownsDatabaseDirectory = true;
    }

    internal TestApplicationFactory(string sharedDatabasePath)
    {
        databasePath = Path.GetFullPath(sharedDatabasePath);
        databaseDirectory = Path.GetDirectoryName(databasePath) ?? throw new ArgumentException("A database directory is required.", nameof(sharedDatabasePath));
        staticDirectory = Path.Combine(databaseDirectory, "wwwroot");
        ownsDatabaseDirectory = false;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(staticDirectory);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={DatabasePath};Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=False"
                , ["Authentication:AllowedOrigins:0"] = "http://localhost:5173"
                , ["Authentication:SeedAccounts:0:Username"] = "alice"
                , ["Authentication:SeedAccounts:0:Nickname"] = "Alice"
                , ["Authentication:SeedAccounts:0:Password"] = "test-password-alice"
                , ["Authentication:SeedAccounts:1:Username"] = "bob"
                , ["Authentication:SeedAccounts:1:Nickname"] = "Bob"
                , ["Authentication:SeedAccounts:1:Password"] = "test-password-bob"
            });
        });
        builder.ConfigureServices(services =>
        {
            var mvcBuilder = services.AddControllers();
            mvcBuilder.PartManager.ApplicationParts.Add(new AssemblyPart(typeof(TestController).Assembly));
            services.AddAuthorization(options => options.AddPolicy("AlwaysForbidden", policy => policy.RequireAuthenticatedUser().AddRequirements(new AlwaysForbiddenRequirement())));
            services.AddSingleton<IAuthorizationHandler, AlwaysForbiddenHandler>();
            services.PostConfigure<CookieAuthenticationOptions>(RoundTableMeeting.Authentication.AuthenticationConstants.Scheme, options => options.TimeProvider = ManualTimeProvider);
            services.AddSingleton<ILogEventSink>(LogSink);
            if (useUnavailableDatabase)
            {
                services.RemoveAll<IDatabaseAvailabilityChecker>();
                services.AddSingleton<IDatabaseAvailabilityChecker, TestDatabaseAvailabilityChecker>();
            }
        });
    }

    public TestApplicationFactory WithUnavailableDatabase()
    {
        var replacement = new TestApplicationFactory();
        replacement.useUnavailableDatabase = true;
        return replacement;
    }

    private bool useUnavailableDatabase;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            if (ownsDatabaseDirectory && Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
