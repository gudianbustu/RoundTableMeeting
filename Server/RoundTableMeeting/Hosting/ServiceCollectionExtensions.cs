using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Application;
using RoundTableMeeting.Repository.Sqlite;
using RoundTableMeeting.Repository.Sqlite.Configuration;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.Hosting;

public static class ServiceCollectionExtensions
{
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
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
                new ObjectResult(ProblemDetailsFactory.CreateValidation(context.HttpContext, context.ModelState))
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    ContentTypes = { "application/problem+json" }
                };
        });

        return builder;
    }
}
