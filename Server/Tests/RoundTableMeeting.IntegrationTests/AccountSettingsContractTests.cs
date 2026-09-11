using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AccountSettingsContractTests
{
    internal static HttpClient Client(TestApplicationFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    internal static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Origin", "http://localhost:5173");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
    internal static async Task<JsonElement> Login(HttpClient client, string name)
    {
        var response = await Send(client, HttpMethod.Post, "/api/auth/login", new { username = name, password = "test-password-" + name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task AdministratorCannotReadSendOrDeleteAnotherUsersMeeting()
    {
        using var factory = new TestApplicationFactory(); using var admin = Client(factory); using var member = Client(factory);
        await Login(admin, "alice"); await Login(member, "bob");
        var created = await Send(member, HttpMethod.Post, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Private", description = "Only member" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("meeting").GetProperty("meetingId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/meetings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(admin, HttpMethod.Post, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "denied" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(admin, HttpMethod.Post, $"/api/meetings/{id}/deletion", new { confirmationName = "Private" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/meetings/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PersonalPasswordRejectsWhitespaceWithoutChangingThePassword(string password)
    {
        using var factory = new TestApplicationFactory(); using var client = Client(factory);
        await Login(client, "bob");
        var result = await Send(client, HttpMethod.Post, "/api/account/change-password", new { currentPassword = "test-password-bob", newPassword = password });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        using var fresh = Client(factory); await Login(fresh, "bob");
    }

    [Fact]
    public async Task OrdinaryUserCannotUseAnyManagementEndpointEvenForMissingTarget()
    {
        using var factory = new TestApplicationFactory(); using var client = Client(factory);
        var bob = await Login(client, "bob"); Assert.False(bob.GetProperty("isAdmin").GetBoolean());
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[] {
            (HttpMethod.Get, "/api/users", null),
            (HttpMethod.Post, "/api/users", new { username = "new", nickname = "New", initialPassword = "secret" }),
            (HttpMethod.Patch, $"/api/users/{Guid.NewGuid()}/nickname", new { nickname = "New" }),
            (HttpMethod.Patch, $"/api/users/{Guid.NewGuid()}/admin", new { isAdmin = true }),
            (HttpMethod.Post, $"/api/users/{Guid.NewGuid()}/reset-password", null) })
        {
            var response = await Send(client, method, path, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("forbidden", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task AdminChangesAreValidatedAndRevocationAppliesToExistingCookie()
    {
        using var factory = new TestApplicationFactory(); using var alice = Client(factory); using var bob = Client(factory);
        var a = await Login(alice, "alice"); var b = await Login(bob, "bob");
        var aid = a.GetProperty("userId").GetString(); var bid = b.GetProperty("userId").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(alice, HttpMethod.Patch, $"/api/users/{aid}/admin", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(alice, HttpMethod.Patch, $"/api/users/{aid}/admin", new { isAdmin = "false" })).StatusCode);
        var last = await Send(alice, HttpMethod.Patch, $"/api/users/{aid}/admin", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("last_admin_required", (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(alice, HttpMethod.Patch, $"/api/users/{bid}/admin", new { isAdmin = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(bob, HttpMethod.Patch, $"/api/users/{aid}/admin", new { isAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/auth/me")).StatusCode);
        var created = await Send(bob, HttpMethod.Post, "/api/users", new { username = "new", nickname = "New", initialPassword = "secret", isAdmin = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task PersonalSettingsIgnoreForgedIdentityAndPasswordChangeKeepsAllSessions()
    {
        using var factory = new TestApplicationFactory(); using var alice = Client(factory); using var bob = Client(factory); using var otherBob = Client(factory);
        var a = await Login(alice, "alice"); var b = await Login(bob, "bob"); await Login(otherBob, "bob");
        var changed = await Send(bob, HttpMethod.Patch, "/api/account/nickname", new { nickname = " Bob new ", userId = a.GetProperty("userId").GetString(), isAdmin = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var user = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(b.GetProperty("userId").GetString(), user.GetProperty("userId").GetString());
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("Bob new", user.GetProperty("nickname").GetString());
        var wrong = await Send(bob, HttpMethod.Post, "/api/account/change-password", new { currentPassword = "wrong", newPassword = "new" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True((await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("currentPassword", out _));
        var success = await Send(bob, HttpMethod.Post, "/api/account/change-password", new { currentPassword = "test-password-bob", newPassword = " 新密码 " });
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        Assert.False(success.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherBob.GetAsync("/api/auth/me")).StatusCode);
        using var fresh = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(fresh, HttpMethod.Post, "/api/auth/login", new { username = "bob", password = "test-password-bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(fresh, HttpMethod.Post, "/api/auth/login", new { username = "bob", password = " 新密码 " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(alice, HttpMethod.Post, $"/api/users/{b.GetProperty("userId").GetString()}/reset-password")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Post, "/api/account/change-password", new { currentPassword = "123456", newPassword = "personal" })).StatusCode);
    }

    [Theory]
    [InlineData("/api/account/nickname", "PATCH")]
    [InlineData("/api/account/change-password", "POST")]
    public async Task PersonalWritesEnforceAuthenticationOriginMediaAndCache(string path, string method)
    {
        using var factory = new TestApplicationFactory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, new HttpMethod(method), path, new { nickname = "N", currentPassword = "x", newPassword = "y" })).StatusCode);
        await Login(client, "bob");
        using var missingOrigin = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        var denied = await client.SendAsync(missingOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.True(denied.Headers.CacheControl?.NoStore);
        using var media = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("bad") };
        media.Headers.Add("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(media)).StatusCode);
    }

    [Fact]
    public async Task OpenApiIncludesRequiredBooleanAndAllNewOperations()
    {
        using var factory = new TestApplicationFactory(); using var client = Client(factory);
        var doc = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = doc.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/users").GetProperty("get").GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(paths.GetProperty("/api/users/{userId}/admin").GetProperty("patch").GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.TryGetProperty("/api/account/change-password", out _));
        Assert.Contains("isAdmin", doc.GetProperty("components").GetProperty("schemas").GetProperty("UpdateAdminRequest").GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }
}
