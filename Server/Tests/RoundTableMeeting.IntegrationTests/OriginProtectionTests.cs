using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Authentication;
using Serilog.Events;

namespace RoundTableMeeting.IntegrationTests;

public sealed class OriginProtectionTests
{
    private const string AllowedOrigin = "http://localhost:5173";

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    public async Task JsonEndpointsSupportAllDeclaredMethods(string method)
    {
        using var factory = new TestApplicationFactory();
        using var request = CreateRequest(new HttpMethod(method), "/__test/origin/json", AllowedOrigin, new StringContent("{\"name\":\"accepted\"}", Encoding.UTF8, "application/json"));
        using var response = await factory.CreateClient().SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(method, body.GetProperty("method").GetString());
    }

    [Fact]
    public async Task DeleteWithoutBodyOrContentTypeIsAccepted()
    {
        using var factory = new TestApplicationFactory();
        using var request = CreateRequest(HttpMethod.Delete, "/__test/origin/no-json", AllowedOrigin);
        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("https://evil.example")]
    [InlineData("not an origin")]
    public async Task InvalidOriginFormsReturnUnified403(string? origin)
    {
        using var factory = new TestApplicationFactory();
        using var request = CreateRequest(HttpMethod.Post, "/__test/origin/json", origin, new StringContent("{\"name\":\"blocked\"}", Encoding.UTF8, "application/json"));
        using var response = await factory.CreateClient().SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "origin_not_allowed", "The browser origin is not allowed.");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://evil.example", null)]
    [InlineData(null, "text/plain")]
    [InlineData("https://evil.example", "text/plain")]
    public async Task OriginValidationRunsBeforeJsonMediaTypeValidation(string? origin, string? contentType)
    {
        using var factory = new TestApplicationFactory();
        HttpContent? content = contentType is null ? null : new StringContent("ignored", Encoding.UTF8, contentType);
        using var request = CreateRequest(HttpMethod.Post, "/__test/origin/json", origin, content);
        using var response = await factory.CreateClient().SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "origin_not_allowed", "The browser origin is not allowed.");
    }

    [Fact]
    public async Task MultipleOriginValuesReturnUnified403()
    {
        using var factory = new TestApplicationFactory();
        using var request = CreateRequest(HttpMethod.Post, "/__test/origin/json", null, new StringContent("{\"name\":\"blocked\"}", Encoding.UTF8, "application/json"));
        request.Headers.TryAddWithoutValidation("Origin", [AllowedOrigin, "https://evil.example"]);
        using var response = await factory.CreateClient().SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "origin_not_allowed", "The browser origin is not allowed.");
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.OK)]
    [InlineData("application/json; charset=utf-8", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/problem+json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("invalid content type", HttpStatusCode.UnsupportedMediaType)]
    public async Task JsonRequirementUsesExactParsedMediaType(string? contentType, HttpStatusCode expected)
    {
        using var factory = new TestApplicationFactory();
        var content = new StringContent("{\"name\":\"accepted\"}", Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        if (contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var request = CreateRequest(HttpMethod.Post, "/__test/origin/json", AllowedOrigin, content);
        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.UnsupportedMediaType)
        {
            await AssertProblemAsync(response, expected, "unsupported_media_type", "The request media type is not supported.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    public async Task ValidMediaTypeLeavesBodyValidationToMvc(string body)
    {
        using var factory = new TestApplicationFactory();
        using var request = CreateRequest(HttpMethod.Post, "/__test/origin/json", AllowedOrigin, new StringContent(body, Encoding.UTF8, "application/json"));
        using var response = await factory.CreateClient().SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "One or more validation errors occurred.");
    }

    [Theory]
    [InlineData("POST", null)]
    [InlineData("POST", "text/plain")]
    [InlineData("DELETE", null)]
    [InlineData("DELETE", "application/problem+json")]
    public async Task EndpointsWithoutJsonRequirementIgnoreContentType(string method, string? contentType)
    {
        using var factory = new TestApplicationFactory();
        HttpContent? content = null;
        if (contentType is not null) content = new StringContent("ignored", Encoding.UTF8, contentType);
        using var request = CreateRequest(new HttpMethod(method), "/__test/origin/no-json", AllowedOrigin, content);
        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task MethodMismatchPreservesRoutingResponse(string method)
    {
        using var factory = new TestApplicationFactory();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/__test/origin/json");
        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task MiddlewareExplicitlyBypassesMismatchedHttpMethodMetadata()
    {
        var nextCalled = false;
        var middleware = new OriginValidationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new AuthenticationOptions { AllowedOrigins = [AllowedOrigin] }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new RequireBrowserOriginAttribute(requireJsonBody: true),
                new HttpMethodMetadata([HttpMethods.Post])),
            "origin method mismatch"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExplicitTransportMetadataAllowsOnlyMissingOriginGetRequests()
    {
        var nextCalled = false;
        var middleware = new OriginValidationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new AuthenticationOptions { AllowedOrigins = [AllowedOrigin] }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequireBrowserOriginAttribute(allowMissingOriginForGet: true)),
            "origin-optional transport get"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        nextCalled = false;
        context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Origin = "https://evil.example";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequireBrowserOriginAttribute(allowMissingOriginForGet: true)),
            "origin-checked transport get"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnmarkedAndUnknownEndpointsAreNotIntercepted()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        using var unmarked = new HttpRequestMessage(HttpMethod.Post, "/__test/origin/unmarked");
        unmarked.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var unmarkedResponse = await client.SendAsync(unmarked);
        Assert.Equal(HttpStatusCode.OK, unmarkedResponse.StatusCode);

        using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/unknown-origin");
        unknown.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var unknownResponse = await client.SendAsync(unknown);
        await AssertProblemAsync(unknownResponse, HttpStatusCode.NotFound, "route_not_found", "The requested API route was not found.");
    }

    [Fact]
    public async Task OriginRunsBeforeAuthenticationAndResponsesAreRequestLogged()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var missingOrigin = new HttpRequestMessage(HttpMethod.Post, "/__test/origin/authorized");
        using var forbidden = await client.SendAsync(missingOrigin);
        await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "origin_not_allowed", "The browser origin is not allowed.");

        using var allowedOrigin = CreateRequest(HttpMethod.Post, "/__test/origin/authorized", AllowedOrigin);
        using var unauthorized = await client.SendAsync(allowedOrigin);
        await AssertProblemAsync(unauthorized, HttpStatusCode.Unauthorized, "unauthenticated", "Authentication is required.");

        Assert.Contains(factory.LogSink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Information
            && logEvent.Properties.TryGetValue("requestPath", out var path)
            && path.ToString().Trim('\"') == "/__test/origin/authorized"
            && logEvent.Properties.TryGetValue("statusCode", out var status)
            && status.ToString() == "403");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? origin, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string detail)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
