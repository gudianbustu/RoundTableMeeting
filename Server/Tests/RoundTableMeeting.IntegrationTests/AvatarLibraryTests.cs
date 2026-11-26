using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Repository.Sqlite.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AvatarLibraryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshRoomsTransportRetainsParticipantAndAvatarAfterLeaseExpiry(bool useAvatarInvitation, bool simulateMissingBinding)
    {
        using var original = new TestApplicationFactory();
        var clock = new ManualTimeProvider();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        (await owner.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" })).EnsureSuccessStatusCode();
        var created = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "头像重入" });
        created.EnsureSuccessStatusCode();
        var creation = await created.Content.ReadFromJsonAsync<JsonElement>();
        var meetingPath = "/api/meetings/" + creation.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var key = Guid.NewGuid().ToString("N");
        if (useAvatarInvitation)
        {
            var invitation = await owner.PostAsJsonAsync(meetingPath + "/agent-invitations", new { displayName = "Returning", avatarId = "/Avatars/%E6%A8%A1%E5%9E%8B/Claude.png" });
            invitation.EnsureSuccessStatusCode();
            key = (await invitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitationKey").GetString()!;
        }
        var arguments = new Dictionary<string, object?> {
            ["meetingCode"] = creation.GetProperty("invitation").GetProperty("meetingCode").GetString(),
            ["displayName"] = "Returning", ["invitationKey"] = key
        };
        async Task<JsonElement> AttendOnNewTransport()
        {
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(owner.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
            }, factory.CreateClient()));
            var result = await client.CallToolAsync("attend", arguments);
            Assert.NotEqual(true, result.IsError);
            var room = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement.GetProperty("room");
            Assert.False(string.IsNullOrEmpty(room.GetString()));
            var roster = await owner.GetFromJsonAsync<JsonElement>(meetingPath);
            return Assert.Single(roster.GetProperty("participants").EnumerateArray(), x => x.GetProperty("displayName").GetString() == "Returning");
        }
        var first = await AttendOnNewTransport();
        if (simulateMissingBinding)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("UPDATE AvatarInvitations SET BoundParticipantId = NULL"));
        }
        clock.Advance(TimeSpan.FromMinutes(6));
        var recovered = await AttendOnNewTransport();
        Assert.Equal(first.GetProperty("participantId").GetGuid(), recovered.GetProperty("participantId").GetGuid());
        foreach (var seat in new[] { first, recovered })
        {
            Assert.Equal(useAvatarInvitation ? "/Avatars/%E6%A8%A1%E5%9E%8B/Claude.png" : null,
                seat.TryGetProperty("avatarUrl", out var url) ? url.GetString() : null);
            Assert.Equal(useAvatarInvitation ? 0 : (int?)null,
                seat.TryGetProperty("avatarColorSlot", out var color) && color.ValueKind == JsonValueKind.Number ? color.GetInt32() : null);
        }
    }

    [Fact]
    public async Task CatalogPersonalImageAndAgentInvitationKeepDistinctOwnership()
    {
        using var factory = new TestApplicationFactory();
        using var owner = factory.CreateClient();
        using var member = factory.CreateClient();
        using var outsider = factory.CreateClient();
        const string library = "/api/avatars";
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync(library)).StatusCode);
        foreach (var (client, name) in new[] { (owner, "alice"), (member, "bob") })
        {
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
            (await client.PostAsJsonAsync("/api/auth/login", new { username = name, password = "test-password-" + name })).EnsureSuccessStatusCode();
        }
        var initial = await member.GetFromJsonAsync<JsonElement>(library);
        var seedRoot = Path.Combine(AppContext.BaseDirectory, "AvatarSeed");
        var expectedCount = Directory.EnumerateFiles(seedRoot, "*.png", SearchOption.AllDirectories).Count();
        Assert.True(expectedCount > 0);
        Assert.Equal(expectedCount, initial.GetArrayLength());
        Assert.Equal(expectedCount, initial.EnumerateArray().Select(a => a.GetProperty("id").GetString()).Distinct().Count());

        using var bitmap = new SKBitmap(3, 2);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        MultipartFormDataContent Form(bool valid = true)
        {
            var body = new MultipartFormDataContent();
            body.Add(new ByteArrayContent(valid ? png.ToArray() : [0, 1, 2]), "file", "sample.png");
            body.Add(new StringContent("测试头像"), "name");
            return body;
        }
        using var deniedUpload = Form();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(library, deniedUpload)).StatusCode);
        using var badUpload = Form(false);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(library, badUpload)).StatusCode);
        using var catalogUpload = Form();
        using var uploaded = await owner.PostAsync(library, catalogUpload);
        uploaded.EnsureSuccessStatusCode();
        var entry = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var photo = entry.GetProperty("imageUrl").GetString()!;
        Assert.Equal("image/png", (await member.GetAsync(photo)).Content.Headers.ContentType?.MediaType);
        using var personal = Form();
        using var mine = await member.PostAsync("/api/account/avatar", personal);
        mine.EnsureSuccessStatusCode();
        var ownPhoto = (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarUrl").GetString()!;
        Assert.NotEqual(photo, ownPhoto);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(ownPhoto)).StatusCode);
        Assert.Equal(expectedCount + 1, (await member.GetFromJsonAsync<JsonElement>(library)).GetArrayLength());
        var me = await member.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(ownPhoto, me.GetProperty("avatarUrl").GetString());

        using var created = await owner.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "头像验收" });
        created.EnsureSuccessStatusCode();
        var creation = await created.Content.ReadFromJsonAsync<JsonElement>();
        var meeting = creation.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = creation.GetProperty("invitation").GetProperty("meetingCode").GetString()!;
        var path = $"/api/meetings/{meeting}";
        (await member.PostAsJsonAsync("/api/meetings/join", new { meetingCode = code, displayName = "Member" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(ownPhoto)).StatusCode);
        var roster = await owner.GetFromJsonAsync<JsonElement>(path);
        Assert.Contains(roster.GetProperty("participants").EnumerateArray(), p =>
            p.GetProperty("displayName").GetString() == "Member" && p.GetProperty("avatarUrl").GetString() == ownPhoto);

        var id = entry.GetProperty("id").GetString();
        using var invitation = await owner.PostAsJsonAsync(path + "/agent-invitations", new { displayName = "AgentA", avatarId = id });
        invitation.EnsureSuccessStatusCode();
        var key = (await invitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitationKey").GetString();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(owner.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, factory.CreateClient()));
        var attended = await mcp.CallToolAsync("attend", new Dictionary<string, object?> {
            ["meetingCode"] = code, ["displayName"] = "AgentA", ["invitationKey"] = key
        });
        Assert.NotEqual(true, attended.IsError);
        var room = JsonDocument.Parse(Assert.Single(attended.Content.OfType<TextContentBlock>()).Text).RootElement.GetProperty("room").GetString();
        var sent = await mcp.CallToolAsync("send_public_message", new Dictionary<string, object?> {
            ["room"] = room, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = "avatar history sample"
        });
        Assert.NotEqual(true, sent.IsError);
        var history = await member.GetFromJsonAsync<JsonElement>(path + "/messages");
        var message = Assert.Single(history.GetProperty("items").EnumerateArray(), x => x.GetProperty("content").GetString() == "avatar history sample");
        Assert.Equal(photo, message.GetProperty("author").GetProperty("avatarUrl").GetString());
        Assert.Equal(0, message.GetProperty("author").GetProperty("avatarColorSlot").GetInt32());
        (await member.PutAsJsonAsync("/api/account/avatar", new { avatarId = id })).EnsureSuccessStatusCode();
        async Task<int> ReadMemberColor() =>
            (await member.GetFromJsonAsync<JsonElement>(path)).GetProperty("participants").EnumerateArray()
                .Single(p => p.GetProperty("displayName").GetString() == "Member").GetProperty("avatarColorSlot").GetInt32();
        Assert.Equal(1, await ReadMemberColor());
        (await member.PutAsJsonAsync("/api/account/avatar", new { avatarId = "/Avatars/%E6%A8%A1%E5%9E%8B/GPT.png" })).EnsureSuccessStatusCode();
        Assert.Equal(0, await ReadMemberColor());
        (await member.PutAsJsonAsync("/api/account/avatar", new { avatarId = id })).EnsureSuccessStatusCode();
        Assert.Equal(1, await ReadMemberColor());
        (await member.PutAsJsonAsync("/api/account/avatar", new { avatarId = "/Avatars/%E6%A8%A1%E5%9E%8B/GPT.png" })).EnsureSuccessStatusCode();
        Assert.Equal("/Avatars/%E6%A8%A1%E5%9E%8B/GPT.png", (await member.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("avatarUrl").GetString());
        // Both participants use the migrated public upload before its group is renamed.
        (await member.PutAsJsonAsync("/api/account/avatar", new { avatarId = id })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(library + "/groups", new { action = "renameGroup", group = "自定义", name = "公共收藏" })).EnsureSuccessStatusCode();
        Assert.Equal(1, await ReadMemberColor());
        var renamed = await member.GetFromJsonAsync<JsonElement>(path + "/messages");
        var author = renamed.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("content").GetString() == "avatar history sample").GetProperty("author");
        var renamedPhoto = author.GetProperty("avatarUrl").GetString();
        Assert.Contains(Uri.EscapeDataString("公共收藏"), renamedPhoto);
        Assert.Equal(0, author.GetProperty("avatarColorSlot").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(renamedPhoto)).StatusCode);
    }
}
