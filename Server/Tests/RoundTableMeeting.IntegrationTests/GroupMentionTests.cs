using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Repository.Sqlite.Database;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class GroupMentionTests
{
    [Fact]
    public async Task PublicGroupMentionsRespectAudienceModeAndMembership()
    {
        using var factory = new TestApplicationFactory();
        using var human = factory.CreateClient();
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        using var login = await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" });
        login.EnsureSuccessStatusCode();
        using var create = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "群体提及" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        await using var host = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(human.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, factory.CreateClient()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task<JsonElement> Call(string name, Dictionary<string, object?> args)
        {
            var result = await host.CallToolAsync(name, args, cancellationToken: timeout.Token);
            var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            Assert.False(result.IsError == true, text);
            return JsonDocument.Parse(text.StartsWith('{') ? text : JsonSerializer.Serialize(text)).RootElement;
        }
        async Task<string> Join(string name) => (await Call("attend", new() { ["meetingCode"] = code,
            ["displayName"] = name, ["invitationKey"] = Guid.NewGuid().ToString("N") })).GetProperty("room").GetString()!;
        async Task<JsonElement> Next(string room) =>
            (await Call("next", new() { ["rooms"] = new[] { room } })).GetProperty("message");
        async Task Post(string content)
        {
            using var response = await human.PostAsJsonAsync($"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content });
            response.EnsureSuccessStatusCode();
        }
        var first = await Join("甲");
        var second = await Join("乙");
        var left = await Join("已离开");
        await Call("leave", new() { ["room"] = left });
        await Post("🙂 @所有人类");
        await Post("🙂 @所有Agent，准备");
        foreach (var room in new[] { first, second })
        {
            var directed = await Next(room);
            Assert.Equal("🙂 @所有Agent，准备", directed.GetProperty("content").GetString());
            Assert.False(directed.GetProperty("optionalAgentResponse").GetBoolean());
            var mentions = directed.GetProperty("mentions").EnumerateArray().ToArray();
            Assert.Equal(new[] { "乙", "甲" }.Order(), mentions.Select(x => x.GetProperty("displayName").GetString()).Order());
            Assert.All(mentions, m => Assert.Equal(3, m.GetProperty("startOffset").GetInt32()));
            var skip = await host.CallToolAsync("skip", new Dictionary<string, object?> { ["room"] = room,
                ["messageId"] = directed.GetProperty("messageId").GetGuid() }, cancellationToken: timeout.Token);
            Assert.True(skip.IsError);
            Assert.Contains("group_reply_required", Assert.Single(skip.Content.OfType<TextContentBlock>()).Text);
            Assert.Equal(directed.GetProperty("messageId").GetGuid(), (await Next(room)).GetProperty("messageId").GetGuid());
            await Call("reply", new() { ["room"] = room, ["messageId"] = directed.GetProperty("messageId").GetGuid(), ["content"] = "到" });
        }
        using var mode = await human.PutAsJsonAsync($"/api/meetings/{id}/discussion-mode", new { mode = "free" });
        mode.EnsureSuccessStatusCode();
        await Call("send_public_message", new() { ["room"] = first, ["content"] = "@所有人类", ["clientSubmissionId"] = Guid.NewGuid() });
        await Call("send_public_message", new() { ["room"] = first, ["content"] = "@所有人", ["clientSubmissionId"] = Guid.NewGuid() });
        var voluntary = await Next(second);
        Assert.Equal("@所有人", voluntary.GetProperty("content").GetString());
        Assert.True(voluntary.GetProperty("optionalAgentResponse").GetBoolean());
        Assert.Equal(3, voluntary.GetProperty("mentions").GetArrayLength());
        await Call("skip", new() { ["room"] = second, ["messageId"] = voluntary.GetProperty("messageId").GetGuid() });
        await Call("send_private_message", new() { ["room"] = first, ["recipientDisplayName"] = "乙",
            ["content"] = "@所有人", ["clientSubmissionId"] = Guid.NewGuid() });
        var personal = await Next(second);
        Assert.Equal("private", personal.GetProperty("visibility").GetString());
        Assert.Empty(personal.GetProperty("mentions").EnumerateArray());
        await Call("send_public_message", new() { ["room"] = first, ["content"] = "@乙", ["clientSubmissionId"] = Guid.NewGuid() });
        await VerifyDowngradeAsync(factory.DatabasePath);
    }

    private static async Task VerifyDowngradeAsync(string sourcePath)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await using var copy = new SqliteConnection("Data Source=:memory:");
        await source.OpenAsync();
        await copy.OpenAsync();
        source.BackupDatabase(copy);
        await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>().UseSqlite(copy).Options);
        async Task<long> Count(string query)
        {
            await using var command = copy.CreateCommand();
            command.CommandText = query;
            return (long)(await command.ExecuteScalarAsync())!;
        }
        var individual = await Count("SELECT COUNT(*) FROM MessageMentions WHERE IsGroup = 0");
        Assert.True(individual > 0);
        Assert.True(await Count("SELECT COUNT(*) FROM MessageMentions WHERE IsGroup = 1") > 1);
        await db.Database.MigrateAsync("20261125024318_RecordParticipantRemovalEpoch");
        Assert.Equal(individual, await Count("SELECT COUNT(*) FROM MessageMentions"));
        await db.Database.MigrateAsync("20261125040755_ExpandGroupMentionTargets");
        Assert.Equal(individual, await Count("SELECT COUNT(*) FROM MessageMentions WHERE IsGroup = 0"));
    }
}
