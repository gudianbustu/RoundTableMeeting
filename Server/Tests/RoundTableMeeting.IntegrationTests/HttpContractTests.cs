using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog.Events;

namespace RoundTableMeeting.IntegrationTests;

public sealed class HttpContractTests : IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient client;

    public HttpContractTests(TestApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task StatusEndpointReturnsRealAvailableContract()
    {
        var response = await client.GetAsync("/api/system/status");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("available", body.GetProperty("status").GetString());
        Assert.Equal("available", body.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("Testing", body.GetProperty("environment").GetString());
        Assert.False(body.TryGetProperty("migrationCompleted", out _));
    }

    [Fact]
    public async Task UnknownApiReturnsProblemDetails404()
    {
        var response = await client.GetAsync("/api/not-found");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("route_not_found", body.GetProperty("code").GetString());
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiDocumentIsGeneratedByAspNetCoreProvider()
    {
        var response = await client.GetAsync("/openapi/v1.json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("3.0", body.GetProperty("openapi").GetString());
        Assert.True(body.GetProperty("paths").TryGetProperty("/api/system/status", out _));
        Assert.False(body.TryGetProperty("servers", out _));
        Assert.True(body.GetProperty("components").GetProperty("schemas").TryGetProperty("ApiProblemDetails", out var problemSchema));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("code", out _));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("traceId", out _));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("errors", out _));
        var statusOperation = body.GetProperty("paths").GetProperty("/api/system/status").GetProperty("get");
        Assert.True(statusOperation.GetProperty("responses").TryGetProperty("500", out var errorResponse));
        Assert.Contains("ApiProblemDetails", errorResponse.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarPointsToTheSameOpenApiDocument()
    {
        var response = await client.GetAsync("/scalar/v1/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openapi", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v1", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestControllerExceptionUsesSafeProblemDetails()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var response = await isolatedClient.GetAsync("/__test/throw");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("test exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.InvalidOperationException", text, StringComparison.Ordinal);

        var errorLogs = isolatedFactory.LogSink.Events
            .Where(logEvent => logEvent.Level is LogEventLevel.Error or LogEventLevel.Fatal)
            .ToArray();
        var exceptionLog = Assert.Single(errorLogs);
        Assert.IsType<InvalidOperationException>(exceptionLog.Exception);
        Assert.Equal("test exception", exceptionLog.Exception?.Message);
        Assert.Equal(
            "RoundTableMeeting.Errors.ApiExceptionHandler",
            exceptionLog.Properties["SourceContext"].ToString().Trim('"'));
    }

    [Fact]
    public async Task InvalidTestModelUsesFieldValidationProblemDetails()
    {
        var response = await client.PostAsJsonAsync("/__test/validation", new { });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task W3CTraceparentIsReflectedInProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/throw");
        request.Headers.TryAddWithoutValidation("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("0123456789abcdef0123456789abcdef", body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task RuntimeDatabaseFailureReturnsDegradedStatus()
    {
        using var unavailableFactory = new TestApplicationFactory().WithUnavailableDatabase();
        using var unavailableClient = unavailableFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/status");
        request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        var response = await unavailableClient.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("unavailable", body.GetProperty("database").GetProperty("status").GetString());

        var warning = Assert.Single(unavailableFactory.LogSink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source.ToString().Trim('"') == typeof(TestDatabaseAvailabilityChecker).FullName);
        var requestLog = Assert.Single(unavailableFactory.LogSink.Events, logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source.ToString().Trim('"') == "Serilog.AspNetCore.RequestLoggingMiddleware"
            && logEvent.Properties.TryGetValue("requestPath", out var path)
            && path.ToString().Trim('"') == "/api/system/status");
        Assert.Equal("11111111111111111111111111111111", warning.Properties["traceId"].ToString().Trim('"'));
        Assert.Equal(warning.Properties["traceId"].ToString(), requestLog.Properties["traceId"].ToString());
        Assert.DoesNotContain("Data Source", warning.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestLogContainsNormalizedStructuredFields()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient();

        var response = await isolatedClient.GetAsync("/__test/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var requestLog = Assert.Single(isolatedFactory.LogSink.Events, logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source.ToString().Trim('"') == "Serilog.AspNetCore.RequestLoggingMiddleware");

        Assert.Equal("GET", requestLog.Properties["requestMethod"].ToString().Trim('"'));
        Assert.Equal("/__test/throw", requestLog.Properties["requestPath"].ToString().Trim('"'));
        Assert.Equal("500", requestLog.Properties["statusCode"].ToString());
        var elapsedText = requestLog.Properties["elapsedMilliseconds"].ToString().Trim('"');
        Assert.True(double.Parse(elapsedText, System.Globalization.CultureInfo.InvariantCulture) >= 0);
        Assert.DoesNotContain("Exception", requestLog.Properties.Keys);
        Assert.DoesNotContain("RequestBody", requestLog.Properties.Keys);
        Assert.DoesNotContain("ResponseBody", requestLog.Properties.Keys);
    }

    [Fact]
    public async Task MissingSpaArtifactReturns404InsteadOfInternalError()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/system/status");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
