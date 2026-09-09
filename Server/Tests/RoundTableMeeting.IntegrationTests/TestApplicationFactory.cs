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

/// <summary>
/// 创建可控的集成测试应用宿主
/// </summary>
public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 获取测试期间捕获的结构化日志
    /// </summary>
    internal StructuredLogSink LogSink { get; } = new();
    /// <summary>
    /// 获取 Cookie 认证使用的可控时钟
    /// </summary>
    internal ManualTimeProvider ManualTimeProvider { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>
    /// 测试数据库及网站根目录所在目录
    /// </summary>
    private readonly string databaseDirectory;
    /// <summary>
    /// 测试宿主使用的 SQLite 数据库路径
    /// </summary>
    private readonly string databasePath;
    /// <summary>
    /// 集成测试使用的静态文件根目录
    /// </summary>
    private readonly string staticDirectory;
    /// <summary>
    /// 是否由当前工厂负责清理临时目录
    /// </summary>
    private readonly bool ownsDatabaseDirectory;
    /// <summary>
    /// 可选的 Agent 等待超时覆盖值
    /// </summary>
    private readonly int? agentWaitTimeoutSeconds;

    /// <summary>
    /// 测试宿主使用的 SQLite 数据库路径
    /// </summary>
    public string DatabasePath => databasePath;

    /// <summary>
    /// 创建使用隔离临时数据库的测试工厂
    /// </summary>
    public TestApplicationFactory()
        : this((int?)null)
    {
    }

    /// <summary>
    /// 创建带有 Agent 等待超时覆盖的测试工厂
    /// </summary>
    internal TestApplicationFactory(int? agentWaitTimeoutSeconds)
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
        this.agentWaitTimeoutSeconds = agentWaitTimeoutSeconds;
    }

    /// <summary>
    /// 创建复用指定数据库的测试工厂
    /// </summary>
    internal TestApplicationFactory(string sharedDatabasePath)
    {
        databasePath = Path.GetFullPath(sharedDatabasePath);
        databaseDirectory = Path.GetDirectoryName(databasePath) ?? throw new ArgumentException("A database directory is required.", nameof(sharedDatabasePath));
        staticDirectory = Path.Combine(databaseDirectory, "wwwroot");
        ownsDatabaseDirectory = false;
        agentWaitTimeoutSeconds = null;
    }

    /// <summary>
    /// 配置集成测试宿主及其替代服务
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(staticDirectory);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={DatabasePath};Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=False"
                , ["Authentication:AllowedOrigins:0"] = "http://localhost:5173"
                , ["Authentication:SeedAccounts:0:Username"] = "alice"
                , ["Authentication:SeedAccounts:0:Nickname"] = "Alice"
                , ["Authentication:SeedAccounts:0:Password"] = "test-password-alice"
                , ["Authentication:SeedAccounts:1:Username"] = "bob"
                , ["Authentication:SeedAccounts:1:Nickname"] = "Bob"
                , ["Authentication:SeedAccounts:1:Password"] = "test-password-bob"
            };
            if (agentWaitTimeoutSeconds is int waitTimeoutSeconds)
            {
                values["AgentCollaboration:WaitTimeoutSeconds"] = waitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            configuration.AddInMemoryCollection(values);
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

    /// <summary>
    /// 创建数据库不可用场景的测试工厂
    /// </summary>
    public TestApplicationFactory WithUnavailableDatabase()
    {
        var replacement = new TestApplicationFactory();
        replacement.useUnavailableDatabase = true;
        return replacement;
    }

    /// <summary>
    /// 是否替换为数据库不可用的测试服务
    /// </summary>
    private bool useUnavailableDatabase;

    /// <summary>
    /// 清理测试宿主拥有的临时资源
    /// </summary>
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
