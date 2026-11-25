using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace RoundTableMeeting.IntegrationTests;

public sealed class HumanMentionNotificationTests
{
    [Fact]
    public async Task AuthenticatedAccountReceivesOnlyItsMentionsWithoutSubscribingToMeeting()
    {
        using var original = new TestApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Authentication:SeedAccounts:2:Username"] = "carol", ["Authentication:SeedAccounts:2:Nickname"] = "Carol",
                ["Authentication:SeedAccounts:2:Password"] = "test-password-carol"
            })));
        factory.UseKestrel(0);
        using var owner = factory.CreateClient();
        using var member = factory.CreateClient();
        using var observer = factory.CreateClient();
        using var anonymous = factory.CreateClient(new() { BaseAddress = owner.BaseAddress!, AllowAutoRedirect = false });
        const string origin = "http://localhost:5173";
        const string hubPath = "/hubs/mention-notifications";
        anonymous.DefaultRequestHeaders.Add("Origin", origin);
        using var denied = await anonymous.PostAsync(hubPath + "/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<ClientWebSocket> Connect(HttpClient client, string username)
        {
            client.DefaultRequestHeaders.Add("Origin", origin);
            using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "test-password-" + username });
            login.EnsureSuccessStatusCode();
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", origin);
            ws.Options.Cookies = new CookieContainer();
            foreach (var cookie in login.Headers.GetValues("Set-Cookie"))
                ws.Options.Cookies.SetCookies(client.BaseAddress!, cookie.Split(';')[0]);
            var address = new UriBuilder(new Uri(client.BaseAddress!, hubPath)) { Scheme = "ws" };
            await ws.ConnectAsync(address.Uri, timeout.Token);
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),
                WebSocketMessageType.Text, true, timeout.Token);
            var handshake = new byte[4096];
            var frame = await ws.ReceiveAsync(handshake, timeout.Token);
            Assert.Contains("{}\u001e", Encoding.UTF8.GetString(handshake, 0, frame.Count));
            return ws;
        }
        async Task<Guid> Receive(ClientWebSocket socket, Guid meeting)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var frame = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
                foreach (var record in Encoding.UTF8.GetString(buffer, 0, frame.Count).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    var item = JsonSerializer.Deserialize<JsonElement>(record);
                    if (item.GetProperty("type").GetInt32() == 6) continue;
                    Assert.Equal("humanMentioned", item.GetProperty("target").GetString());
                    var payload = Assert.Single(item.GetProperty("arguments").EnumerateArray());
                    Assert.Equal(new[] { "meetingId", "messageId" }, payload.EnumerateObject().Select(x => x.Name).Order());
                    Assert.Equal(meeting, payload.GetProperty("meetingId").GetGuid());
                    return payload.GetProperty("messageId").GetGuid();
                }
            }
        }
        using var first = await Connect(owner, "alice");
        using var second = await Connect(member, "bob");
        using var third = await Connect(observer, "carol");
        using var created = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "通知验收" });
        var data = await created.Content.ReadFromJsonAsync<JsonElement>();
        var meeting = data.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = data.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var ownerName = data.GetProperty("meeting").GetProperty("currentParticipant").GetProperty("displayName").GetString();
        using var joined = await member.PostAsJsonAsync("/api/meetings/join", new { meetingCode = code, displayName = "通知乙" });
        joined.EnsureSuccessStatusCode();
        var memberData = await joined.Content.ReadFromJsonAsync<JsonElement>();
        var memberId = memberData.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        using var observerJoin = await observer.PostAsJsonAsync("/api/meetings/join", new { meetingCode = code, displayName = "旁观者" });
        observerJoin.EnsureSuccessStatusCode();
        async Task<Guid> Send(HttpClient client, string content)
        {
            using var response = await client.PostAsJsonAsync($"/api/meetings/{meeting}/messages", new { clientSubmissionId = Guid.NewGuid(), content });
            response.EnsureSuccessStatusCode();
            var sent = await response.Content.ReadFromJsonAsync<JsonElement>();
            return sent.GetProperty("message").GetProperty("messageId").GetGuid();
        }
        await Send(owner, "普通消息");
        await Send(owner, "@" + ownerName);
        using var privatePost = await owner.PostAsJsonAsync($"/api/meetings/{meeting}/messages", new {
            clientSubmissionId = Guid.NewGuid(), content = "@旁观者 仅私聊", visibility = "private", recipientParticipantId = memberId
        });
        privatePost.EnsureSuccessStatusCode();
        var personal = await Send(owner, "@通知乙 @通知乙");
        Assert.Equal(personal, await Receive(second, meeting));
        var group = await Send(member, "@所有人类");
        Assert.Equal(group, await Receive(first, meeting));
        Assert.Equal(group, await Receive(third, meeting));
        var last = await Send(owner, "@通知乙");
        Assert.Equal(last, await Receive(second, meeting));
    }
}
