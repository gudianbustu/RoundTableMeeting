using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIC.ResidentLab.Transport.Tests;

/// <summary>
/// 使用真实 HTTP 请求执行最小 MCP Streamable HTTP 客户端流程。
/// </summary>
public sealed class McpHttpSession : IAsyncDisposable
{
    private readonly HttpClient client;
    private int nextRequestId;

    private McpHttpSession(HttpClient client, string sessionId)
    {
        this.client = client;
        SessionId = sessionId;
        SessionIdHash = McpProbeState.HashSessionId(sessionId);
    }

    public string SessionId { get; }
    public string SessionIdHash { get; }

    public static async Task<McpHttpSession> ConnectAsync(TransportProbeHost host, bool sendInitializedNotification = true)
    {
        var client = new HttpClient { BaseAddress = host.Endpoint };
        var initialize = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "transport-spike-tests", version = "1.0" }
            }
        };

        using var response = await SendAsync(client, initialize, sessionId: null);
        var sessionId = response.Headers.GetValues("Mcp-Session-Id").SingleOrDefault();
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new Xunit.Sdk.XunitException("stateful_session_continuity: initialize did not return Mcp-Session-Id.");

        await response.Content.ReadAsStringAsync();
        var session = new McpHttpSession(client, sessionId);
        if (sendInitializedNotification)
            await session.SendNotificationAsync("notifications/initialized", new { });
        return session;
    }

    public async Task<IReadOnlySet<string>> ListToolNamesAsync()
    {
        using var document = await SendRpcAsync("tools/list", new { });
        return document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<string> CallToolTextAsync(string name, int? requestId = null, CancellationToken cancellationToken = default)
    {
        var id = requestId ?? Interlocked.Increment(ref nextRequestId) + 10;
        using var document = await SendRpcAsync("tools/call", new { name, arguments = new { } }, id, cancellationToken);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            throw new Xunit.Sdk.XunitException($"cancellation: tools/call returned an MCP error for {name}.");
        return result.GetProperty("content").EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString()
            ?? throw new Xunit.Sdk.XunitException($"stateful_session_continuity: tools/call returned no text for {name}.");
    }

    public async Task<HttpStatusCode> SendCancelledAsync(int requestId)
    {
        using var response = await SendAsync(client, new
        {
            jsonrpc = "2.0",
            method = "notifications/cancelled",
            @params = new { requestId }
        }, SessionId);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    public async Task<HttpStatusCode> DeleteAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, string.Empty);
        request.Headers.Add("Mcp-Session-Id", SessionId);
        using var response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    public async Task<HttpStatusCode> SendRawRpcStatusAsync(string method, object parameters, int requestId)
    {
        using var response = await SendAsync(client, new { jsonrpc = "2.0", id = requestId, method, @params = parameters }, SessionId);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync() => client.Dispose();

    public async Task<HttpStatusCode> AbortRequestAsync(int requestId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "tools/call",
            @params = new { name = "wait_for_cancellation", arguments = new { } }
        }, SessionId, cancellationToken);
        await response.Content.ReadAsStringAsync(cancellationToken);
        return response.StatusCode;
    }

    private async Task SendNotificationAsync(string method, object parameters)
    {
        using var response = await SendAsync(client, new { jsonrpc = "2.0", method, @params = parameters }, SessionId);
        await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new Xunit.Sdk.XunitException($"stateful_session_continuity: notification {method} returned HTTP {(int)response.StatusCode}.");
    }

    private async Task<JsonDocument> SendRpcAsync(string method, object parameters, int? requestId = null, CancellationToken cancellationToken = default)
    {
        var id = requestId ?? Interlocked.Increment(ref nextRequestId) + 10;
        using var response = await SendAsync(client, new { jsonrpc = "2.0", id, method, @params = parameters }, SessionId, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new Xunit.Sdk.XunitException($"stateful_session_continuity: MCP method {method} returned HTTP {(int)response.StatusCode}.");
        return ParseResponse(body, method);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, object payload, string? sessionId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.Add("Mcp-Session-Id", sessionId);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static JsonDocument ParseResponse(string body, string method)
    {
        var dataLine = body.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        var json = dataLine is null ? body.Trim() : dataLine[6..].Trim();
        if (string.IsNullOrWhiteSpace(json))
            throw new Xunit.Sdk.XunitException($"stateful_session_continuity: MCP method {method} returned an empty response.");
        return JsonDocument.Parse(json);
    }
}
