using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class ParticipantRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatorCanRemoveAndReadmitHumanAndAgentWithoutRevivingOldRoom(bool offline)
    {
        using var original = new TestApplicationFactory();
        var clock = new ManualTimeProvider();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        factory.UseKestrel(0);
        using var owner = factory.CreateClient();
        using var member = factory.CreateClient();
        foreach (var (client, username) in new[] { (owner, "alice"), (member, "bob") })
        {
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
            using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "test-password-" + username }, cancellationToken: TestContext.Current.CancellationToken);
            login.EnsureSuccessStatusCode();
        }
        using var created = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Removal" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var data = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var id = data.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = data.GetProperty("invitation").GetProperty("meetingCode").GetString()!;
        var ownerId = data.GetProperty("meeting").GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        async Task<Guid> Join()
        {
            using var joined = await member.PostAsJsonAsync("/api/meetings/join", new { meetingCode = code, displayName = "Bob" });
            joined.EnsureSuccessStatusCode();
            return (await joined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        }
        var memberId = await Join();
        async Task<HttpStatusCode> Remove(HttpClient client, Guid participant, Guid operationId)
        {
            using var result = await client.PostAsJsonAsync($"/api/meetings/{id}/participants/{participant}/remove", new { operationId });
            return result.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Remove(member, ownerId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, await Remove(owner, ownerId, Guid.NewGuid()));
        var operation = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, await Remove(owner, memberId, operation));
        Assert.Equal("left", (await member.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}", cancellationToken: TestContext.Current.CancellationToken)).GetProperty("membershipState").GetString());
        Assert.Equal(memberId, await Join());
        Assert.Equal(HttpStatusCode.NoContent, await Remove(owner, memberId, operation));
        Assert.Equal("joined", (await member.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}", cancellationToken: TestContext.Current.CancellationToken)).GetProperty("membershipState").GetString());

        using var http = factory.CreateClient();
        await using var agent = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, http), cancellationToken: TestContext.Current.CancellationToken);
        var invite = new Dictionary<string, object?> { ["meetingCode"] = code, ["displayName"] = "Test agent", ["invitationKey"] = Guid.NewGuid() };
        async Task<string> Attend()
        {
            var result = await agent.CallToolAsync("attend", invite);
            Assert.False(result.IsError == true);
            return JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement.GetProperty("room").GetString()!;
        }
        var oldRoom = await Attend();
        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}", cancellationToken: TestContext.Current.CancellationToken);
        var agentId = before.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("displayName").GetString() == "Test agent").GetProperty("participantId").GetGuid();
        if (offline) clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.NoContent, await Remove(owner, agentId, Guid.NewGuid()));
        async Task<bool> HasRoom(string room) => (await agent.CallToolAsync("list_participants", new Dictionary<string, object?> { ["room"] = room })).IsError != true;
        Assert.False(await HasRoom(oldRoom));
        var newRoom = await Attend();
        Assert.True(await HasRoom(newRoom));
        Assert.False(await HasRoom(oldRoom));
    }
}
