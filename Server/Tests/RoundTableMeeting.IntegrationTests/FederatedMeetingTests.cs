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
        var invitation = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation");

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
        var leave = await resumed.CallToolAsync("leave", new Dictionary<string, object?> { ["room"] = room });
        Assert.Equal("left", Assert.Single(leave.Content.OfType<TextContentBlock>()).Text);
        args["siteUrl"] = "http://localhost:1/";
        Assert.True((await resumed.CallToolAsync("attend", args)).IsError);
    }
}
