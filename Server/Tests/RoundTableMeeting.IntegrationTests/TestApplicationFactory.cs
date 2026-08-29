using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.IntegrationTests.Controllers;
using Serilog;
using Serilog.Core;

namespace RoundTableMeeting.IntegrationTests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    internal StructuredLogSink LogSink { get; } = new();
    private readonly string databaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "RoundTableMeetingTests",
        Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(databaseDirectory, "integration.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={DatabasePath};Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=False"
            });
        });
        builder.ConfigureServices(services =>
        {
            var mvcBuilder = services.AddControllers();
            mvcBuilder.PartManager.ApplicationParts.Add(new AssemblyPart(typeof(TestController).Assembly));
            services.AddSingleton<ILogEventSink>(LogSink);
            if (useUnavailableDatabase)
            {
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
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
