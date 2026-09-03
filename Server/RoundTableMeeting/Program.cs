using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Hosting;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.OpenApi;
using RoundTableMeeting.RealtimeChat;
using Serilog;
using Serilog.Context;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.With<RoundTableMeeting.Errors.RequestLogPropertyEnricher>()
        .Enrich.FromLogContext());

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });
    builder.Services.AddOpenApi(options =>
    {
        options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
        options.AddOperationTransformer<MeetingOpenApiOperationTransformer>();
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Servers = null;
            return Task.CompletedTask;
        });
    });
    builder.AddRoundTableMeetingServices();

    var app = builder.Build();
    // 在接受请求前完成迁移和幂等账号播种
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        var authenticationOptions = scope.ServiceProvider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        await scope.ServiceProvider.GetRequiredService<IAccountSeeder>().SeedAsync(
            authenticationOptions.SeedAccounts.Select(account => new SeedAccountInput(account.Username!, account.Nickname!, account.Password!)).ToArray());
    }

    app.Use(async (context, next) =>
    {
        using (LogContext.PushProperty("traceId", RoundTableMeeting.Errors.TraceIdResolver.Resolve(context)))
        {
            await next();
        }
    });
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (_, _, _) => Serilog.Events.LogEventLevel.Information;
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("traceId", RoundTableMeeting.Errors.TraceIdResolver.Resolve(httpContext));
            diagnosticContext.Set("requestMethod", httpContext.Request.Method);
            diagnosticContext.Set("requestPath", httpContext.Request.Path.Value ?? string.Empty);
        };
    });
    app.UseHttpsRedirection();
    app.UseRouting();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseMiddleware<SensitiveResponseHeadersMiddleware>();
    app.UseMiddleware<OriginValidationMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapOpenApi().AllowAnonymous();
    app.MapControllers();
    app.MapHub<MeetingChatHub>("/hubs/meeting-chat")
        .RequireAuthorization()
        .WithMetadata(new RequireBrowserOriginAttribute(allowMissingOriginForGet: true), new NoStoreResponseAttribute());
    app.Map("/api/{**path}", async (HttpContext context) =>
    {
        await RoundTableMeeting.Errors.ApiProblemDetailsWriter.WriteAsync(context, StatusCodes.Status404NotFound, "route_not_found");
    }).AllowAnonymous();
    app.MapScalarApiReference("/scalar/v1", options => options.WithOpenApiRoutePattern("/openapi/v1.json")).AllowAnonymous();
    // 仅对浏览器页面请求提供 SPA，API 和文档路径保持真实的 404 响应
    app.MapFallback(async context =>
    {
        var acceptsHtml = context.Request.GetTypedHeaders().Accept?.Any(value =>
            value.MediaType.HasValue && value.MediaType.Value.Equals("text/html", StringComparison.OrdinalIgnoreCase)) == true;
        var isPageRequest = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var excluded = context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/openapi")
            || context.Request.Path.StartsWithSegments("/scalar")
            || context.Request.Path.StartsWithSegments("/hubs");
        if (!isPageRequest || !acceptsHtml || excluded)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var indexPath = Path.Combine(webRootPath, "index.html");
        if (!File.Exists(indexPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await context.Response.SendFileAsync(indexPath);
    }).AllowAnonymous();
    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(
        "Application terminated unexpectedly. ExceptionType: {ExceptionType}",
        exception.GetType().FullName ?? exception.GetType().Name);
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// 提供应用或实验的顶层启动入口
/// </summary>
public partial class Program;
