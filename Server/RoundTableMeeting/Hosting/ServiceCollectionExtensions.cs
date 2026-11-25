using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Materal.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Application;
using RoundTableMeeting.Application.AccountAccess;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Meetings;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Repository.Sqlite;
using RoundTableMeeting.Repository.Sqlite.Configuration;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Hosting;

public static class ServiceCollectionExtensions
{
	public static WebApplicationBuilder AddRoundTableMeetingServices(this WebApplicationBuilder builder)
	{
		builder.Services.AddAutoService(typeof(RoundTableMeeting.Application.AssemblyMarker).Assembly, typeof(RoundTableMeeting.Repository.Sqlite.AssemblyMarker).Assembly);
		builder.Services.AddOptions<AgentCollaborationOptions>().Bind(builder.Configuration.GetSection("AgentCollaboration")).ValidateDataAnnotations()
			.Validate((AgentCollaborationOptions options) => options.MinimumContextCharacterLimit <= options.DefaultContextCharacterLimit, "Minimum context character limit cannot exceed the default.")
			.Validate((AgentCollaborationOptions options) => options.DefaultContextCharacterLimit <= options.MaximumContextCharacterLimit, "Default context character limit cannot exceed the maximum.")
			.ValidateOnStart();
		builder.Services.AddSingleton((IServiceProvider sp) => sp.GetRequiredService<IOptions<AgentCollaborationOptions>>().Value);
		builder.Services.AddScoped<AgentCollaborationService>();
		builder.Services.AddScoped((Func<IServiceProvider, IAgentCollaborationService>)((IServiceProvider sp) => sp.GetRequiredService<AgentCollaborationService>()));
		builder.Services.AddScoped<AgentContextBuilder>();
		builder.Services.AddScoped<IResolvedMessageAppendCore, ResolvedMessageAppendCore>();
		builder.Services.AddSingleton<IAgentJoinRateLimiter, AgentJoinRateLimiter>();
        builder.Services.AddSingleton<IAgentResumeRateLimiter, AgentResumeRateLimiter>();
		builder.Services.AddSingleton<IAgentConnectionRegistry, AgentConnectionRegistry>();
		builder.Services.AddSingleton<IAgentJoinCoordinator, AgentJoinCoordinator>();
		builder.Services.AddSingleton<IAgentConnectionCompensator, AgentConnectionCompensator>();
		builder.Services.AddSingleton<IAgentActivityNotifier, AgentActivityNotifier>();
		builder.Services.AddSingleton<IAgentTaskSignalRegistry, AgentTaskSignalRegistry>();
		builder.Services.AddScoped<MeetingAgentContext>();
        builder.Services.AddScoped<CurrentMcpSessionService>();
        builder.Services.AddScoped<MeetingNicknameService>();
        builder.Services.AddScoped<McpLongText>();
        builder.Services.AddHostedService<AgentConnectionStartupReconciler>();
        builder.Services.AddSingleton<McpTransferTickets>();
        var publicUrl = builder.Configuration["Federation:PublicSiteUrl"];
        builder.Services.AddSingleton(new RoundTableMeeting.Abstraction.Contracts.Meetings.MeetingSiteAddress(
            string.IsNullOrWhiteSpace(publicUrl) ? null : PeerTransport.Normalize(publicUrl).AbsoluteUri));
        builder.Services.AddSingleton<PeerTransport>();
        builder.Services.AddScoped<McpTransportSessionAccessor>();
		builder.Services.AddScoped((Func<IServiceProvider, IMcpTransportSessionAccessor>)((IServiceProvider sp) => sp.GetRequiredService<McpTransportSessionAccessor>()));
		JsonSerializerOptions jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		jsonSerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
		jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
		builder.Services.AddMcpServer(delegate(McpServerOptions options)
		{
			options.ServerInfo = new Implementation
			{
				Name = "round-table-meeting-agent-messaging",
				Version = "mcp-preview-v2"
			};
		})
#pragma warning disable MCPEXP002
		.WithHttpTransport(options =>
		{
			options.ConfigureSessionOptions = (context, sessionOptions, _) =>
            {
                SimpleMeetingTools.Configure(context, sessionOptions, jsonSerializerOptions);
                return Task.CompletedTask;
            };
            options.RunSessionHandler = async (context, server, cancellationToken) =>
			{
				var connectionRegistry = context.RequestServices.GetRequiredService<IAgentConnectionRegistry>();
				var connectionCompensator = context.RequestServices.GetRequiredService<IAgentConnectionCompensator>();
				var activityNotifier = context.RequestServices.GetRequiredService<IAgentActivityNotifier>();
				var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
				var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
				var transportSession = McpTransportSessionAccessor.FromSessionId(server.SessionId);
				try
				{
					await server.RunAsync(cancellationToken);
				}
				finally
				{
					if (transportSession is { IsEmpty: false } key)
					{
						foreach (var registration in connectionRegistry.RemoveRegistrations(key))
						{
							await connectionCompensator.DisconnectAsync(registration, timeProvider.GetUtcNow(), CancellationToken.None);
							await using var scope = scopeFactory.CreateAsyncScope();
							var activity = await scope.ServiceProvider.GetRequiredService<IAgentActivityService>()
								.FindAsync(registration.MeetingId, registration.ParticipantId, CancellationToken.None);
							if (activity is not null) await activityNotifier.NotifyAsync(activity, notifyPresence: true, CancellationToken.None);
						}
					}
				}
			};
		})
#pragma warning restore MCPEXP002
		.WithTools<CurrentMcpTools>(jsonSerializerOptions);
		builder.Services.AddOptions<SqliteDatabaseOptions>().Bind(builder.Configuration.GetSection("Database")).ValidateDataAnnotations()
			.Validate((SqliteDatabaseOptions options) => SqliteDatabaseOptions.TryValidate(options.ConnectionString, out string _), "Database connection settings are invalid.")
			.ValidateOnStart();
		builder.Services.AddOptions<RoundTableMeeting.Authentication.AuthenticationOptions>().Bind(builder.Configuration.GetSection("Authentication")).ValidateDataAnnotations()
			.Validate((RoundTableMeeting.Authentication.AuthenticationOptions options) => options.Validate(out string _), "Authentication settings are invalid.")
			.ValidateOnStart();
		builder.Services.AddOptions<MeetingJoinRateLimitOptions>().Bind(builder.Configuration.GetSection("MeetingJoinRateLimit")).ValidateDataAnnotations()
			.ValidateOnStart();
		builder.Services.AddOptions<RealtimeChatOptions>().Bind(builder.Configuration.GetSection("RealtimeChat")).ValidateDataAnnotations()
			.Validate((RealtimeChatOptions options) => options.HistoryPageSize <= options.MaxHistoryPageSize, "History page size cannot exceed maximum page size.")
			.Validate((RealtimeChatOptions options) => options.EventRetryMaxSeconds >= options.EventRetryBaseSeconds, "Event retry maximum must not be lower than the base delay.")
			.Validate(delegate(RealtimeChatOptions options)
			{
				MessageSendRateLimitOptions sendRateLimit = options.SendRateLimit;
				return sendRateLimit != null && sendRateLimit.WindowSeconds > 0 && sendRateLimit.PerUserLimit > 0 && sendRateLimit.PerParticipantLimit > 0 && sendRateLimit.PerSourceLimit > 0;
			}, "Message send rate limits are invalid.")
			.ValidateOnStart();
		builder.Services.AddSingleton((IServiceProvider sp) => sp.GetRequiredService<IOptions<RealtimeChatOptions>>().Value);
        builder.Services.AddOptions<AttachmentOptions>().Bind(builder.Configuration.GetSection(AttachmentOptions.SectionName))
            .ValidateDataAnnotations().Validate(o => !string.IsNullOrWhiteSpace(o.RootPath), "Attachment root is required.").ValidateOnStart();
        builder.Services.AddSingleton<IAttachmentStore, LocalAttachmentStore>();
        builder.Services.AddSingleton<IMeetingDeletionResourceCleanup, AttachmentMeetingCleanup>();
        builder.Services.AddHostedService<AttachmentMaintenanceService>();
		builder.Services.AddSignalR();
		builder.Services.AddSingleton<MeetingPresenceRegistry>();
		builder.Services.AddSingleton<IMeetingRealtimeTermination, MeetingRealtimeTermination>();
		builder.Services.AddSingleton<MessageSendRateLimiter>();
		builder.Services.AddScoped<MessageSendRateLimitFilter>();
		builder.Services.AddSingleton<MeetingRealtimeGate>();
        builder.Services.AddSingleton<IMeetingRealtimeGate>(sp => sp.GetRequiredService<MeetingRealtimeGate>());
        builder.Services.AddSingleton<IMeetingFileTransferGate>(sp => sp.GetRequiredService<MeetingRealtimeGate>());
		builder.Services.AddSingleton<IMessageMentionParser, MessageMentionParser>();
		builder.Services.AddScoped<RealtimeChatService>();
		builder.Services.AddScoped((Func<IServiceProvider, IMessageAppendService>)((IServiceProvider sp) => sp.GetRequiredService<RealtimeChatService>()));
		builder.Services.AddScoped((Func<IServiceProvider, IMessageHistoryService>)((IServiceProvider sp) => sp.GetRequiredService<RealtimeChatService>()));
		builder.Services.AddScoped((Func<IServiceProvider, IMeetingChatSubscriptionService>)((IServiceProvider sp) => sp.GetRequiredService<RealtimeChatService>()));
		builder.Services.AddSingleton<IMessageEventConsumerCatalog, MessageEventConsumerCatalog>();
		builder.Services.AddScoped<RealtimeMessageBroadcastConsumer>();
		builder.Services.AddSingleton(new MessageEventConsumerDescriptor("realtime-broadcast-v1", typeof(RealtimeMessageBroadcastConsumer)));

		builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
		builder.Services.AddSingleton<PasswordVerificationService>();
		builder.Services.AddHttpContextAccessor();
		builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
		builder.Services.AddAuthentication("RoundTableMeetingCookie").AddCookie("RoundTableMeetingCookie", delegate(CookieAuthenticationOptions options)
		{
			options.Cookie.Name = "RoundTableMeeting.Auth";
			options.Cookie.Path = "/";
			options.Cookie.HttpOnly = true;
			options.Cookie.SameSite = SameSiteMode.Lax;
			options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
			options.ExpireTimeSpan = TimeSpan.FromHours(8);
			options.SlidingExpiration = false;
			options.Events.OnRedirectToLogin = (RedirectContext<CookieAuthenticationOptions> context) => AuthenticationProblemDetailsExtensions.WriteAsync(context.HttpContext, 401, "unauthenticated");
			options.Events.OnRedirectToAccessDenied = (RedirectContext<CookieAuthenticationOptions> context) => AuthenticationProblemDetailsExtensions.WriteAsync(context.HttpContext, 403, "forbidden");
		});
		builder.Services.AddScoped<IAuthorizationHandler, AdministratorAuthorizationHandler>();
		builder.Services.AddAuthorization(delegate(AuthorizationOptions options)
		{
			options.AddPolicy("AdminOnly", policy => policy.RequireAuthenticatedUser().AddRequirements(new AdministratorRequirement()));
			options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
		});
		builder.Services.AddSingleton<SqliteConnectionInterceptor>();
		builder.Services.AddDbContext<RoundTableMeetingDbContext>(delegate(IServiceProvider serviceProvider, DbContextOptionsBuilder options)
		{
			SqliteDatabaseOptions value = serviceProvider.GetRequiredService<IOptions<SqliteDatabaseOptions>>().Value;
			string connectionString = SqlitePathResolver.NormalizeConnectionString(value.ConnectionString);
			options.UseSqlite(connectionString, delegate(SqliteDbContextOptionsBuilder sqlite)
			{
				sqlite.MigrationsAssembly(typeof(RoundTableMeetingDbContext).Assembly.FullName);
			});
			options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
			options.AddInterceptors(serviceProvider.GetRequiredService<SqliteConnectionInterceptor>());
			options.EnableDetailedErrors(builder.Environment.IsDevelopment());
			options.EnableSensitiveDataLogging(sensitiveDataLoggingEnabled: false);
		});
		builder.Services.AddSingleton((IRuntimeEnvironment)new RuntimeEnvironment(builder.Environment.EnvironmentName));
		builder.Services.AddSingleton(TimeProvider.System);
		builder.Services.AddSingleton<MeetingDeletionSignal>();
		builder.Services.AddSingleton((Func<IServiceProvider, IMeetingDeletionSignal>)((IServiceProvider services) => services.GetRequiredService<MeetingDeletionSignal>()));
		builder.Services.AddHostedService<MeetingDeletionRecoveryService>();

		builder.Services.AddHostedService<MessageEventDispatcherService>();
		builder.Services.AddSingleton<MeetingJoinRateLimiter>();
		builder.Services.AddScoped<MeetingJoinRateLimitFilter>();
		builder.Services.AddExceptionHandler<ApiExceptionHandler>();
		builder.Services.AddProblemDetails();
		builder.Services.Configure(delegate(ApiBehaviorOptions options)
		{
			options.InvalidModelStateResponseFactory = (ActionContext context) => ApiProblemDetailsWriter.CreateValidationResult(context.HttpContext, context.ModelState);
		});
		return builder;
	}
}
