using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Application.AccountAccess;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Application;
using RoundTableMeeting.Repository.Sqlite;
using RoundTableMeeting.Repository.Sqlite.Configuration;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Meetings;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Hosting;

/// <summary>
/// 注册应用的持久化、认证和 Web 服务
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册应用依赖和 Web 基础设施
    /// </summary>
    public static WebApplicationBuilder AddRoundTableMeetingServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddAutoService(
            typeof(RoundTableMeeting.Application.AssemblyMarker).Assembly,
            typeof(RoundTableMeeting.Repository.Sqlite.AssemblyMarker).Assembly);

        builder.Services.AddOptions<SqliteDatabaseOptions>()
            .Bind(builder.Configuration.GetSection(SqliteDatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => SqliteDatabaseOptions.TryValidate(options.ConnectionString, out _), "Database connection settings are invalid.")
            .ValidateOnStart();

        builder.Services.AddOptions<AuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(AuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Validate(out _), "Authentication settings are invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<MeetingJoinRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(MeetingJoinRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<RealtimeChatOptions>()
            .Bind(builder.Configuration.GetSection(RealtimeChatOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HistoryPageSize <= options.MaxHistoryPageSize, "History page size cannot exceed maximum page size.")
            .Validate(options => options.EventRetryMaxSeconds >= options.EventRetryBaseSeconds, "Event retry maximum must not be lower than the base delay.")
            .Validate(options => options.SendRateLimit is { WindowSeconds: > 0, PerUserLimit: > 0, PerParticipantLimit: > 0, PerSourceLimit: > 0 }, "Message send rate limits are invalid.")
            .ValidateOnStart();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RealtimeChatOptions>>().Value);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<MeetingPresenceRegistry>();
        builder.Services.AddSingleton<IMeetingRealtimeTermination, MeetingRealtimeTermination>();
        builder.Services.AddSingleton<MessageSendRateLimiter>();
        builder.Services.AddScoped<MessageSendRateLimitFilter>();
        builder.Services.AddSingleton<IMeetingRealtimeGate, MeetingRealtimeGate>();
        builder.Services.AddSingleton<IMessageMentionParser, MessageMentionParser>();
        builder.Services.AddScoped<RealtimeChatService>();
        builder.Services.AddScoped<IMessageAppendService>(sp => sp.GetRequiredService<RealtimeChatService>());
        builder.Services.AddScoped<IMessageHistoryService>(sp => sp.GetRequiredService<RealtimeChatService>());
        builder.Services.AddScoped<IMeetingChatSubscriptionService>(sp => sp.GetRequiredService<RealtimeChatService>());
        builder.Services.AddSingleton<IMessageEventConsumerCatalog, MessageEventConsumerCatalog>();
        builder.Services.AddScoped<RealtimeMessageBroadcastConsumer>();
        builder.Services.AddSingleton(new MessageEventConsumerDescriptor("realtime-broadcast-v1", typeof(RealtimeMessageBroadcastConsumer)));
        builder.Services.AddSingleton<IPasswordHasher<RoundTableMeeting.Domain.Entities.UserAccount>, PasswordHasher<RoundTableMeeting.Domain.Entities.UserAccount>>();
        builder.Services.AddSingleton<PasswordVerificationService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<RoundTableMeeting.Abstraction.Services.ICurrentUserContext, HttpCurrentUserContext>();
        builder.Services.AddAuthentication(AuthenticationConstants.Scheme)
            .AddCookie(AuthenticationConstants.Scheme, options =>
            {
                options.Cookie.Name = AuthenticationConstants.CookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => AuthenticationProblemDetailsExtensions.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated");
                options.Events.OnRedirectToAccessDenied = context => AuthenticationProblemDetailsExtensions.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "forbidden");
            });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddSingleton<SqliteConnectionInterceptor>();
        builder.Services.AddDbContext<RoundTableMeetingDbContext>((serviceProvider, options) =>
        {
            var databaseOptions = serviceProvider.GetRequiredService<IOptions<SqliteDatabaseOptions>>().Value;
            var connectionString = SqlitePathResolver.NormalizeConnectionString(databaseOptions.ConnectionString);
            options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(RoundTableMeetingDbContext).Assembly.FullName));
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            options.AddInterceptors(serviceProvider.GetRequiredService<SqliteConnectionInterceptor>());
            options.EnableDetailedErrors(builder.Environment.IsDevelopment());
            options.EnableSensitiveDataLogging(false);
        });

        builder.Services.AddSingleton<IRuntimeEnvironment>(new RuntimeEnvironment(builder.Environment.EnvironmentName));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MeetingDeletionSignal>();
        builder.Services.AddSingleton<RoundTableMeeting.Abstraction.Services.IMeetingDeletionSignal>(services => services.GetRequiredService<MeetingDeletionSignal>());
        builder.Services.AddHostedService<MeetingDeletionRecoveryService>();
        builder.Services.AddHostedService<MessageEventDispatcherService>();
        builder.Services.AddSingleton<MeetingJoinRateLimiter>();
        builder.Services.AddScoped<MeetingJoinRateLimitFilter>();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
                ApiProblemDetailsWriter.CreateValidationResult(context.HttpContext, context.ModelState);
        });

        return builder;
    }
}
