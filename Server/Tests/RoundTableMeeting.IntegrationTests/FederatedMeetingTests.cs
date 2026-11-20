using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class FederatedMeetingTests
{
    [Fact]
    public async Task EntryAcceptsInvitationFromAnotherDeploymentAndReusesRoomAfterReconnect()
    {
        using var peer = new TestApplicationFactory();
        peer.UseKestrel(0);
        using var human = peer.CreateClient();
        var site = human.BaseAddress!.AbsoluteUri;
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        using var create = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Peer" });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var invitation = created.GetProperty("invitation");
        var meetingId = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();

        using var original = new TestApplicationFactory();
        using var entry = original.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:TrustedSites:0"] = site })));
        Task<McpClient> Connect() => McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, entry.CreateClient()));
        await using var first = await Connect();
        var args = new Dictionary<string, object?>
        {
            ["meetingCode"] = invitation.GetProperty("meetingCode").GetString(), ["displayName"] = "跨站助手",
            ["invitationKey"] = Guid.NewGuid().ToString(), ["siteUrl"] = site
        };
        var joined = await first.CallToolAsync("attend", args);
        var payload = Assert.Single(joined.Content.OfType<TextContentBlock>()).Text;
        Assert.False(joined.IsError == true, payload);
        var room = JsonDocument.Parse(payload).RootElement.GetProperty("room").GetString();
        await using var resumed = await Connect();
        args["displayName"] = "收信助手";
        args["invitationKey"] = Guid.NewGuid().ToString();
        var recipientJoin = await resumed.CallToolAsync("attend", args);
        var recipientRoom = JsonDocument.Parse(Assert.Single(recipientJoin.Content.OfType<TextContentBlock>()).Text)
            .RootElement.GetProperty("room").GetString();
        var send = new Dictionary<string, object?> { ["room"] = room, ["clientSubmissionId"] = Guid.NewGuid(),
            ["recipientDisplayName"] = "收信助手", ["content"] = "仅收件人可见" };
        var sent = await resumed.CallToolAsync("send_private_message", send);
        Assert.False(sent.IsError == true, Assert.Single(sent.Content.OfType<TextContentBlock>()).Text);
        var replay = await resumed.CallToolAsync("send_private_message", send);
        Assert.Equal(Assert.Single(sent.Content.OfType<TextContentBlock>()).Text, Assert.Single(replay.Content.OfType<TextContentBlock>()).Text);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var inbox = await resumed.CallToolAsync("next", new Dictionary<string, object?> { ["rooms"] = new[] { recipientRoom } }, cancellationToken: timeout.Token);
        var pending = JsonDocument.Parse(Assert.Single(inbox.Content.OfType<TextContentBlock>()).Text).RootElement.GetProperty("message");
        Assert.Equal("private", pending.GetProperty("visibility").GetString());
        Assert.Equal("仅收件人可见", pending.GetProperty("content").GetString());
        var answer = new Dictionary<string, object?> { ["room"] = recipientRoom, ["clientSubmissionId"] = Guid.NewGuid(),
            ["recipientParticipantId"] = pending.GetProperty("author").GetProperty("participantId").GetGuid(), ["content"] = "另一条私信" };
        Assert.False((await resumed.CallToolAsync("send_private_message", answer)).IsError == true);
        var skip = await resumed.CallToolAsync("skip", new Dictionary<string, object?> { ["room"] = recipientRoom, ["messageId"] = pending.GetProperty("messageId").GetGuid() });
        Assert.Equal("handled", Assert.Single(skip.Content.OfType<TextContentBlock>()).Text);
        var visible = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}/messages");
        Assert.DoesNotContain(visible.GetProperty("items").EnumerateArray(), m => m.GetProperty("visibility").GetString() == "private");
        send["recipientDisplayName"] = "不存在的参会者";
        Assert.True((await resumed.CallToolAsync("send_private_message", send)).IsError);

        var leave = await resumed.CallToolAsync("leave", new Dictionary<string, object?> { ["room"] = room });
        Assert.Equal("left", Assert.Single(leave.Content.OfType<TextContentBlock>()).Text);
        args["siteUrl"] = "http://localhost:1/";
        Assert.True((await resumed.CallToolAsync("attend", args)).IsError);
    }
}
