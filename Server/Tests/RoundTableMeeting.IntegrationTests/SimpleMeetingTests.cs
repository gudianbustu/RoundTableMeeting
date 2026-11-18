using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class SimpleMeetingTests
{
    [Fact]
    public async Task InvitationRecoversAcrossSessionsAndLeaseExpiryAndRepliesOnce()
    {
        using var original = new TestApplicationFactory();
        var clock = new ManualTimeProvider();
        using var factory = original.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(clock);
        }));
        using var human = factory.CreateClient();
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        using var create = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Remote MCP" });
        var meeting = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = meeting.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = meeting.GetProperty("invitation").GetProperty("meetingCode").GetString()!;
        var invitationKey = Guid.NewGuid().ToString("N");
        Task<McpClient> Host(string? key = null) => McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(human.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string,string> { ["X-RoundTable-Key"] = key ?? invitationKey, ["X-RoundTable-Meeting"] = code, ["X-RoundTable-Agent"] = Uri.EscapeDataString("访客") }
        }, factory.CreateClient()));
        static async Task<string> Call(McpClient client, string name, Dictionary<string,object?>? args = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var response = await client.CallToolAsync(name, args, cancellationToken: timeout.Token);
            var text = Assert.Single(response.Content.OfType<TextContentBlock>()).Text;
            Assert.False(response.IsError == true, text);
            return text;
        }

        await using var first = await Host();
        Assert.Equal(new[] { "attachment_transfer", "attend", "leave", "next", "reply", "skip" },
            (await first.ListToolsAsync()).Select(t => t.Name).Order().ToArray());
        Assert.Equal("joined", await Call(first, "attend"));
        await using var other = await Host(Guid.NewGuid().ToString("N"));
        Assert.True((await other.CallToolAsync("attend")).IsError);
        Assert.True((await other.CallToolAsync("next")).IsError);
        var workspace = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}");
        var agent = workspace.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("displayName").GetString() == "访客");
        var participantId = agent.GetProperty("participantId").GetGuid();
        // The SDK answers the server's first liveness ping while a next call is pending.
        var waiting = Call(first, "next");
        await Task.Delay(TimeSpan.FromSeconds(27));
        Assert.False(waiting.IsCompleted);
        using var post = await human.PostAsJsonAsync($"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(),
            content = "收到请回", visibility = "private", recipientParticipantId = participantId });
        post.EnsureSuccessStatusCode();
        var delivery = JsonDocument.Parse(await waiting).RootElement;
        Assert.True(delivery.GetProperty("fromMeetingCreator").GetBoolean());
        var message = delivery.GetProperty("message").GetProperty("messageId").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(11));
        await using var restored = await Host();
        Assert.Equal(message, JsonDocument.Parse(await Call(restored, "next")).RootElement.GetProperty("message").GetProperty("messageId").GetGuid());
        var reply = new Dictionary<string,object?> { ["messageId"] = message, ["content"] = "收到" };
        Assert.Equal("handled", await Call(restored, "reply", reply));
        Assert.Equal("handled", await Call(restored, "reply", reply));
        Assert.Equal("left", await Call(restored, "leave"));
        Assert.Equal("left", await Call(restored, "leave"));
        Assert.True((await first.CallToolAsync("next")).IsError);
        var history = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/messages");
        Assert.Single(history.GetProperty("items").EnumerateArray(), m => m.GetProperty("content").GetString() == "收到");
    }
    [Fact]
    public async Task SameHostReceivesAndRepliesAcrossIndependentRooms()
    {
        using var factory = new TestApplicationFactory();
        using var human = factory.CreateClient();
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        Task<McpClient> Host() => McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(human.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, factory.CreateClient()));
        await using var host = await Host();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<string> Call(McpClient client, string name, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(name, args, cancellationToken: timeout.Token);
            var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            Assert.False(result.IsError == true, text);
            return text;
        }
        async Task<(Guid Id, string Room, Guid Member)> Join(string name)
        {
            using var create = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name });
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
            var joined = JsonDocument.Parse(await Call(host, "attend", new()
            {
                ["meetingCode"] = created.GetProperty("invitation").GetProperty("meetingCode").GetString(),
                ["displayName"] = "同一个助手", ["invitationKey"] = Guid.NewGuid().ToString("N")
            })).RootElement;
            var details = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}");
            var member = Assert.Single(details.GetProperty("participants").EnumerateArray(), x => x.GetProperty("displayName").GetString() == "同一个助手");
            return (id, joined.GetProperty("room").GetString()!, member.GetProperty("participantId").GetGuid());
        }
        async Task Send(Guid id, Guid member, string content)
        {
            using var sent = await human.PostAsJsonAsync($"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content, visibility = "private", recipientParticipantId = member });
            sent.EnsureSuccessStatusCode();
        }
        var a = await Join("会议 A");
        var b = await Join("会议 B");
        var waiting = Call(host, "next", new() { ["rooms"] = new[] { a.Room, b.Room } });
        await Send(b.Id, b.Member, "乙");
        var received = JsonDocument.Parse(await waiting).RootElement;
        Assert.Equal(b.Room, received.GetProperty("room").GetString());
        var msg = received.GetProperty("message").GetProperty("messageId").GetGuid();
        Assert.True((await host.CallToolAsync("reply", new Dictionary<string,object?> { ["room"] = a.Room, ["messageId"] = msg, ["content"] = "串会" })).IsError);
        Assert.Equal("handled", await Call(host, "reply", new() { ["room"] = b.Room, ["messageId"] = msg, ["content"] = "乙回复" }));
        Assert.Equal("left", await Call(host, "leave", new() { ["room"] = b.Room }));
        var history = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{b.Id}/messages");
        Assert.Single(history.GetProperty("items").EnumerateArray(), m => m.GetProperty("content").GetString() == "乙回复");
        await Send(a.Id, a.Member, "甲");
        await using var resumed = await Host();
        received = JsonDocument.Parse(await Call(resumed, "next", new() { ["rooms"] = new[] { a.Room } })).RootElement;
        Assert.Equal(a.Room, received.GetProperty("room").GetString());
        Assert.Equal("甲", received.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("handled", await Call(resumed, "skip", new() { ["room"] = a.Room, ["messageId"] = received.GetProperty("message").GetProperty("messageId").GetGuid() }));
        Assert.Equal("left", await Call(resumed, "leave", new() { ["room"] = a.Room }));
    }
}
