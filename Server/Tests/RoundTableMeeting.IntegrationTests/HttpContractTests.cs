using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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
    public async Task AuthenticationCookieSupportsLoginMeAndLogout()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        var loginResponse = await isolatedClient.SendAsync(login);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Equal("alice", loginBody.GetProperty("username").GetString());
        Assert.Equal("no-store", loginResponse.Headers.CacheControl?.ToString());

        var meResponse = await isolatedClient.GetAsync("/api/auth/me");
        var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.Equal(loginBody.GetProperty("userId").GetString(), meBody.GetProperty("userId").GetString());

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        var logoutResponse = await isolatedClient.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        Assert.Equal("no-store", logoutResponse.Headers.CacheControl?.ToString());
        var afterLogout = await isolatedClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        Assert.Equal("unauthenticated", (await afterLogout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task StartupSeedsBothConfiguredAccounts()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "bob", password = "test-password-bob" });

        var response = await isolatedClient.SendAsync(login);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bob", body.GetProperty("username").GetString());
        Assert.Equal("Bob", body.GetProperty("nickname").GetString());
    }

    [Fact]
    public async Task InvalidCredentialsAndExternalOriginAreSafe()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        invalid.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        invalid.Content = JsonContent.Create(new { username = "missing", password = "wrong" });
        var invalidResponse = await isolatedClient.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Equal("invalid_credentials", (await invalidResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var external = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        external.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        external.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        var externalResponse = await isolatedClient.SendAsync(external);
        Assert.Equal(HttpStatusCode.Forbidden, externalResponse.StatusCode);
        Assert.Equal("origin_not_allowed", (await externalResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingUsernameAndWrongPasswordHaveEquivalentPublicErrors()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        async Task<JsonElement> SendAsync(string username, string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
            request.Content = JsonContent.Create(new { username, password });
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var missing = await SendAsync("missing", "wrong");
        var wrong = await SendAsync("alice", "wrong");
        Assert.Equal(missing.GetProperty("code").GetString(), wrong.GetProperty("code").GetString());
        Assert.Equal(missing.GetProperty("detail").GetString(), wrong.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task FallbackPolicyAndForbiddenPolicyUseIndependentControllers()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var defaultResponse = await isolatedClient.GetAsync("/__test/default-authorization");
        Assert.Equal(HttpStatusCode.Unauthorized, defaultResponse.StatusCode);
        Assert.Equal("unauthenticated", (await defaultResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        Assert.Equal(HttpStatusCode.OK, (await isolatedClient.SendAsync(login)).StatusCode);
        var forbiddenResponse = await isolatedClient.GetAsync("/__test/forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        var forbiddenBody = await forbiddenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", forbiddenBody.GetProperty("code").GetString());
        Assert.Equal("You do not have permission to access this resource.", forbiddenBody.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task LoginRejectsUnsupportedMediaTypeAndMissingOrigin()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var missingOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" }) };
        var missingOriginResponse = await isolatedClient.SendAsync(missingOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, missingOriginResponse.StatusCode);
        Assert.Equal("origin_not_allowed", (await missingOriginResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var unsupported = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        unsupported.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        unsupported.Content = new StringContent("username=alice&password=test-password-alice", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var unsupportedResponse = await isolatedClient.SendAsync(unsupported);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupportedResponse.StatusCode);
        Assert.Equal("unsupported_media_type", (await unsupportedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnonymousLogoutIsIdempotentAndNullOriginIsRejected()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        var logoutResponse = await isolatedClient.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var nullOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        nullOrigin.Headers.TryAddWithoutValidation("Origin", "null");
        nullOrigin.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        var nullResponse = await isolatedClient.SendAsync(nullOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, nullResponse.StatusCode);
        Assert.Equal("origin_not_allowed", (await nullResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmptyLoginFieldsReturnValidationProblemDetails()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Content = JsonContent.Create(new { username = "", password = "" });

        var response = await isolatedClient.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("username", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out _));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task InvalidAuthenticationCookieReturns401Without500()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation("Cookie", "RoundTableMeeting.Auth=invalid-cookie");

        var response = await isolatedClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("unauthenticated", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownApiWithOriginIsNotInterceptedByOriginMiddleware()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/unknown-with-origin");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        var response = await isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("route_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SuccessfulAuthenticationLogsDoNotContainPasswordCookieClaimOrRequestValues()
    {
        using var isolatedFactory = new TestApplicationFactory();
        using var isolatedClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        var response = await isolatedClient.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cookieHeader = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("RoundTableMeeting.Auth=", StringComparison.Ordinal));
        var cookieValue = cookieHeader.Split(';', 2)[0];
        var claimValue = body.GetProperty("userId").GetString()!;
        var requestBody = "{\"username\":\"alice\",\"password\":\"test-password-alice\"}";

        using var claimsRequest = new HttpRequestMessage(HttpMethod.Get, "/__test/auth-claims");
        claimsRequest.Headers.TryAddWithoutValidation("Cookie", cookieValue);
        var claimsResponse = await isolatedClient.SendAsync(claimsRequest);
        Assert.Equal(HttpStatusCode.OK, claimsResponse.StatusCode);

        var logText = string.Join("\n", isolatedFactory.LogSink.Events.SelectMany(log => new[]
        {
            log.RenderMessage(),
            string.Join(" ", log.Properties.Select(property => $"{property.Key}={property.Value}")),
            log.Exception?.ToString() ?? string.Empty
        }));
        Assert.DoesNotContain("test-password-alice", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("RoundTableMeeting.Auth", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(cookieValue, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(claimValue, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(requestBody, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedHostsSeedTheSameDatabaseIdempotently()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSharedDatabaseTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "shared.db");
        Directory.CreateDirectory(directory);

        try
        {
            using (var firstFactory = new TestApplicationFactory(databasePath))
            using (var firstClient = firstFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/api/system/status")).StatusCode);
                await WaitForSeedRowsAsync(databasePath);
            }

            var before = await ReadSeedRowsAsync(databasePath);

            using (var secondFactory = new TestApplicationFactory(databasePath))
            using (var secondClient = secondFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/api/system/status")).StatusCode);
                await WaitForSeedRowsAsync(databasePath);
            }

            var after = await ReadSeedRowsAsync(databasePath);
            Assert.Equal(2, before.Count);
            Assert.Equal(before, after);
        }
        finally
        {
            await DeleteDirectoryWhenAvailableAsync(directory);
        }
    }

    private static async Task DeleteDirectoryWhenAvailableAsync(string directory)
    {
        for (var attempt = 0; attempt < 50 && Directory.Exists(directory); attempt++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(50);
            }
        }

        Assert.False(Directory.Exists(directory));
    }

    private static async Task WaitForSeedRowsAsync(string databasePath)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(databasePath) && (await ReadSeedRowsAsync(databasePath)).Count == 2) return;
            await Task.Delay(20);
        }

        Assert.Equal(2, (await ReadSeedRowsAsync(databasePath)).Count);
    }

    private static async Task<List<(string Username, string Nickname, string PasswordHash)>> ReadSeedRowsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Username, Nickname, PasswordHash FROM UserAccounts ORDER BY NormalizedUsername;";
        var rows = new List<(string Username, string Nickname, string PasswordHash)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
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
            && source.ToString().Trim('"') == "Serilog.AspNetCore.RequestLoggingMiddleware"
            && logEvent.Properties.TryGetValue("requestPath", out var path)
            && path.ToString().Trim('"') == "/__test/throw");

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

    [Fact]
    public async Task AnonymousStaticAssetIsServedBeforeOriginAndAuthorization()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/origin-safe-asset.txt");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("static asset is public", await response.Content.ReadAsStringAsync());
    }
}
