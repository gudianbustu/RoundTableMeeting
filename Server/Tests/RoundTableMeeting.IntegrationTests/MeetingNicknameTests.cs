using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingNicknameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfRenamePreservesIdentityRoomAndHistory(bool federated)
    {
        using var originalSite = new TestApplicationFactory();
        var clock = new ManualTimeProvider();
        using var site = originalSite.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        site.UseKestrel(0);
        using var human = site.CreateClient();
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        async Task<(Guid Id, string Code)> Create(string name)
        {
            using var response = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name });
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (json.GetProperty("meeting").GetProperty("meetingId").GetGuid(), json.GetProperty("invitation").GetProperty("meetingCode").GetString()!);
        }
        var first = await Create("First");
        var second = await Create("Second");
        using var host = new TestApplicationFactory();
        using var origin = host.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:TrustedSites:0"] = human.BaseAddress!.AbsoluteUri })));
        using var http = federated ? origin.CreateClient() : site.CreateClient();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, http));
        async Task<string> Call(string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args);
            var payload = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            Assert.False(result.IsError == true, payload);
            return payload;
        }
        async Task<string> Attend(string code, string name, Guid key)
        {
            var args = new Dictionary<string, object?> { ["meetingCode"] = code, ["displayName"] = name, ["invitationKey"] = key };
            if (federated) args["siteUrl"] = human.BaseAddress!.AbsoluteUri;
            return JsonDocument.Parse(await Call("attend", args)).RootElement.GetProperty("room").GetString()!;
        }
        async Task<JsonElement[]> Members(string room) => JsonDocument.Parse(await Call("list_participants", new() { ["room"] = room }))
            .RootElement.GetProperty("participants").EnumerateArray().ToArray();
        var key = Guid.NewGuid();
        var room = await Attend(first.Code, "Agent one", key);
        var other = await Attend(second.Code, "Agent one", Guid.NewGuid());
        var initial = await Members(room);
        var agentId = initial.Single(m => m.GetProperty("type").GetString() == "agent").GetProperty("participantId").GetGuid();
        var person = initial.Single(m => m.GetProperty("type").GetString() == "human");
        var humanId = person.GetProperty("participantId").GetGuid();
        await Call("send_public_message", new() { ["room"] = room, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = "Public before rename" });
        await Call("send_private_message", new() { ["room"] = room, ["clientSubmissionId"] = Guid.NewGuid(), ["recipientParticipantId"] = humanId, ["content"] = "Private before rename" });
        using var posted = await human.PostAsJsonAsync($"/api/meetings/{first.Id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "Human before rename" });
        posted.EnsureSuccessStatusCode();
        var endpoint = $"/api/meetings/{first.Id}/participants/me/display-name";
        using (var foreignRequest = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = JsonContent.Create(new { displayName = "Denied name" }) })
        {
            foreignRequest.Headers.Add("Origin", "https://other.example");
            using var foreignResponse = await human.SendAsync(foreignRequest);
            Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
        }
        using var conflict = await human.PutAsJsonAsync(endpoint, new { displayName = "Agent one" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var renamed = await human.PutAsJsonAsync(endpoint, new { displayName = "Alice here" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.True((await client.CallToolAsync("rename_self", new Dictionary<string, object?> { ["room"] = "wrong", ["displayName"] = "Nobody" })).IsError);
        Assert.True((await client.CallToolAsync("rename_self", new Dictionary<string, object?> { ["room"] = room, ["displayName"] = "Alice here" })).IsError);
        Assert.Equal("renamed", await Call("rename_self", new() { ["room"] = room, ["displayName"] = "Agent renamed" }));
        var same = await Attend(first.Code, "Agent one", key);
        var current = await Members(same);
        Assert.Equal("Agent renamed", current.Single(p => p.GetProperty("participantId").GetGuid() == agentId).GetProperty("displayName").GetString());
        Assert.Equal("Alice here", current.Single(p => p.GetProperty("participantId").GetGuid() == humanId).GetProperty("displayName").GetString());
        var elsewhere = await Members(other);
        Assert.Contains(elsewhere, p => p.GetProperty("displayName").GetString() == "Agent one");
        Assert.DoesNotContain(elsewhere, p => p.GetProperty("displayName").GetString() == "Alice here");
        var history = JsonDocument.Parse(await Call("history", new() { ["room"] = room, ["limit"] = 10 })).RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(history.Where(m => m.GetProperty("content").GetString()!.EndsWith("before rename")), m =>
            Assert.Equal(m.GetProperty("author").GetProperty("participantId").GetGuid() == agentId ? "Agent renamed" : "Alice here",
                m.GetProperty("author").GetProperty("displayName").GetString()));
        Assert.Contains(history, m => m.GetProperty("visibility").GetString() == "private" && m.GetProperty("recipient").GetProperty("participantId").GetGuid() == humanId);
        Assert.Contains(history, m => m.GetProperty("content").GetString() == "Human before rename");
        var replacement = await Attend(first.Code, "Agent one", Guid.NewGuid());
        Assert.Contains(await Members(replacement), p => p.GetProperty("displayName").GetString() == "Agent one" && p.GetProperty("participantId").GetGuid() != agentId);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains(await Members(room), person => person.GetProperty("participantId").GetGuid() == agentId);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("renamed", await Call("rename_self", new() { ["room"] = room, ["displayName"] = "Agent renamed" }));
        await Call("send_public_message", new() { ["room"] = room, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = "After reconnect" });
        var latest = JsonDocument.Parse(await Call("history", new() { ["room"] = room })).RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("content").GetString() == "After reconnect").GetProperty("author");
        Assert.Equal(agentId, latest.GetProperty("participantId").GetGuid());
        Assert.Equal("Agent renamed", latest.GetProperty("displayName").GetString());
        Assert.Equal("left", await Call("leave", new() { ["room"] = same }));
        Assert.True((await client.CallToolAsync("rename_self", new Dictionary<string, object?> { ["room"] = room, ["displayName"] = "Invalid" })).IsError);

        // An explicit new connection owns the second room. Expiry alone cannot restore the old holder.
        await using var replacementScope = site.Services.CreateAsyncScope();
        var sessions = replacementScope.ServiceProvider.GetRequiredService<CurrentMcpSessionService>();
        var replacementTransport = McpTransportSessionKey.FromDigest(Guid.NewGuid().ToByteArray());
        await sessions.JoinAsync(replacementTransport, second.Code, "Agent one", "takeover", "test", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await client.CallToolAsync("list_participants", new Dictionary<string, object?> { ["room"] = other });
        Assert.True(stale.IsError);
        Assert.Contains("connection_unavailable", Assert.Single(stale.Content.OfType<TextContentBlock>()).Text);
    }
}
