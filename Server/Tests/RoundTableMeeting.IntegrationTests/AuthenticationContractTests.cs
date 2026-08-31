using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AuthenticationContractTests
{
    [Theory]
    [InlineData("http://localhost:5173/path")]
    [InlineData("http://localhost:5173/?query=value")]
    [InlineData("http://user:password@localhost:5173")]
    [InlineData("http://localhost:5173/#fragment")]
    public void InvalidAllowedOriginIsRejectedWithoutEchoingValue(string origin)
    {
        var options = new RoundTableMeeting.Authentication.AuthenticationOptions
        {
            AllowedOrigins = [origin],
            SeedAccounts = [new() { Username = "alice", Nickname = "Alice", Password = "secret" }]
        };

        Assert.False(options.Validate(out var error));
        Assert.DoesNotContain(origin, error, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingSlashAndDuplicateAllowedOriginsAreRejected()
    {
        var trailingSlash = new RoundTableMeeting.Authentication.AuthenticationOptions
        {
            AllowedOrigins = ["http://localhost:5173/"],
            SeedAccounts = [new() { Username = "alice", Nickname = "Alice", Password = "secret" }]
        };
        var duplicate = new RoundTableMeeting.Authentication.AuthenticationOptions
        {
            AllowedOrigins = ["http://localhost:5173", "HTTP://LOCALHOST:5173"],
            SeedAccounts = [new() { Username = "alice", Nickname = "Alice", Password = "secret" }]
        };

        Assert.False(trailingSlash.Validate(out _));
        Assert.False(duplicate.Validate(out _));
    }

    [Fact]
    public async Task LoginCookieHasExpectedSessionAttributesAndErrorsAreNotCached()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });

        var response = await client.SendAsync(request);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("RoundTableMeeting.Auth=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        var missing = await client.GetAsync("/api/auth/me");
        Assert.Contains("no-store", missing.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiDeclaresAuthenticationResponses()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/api/auth/login", "/api/auth/me", "/api/auth/logout" }) Assert.True(paths.TryGetProperty(path, out _));
        Assert.Contains("400", paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").EnumerateObject().Select(p => p.Name));
        Assert.Contains("401", paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").EnumerateObject().Select(p => p.Name));
        Assert.Contains("403", paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").EnumerateObject().Select(p => p.Name));
        Assert.Contains("415", paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").EnumerateObject().Select(p => p.Name));
        Assert.Contains("500", paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task MissingAccountInvalidatesCookieAndReturns401WithoutRedirect()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);

        await using (var connection = new SqliteConnection("Data Source=" + factory.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM UserAccounts WHERE NormalizedUsername = 'ALICE';";
            await command.ExecuteNonQueryAsync();
        }

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RoundTableMeeting.Auth=;", string.Join(";", response.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication500ResponseIsNotCached()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/test-failure");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", response.Headers.Pragma.ToString());
        Assert.Equal("internal_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CookieExpiresAfterEightHoursUsingManualTimeProvider()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);

        factory.ManualTimeProvider.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResolvedRepositoryAndUnitOfWorkShareTheScopedDbContext()
    {
        using var factory = new TestApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var account = UserAccount.Create("scope-contract", "Scope Contract");
        account.InitializePasswordHash("hash");

        unitOfWork.RegisterAdd(account);
        Assert.Equal(1, await unitOfWork.CommitAsync());
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>());
        Assert.Equal(account.ID, (await repository.FindByNormalizedUsernameAsync("SCOPE-CONTRACT"))?.ID);
    }

    [Fact]
    public async Task LoginCookieUsesOnlyTheFrozenCustomUserIdClaim()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        login.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        login.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);

        var claims = await client.GetFromJsonAsync<JsonElement>("/__test/auth-claims");
        Assert.True(claims.GetProperty("hasCustomUserId").GetBoolean());
        Assert.False(claims.GetProperty("hasNameIdentifier").GetBoolean());
        Assert.Equal(["round_table_meeting_user_id"], claims.GetProperty("claimTypes").EnumerateArray().Select(value => value.GetString()).OfType<string>().ToArray());
    }

    [Fact]
    public async Task Authentication403And415ResponsesAreNotCached()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var forbidden = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        forbidden.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        forbidden.Content = JsonContent.Create(new { username = "alice", password = "test-password-alice" });
        var forbiddenResponse = await client.SendAsync(forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        Assert.Contains("no-store", forbiddenResponse.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", forbiddenResponse.Headers.Pragma.ToString());

        using var unsupported = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        unsupported.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        unsupported.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json-patch+json");
        var unsupportedResponse = await client.SendAsync(unsupported);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupportedResponse.StatusCode);
        Assert.Contains("no-store", unsupportedResponse.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", unsupportedResponse.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task LogoutUsesOriginMetadataWithoutRequiringJsonBody()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Content = new StringContent("logout", System.Text.Encoding.UTF8, "text/plain");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AllAuthenticationOperationsDeclareStableErrorResponses()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = document.GetProperty("paths");
        var expected = new Dictionary<string, (string Method, string[] Codes)>
        {
            ["/api/auth/login"] = ("post", ["400", "401", "403", "415", "500"]),
            ["/api/auth/me"] = ("get", ["401", "500"]),
            ["/api/auth/logout"] = ("post", ["403", "500"])
        };

        foreach (var item in expected)
        {
            var responses = paths.GetProperty(item.Key).GetProperty(item.Value.Method).GetProperty("responses");
            foreach (var code in item.Value.Codes) Assert.True(responses.TryGetProperty(code, out _), $"Missing {code} response for {item.Key}.");
        }
    }
}
