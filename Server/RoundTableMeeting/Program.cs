using System.Diagnostics;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Hosting;
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

    builder.Services.AddControllers();
    builder.Services.AddOpenApi(options =>
    {
        options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Servers = null;
            return Task.CompletedTask;
        });
    });
    builder.AddRoundTableMeetingServices();

    var app = builder.Build();
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
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
    app.UseAuthorization();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapOpenApi();
    app.MapControllers();
    app.Map("/api/{**path}", async (HttpContext context) =>
    {
        var problem = new RoundTableMeeting.Errors.ApiProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Type = "/problems/route_not_found",
            Title = "Route not found",
            Detail = "The requested API route was not found.",
            Instance = context.Request.Path,
            Code = "route_not_found",
            TraceId = RoundTableMeeting.Errors.TraceIdResolver.Resolve(context)
        };
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await Results.Json(problem, contentType: "application/problem+json").ExecuteAsync(context);
    });
    app.MapScalarApiReference("/scalar/v1", options => options.WithOpenApiRoutePattern("/openapi/v1.json"));
    app.MapFallback(async context =>
    {
        var acceptsHtml = context.Request.GetTypedHeaders().Accept?.Any(value =>
            value.MediaType.HasValue && value.MediaType.Value.Equals("text/html", StringComparison.OrdinalIgnoreCase)) == true;
        var isPageRequest = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var excluded = context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/openapi")
            || context.Request.Path.StartsWithSegments("/scalar");
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
    });
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

public partial class Program;
