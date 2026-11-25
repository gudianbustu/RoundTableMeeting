using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoundTableMeeting.IntegrationTests;

public sealed class McpLongTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullTextIsAtomicReplayableAndUsesMessageVisibility(bool federated)
    {
        using var source = new TestApplicationFactory();
        using var site = source.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["RealtimeChat:MaxContentLength"] = "64" })));
        site.UseKestrel(0);
        using var human = site.CreateClient();
        human.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        (await human.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "test-password-alice" })).EnsureSuccessStatusCode();
        var response = await human.PostAsJsonAsync("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Long text" });
        response.EnsureSuccessStatusCode();
        var meeting = await response.Content.ReadFromJsonAsync<JsonElement>();
        var code = meeting.GetProperty("invitation").GetProperty("meetingCode").GetString();
        using var originHost = new TestApplicationFactory();
        using var origin = originHost.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:TrustedSites:0"] = human.BaseAddress!.AbsoluteUri })));
        using var http = federated ? origin.CreateClient() : site.CreateClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-RoundTable-Mode"] = "rooms" }
        }, http));
        async Task<string> Call(string tool, Dictionary<string, object?> args)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var result = await mcp.CallToolAsync(tool, args, cancellationToken: deadline.Token);
            var value = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            Assert.False(result.IsError == true, value);
            return value;
        }
        async Task<string> Attend(string name)
        {
            var args = new Dictionary<string, object?> { ["meetingCode"] = code, ["displayName"] = name, ["invitationKey"] = Guid.NewGuid() };
            if (federated) args["siteUrl"] = human.BaseAddress!.AbsoluteUri;
            return JsonDocument.Parse(await Call("attend", args)).RootElement.GetProperty("room").GetString()!;
        }
        var a = await Attend("Writer");
        var b = await Attend("Reader");
        var outsider = await Attend("Other");
        async Task<JsonElement[]> History(string room) => JsonDocument.Parse(await Call("history", new() { ["room"] = room, ["limit"] = 10 }))
            .RootElement.GetProperty("items").EnumerateArray().ToArray();
        async Task<string> Read(string room, Guid id)
        {
            var content = new StringBuilder();
            long? offset = 0;
            while (offset.HasValue)
            {
                var page = JsonDocument.Parse(await Call("read_message_text", new() { ["room"] = room, ["messageId"] = id,
                    ["offsetBytes"] = offset, ["limitBytes"] = 17 })).RootElement;
                content.Append(page.GetProperty("text").GetString());
                offset = page.GetProperty("nextByteOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextByteOffset").GetInt64();
            }
            return content.ToString();
        }
        const string prefix = "中文😀\n```txt\n原文\n```\r\n";
        var exact = prefix + new string('x', 64 - Encoding.UTF8.GetByteCount(prefix));
        var full = exact + "🙂\n ";
        var shortId = JsonDocument.Parse(await Call("send_public_message", new() { ["room"] = a, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = exact })).RootElement.GetGuid();
        var atLimit = (await History(b)).Single(m => m.GetProperty("messageId").GetGuid() == shortId);
        Assert.Equal(exact, atLimit.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, atLimit.GetProperty("originalTextAttachmentId").ValueKind);
        var post = new Dictionary<string, object?> { ["room"] = a, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = full };
        var sent = await Call("send_public_message", post);
        Assert.Equal(sent, await Call("send_public_message", post));
        var publicId = JsonDocument.Parse(sent).RootElement.GetGuid();
        Assert.Equal(full, await Read(b, publicId));
        foreach (var (offset, limit) in new[] { (1, 4), (999, 4), (0, 3), (0, 65537) })
            Assert.True((await mcp.CallToolAsync("read_message_text", new Dictionary<string, object?> {
                ["room"] = b, ["messageId"] = publicId, ["offsetBytes"] = offset, ["limitBytes"] = limit })).IsError);
        var publicItem = Assert.Single(await History(b), m => m.GetProperty("messageId").GetGuid() == publicId);
        Assert.NotEqual(full, publicItem.GetProperty("content").GetString());
        Assert.Equal(publicItem.GetProperty("originalTextAttachmentId").GetGuid(),
            Assert.Single(publicItem.GetProperty("attachments").EnumerateArray()).GetProperty("attachmentId").GetGuid());
        post["content"] = full + "changed";
        Assert.True((await mcp.CallToolAsync("send_public_message", post)).IsError);

        var draft = Guid.NewGuid();
        var submit = new Dictionary<string, object?> { ["room"] = a, ["draftId"] = draft, ["destination"] = "private", ["recipientDisplayName"] = "Reader" };
        var part = new Dictionary<string, object?> { ["room"] = a, ["draftId"] = draft, ["partCount"] = 2, ["index"] = 1,
            ["totalBytes"] = Encoding.UTF8.GetByteCount(full), ["content"] = full[prefix.Length..] };
        await Call("stage_text_part", part);
        var differentManifest = new Dictionary<string, object?>(part) { ["partCount"] = 3 };
        Assert.True((await mcp.CallToolAsync("stage_text_part", differentManifest)).IsError);
        Assert.True((await mcp.CallToolAsync("submit_staged_text", submit)).IsError);
        Assert.DoesNotContain(await History(b), m => m.GetProperty("visibility").GetString() == "private");
        await Call("stage_text_part", part);
        part["content"] = "different part";
        Assert.True((await mcp.CallToolAsync("stage_text_part", part)).IsError);
        part["content"] = prefix;
        part["index"] = 0;
        await Call("stage_text_part", part);
        var published = Guid.Parse((await Call("submit_staged_text", submit)).Trim('"'));
        // Simulate normal temporary-part cleanup after a response was lost.
        await using (var scope = site.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            await db.Set<MeetingAttachment>().Where(f => f.State == "staged").ExecuteDeleteAsync();
        }
        Assert.Equal(published, Guid.Parse((await Call("submit_staged_text", submit)).Trim('"')));
        var wrongDestination = new Dictionary<string, object?>(submit) { ["recipientDisplayName"] = "Other" };
        Assert.True((await mcp.CallToolAsync("submit_staged_text", wrongDestination)).IsError);
        Assert.Equal(full, await Read(b, published));
        Assert.Single(await History(b), m => m.GetProperty("messageId").GetGuid() == published);
        Assert.True((await mcp.CallToolAsync("read_message_text", new Dictionary<string, object?> { ["room"] = outsider, ["messageId"] = published })).IsError);
        var inbox = JsonDocument.Parse(await Call("next", new() { ["rooms"] = new[] { b } })).RootElement.GetProperty("message");
        Assert.Equal(published, inbox.GetProperty("messageId").GetGuid());
        var answer = new Dictionary<string, object?> { ["room"] = b, ["messageId"] = published, ["content"] = full + "reply" };
        Assert.Equal("handled", await Call("reply", answer));
        Assert.Equal("handled", await Call("reply", answer));
        var reply = Assert.Single(await History(a), m => m.GetProperty("replyToMessageId").ValueKind != JsonValueKind.Null
            && m.GetProperty("replyToMessageId").GetGuid() == published);
        Assert.Equal("private", reply.GetProperty("visibility").GetString());
        Assert.Equal(full + "reply", await Read(a, reply.GetProperty("messageId").GetGuid()));
        var firstReplyId = reply.GetProperty("messageId").GetGuid();
        var pendingReply = JsonDocument.Parse(await Call("next", new() { ["rooms"] = new[] { a } })).RootElement.GetProperty("message");
        Assert.Equal(firstReplyId, pendingReply.GetProperty("messageId").GetGuid());
        var replyDraft = Guid.NewGuid();
        await Call("stage_text_part", new() { ["room"] = a, ["draftId"] = replyDraft, ["partCount"] = 1, ["index"] = 0,
            ["totalBytes"] = Encoding.UTF8.GetByteCount(full), ["content"] = full });
        var stagedReply = new Dictionary<string, object?> { ["room"] = a, ["draftId"] = replyDraft, ["destination"] = "reply", ["messageId"] = firstReplyId };
        // A draft published privately or publicly cannot also answer this pending delivery.
        var publicDraft = Guid.NewGuid();
        await Call("stage_text_part", new() { ["room"] = a, ["draftId"] = publicDraft, ["partCount"] = 1, ["index"] = 0,
            ["totalBytes"] = 2, ["content"] = "ok" });
        var stagedPublic = new Dictionary<string, object?> { ["room"] = a, ["draftId"] = publicDraft, ["destination"] = "public" };
        var publicReceipt = await Call("submit_staged_text", stagedPublic);
        Assert.Equal(publicReceipt, await Call("submit_staged_text", stagedPublic));
        foreach (var usedDraft in new[] { draft, publicDraft })
        {
            var reused = new Dictionary<string, object?>(stagedReply) { ["draftId"] = usedDraft };
            Assert.True((await mcp.CallToolAsync("submit_staged_text", reused)).IsError);
        }
        var countBeforeReply = (await History(a)).Length;
        var replyReceipt = Guid.Parse((await Call("submit_staged_text", stagedReply)).Trim('"'));
        Assert.Equal(replyReceipt, Guid.Parse((await Call("submit_staged_text", stagedReply)).Trim('"')));
        foreach (var destination in new[] { "public", "private" })
        {
            var moved = new Dictionary<string, object?> { ["room"] = a, ["draftId"] = replyDraft, ["destination"] = destination };
            if (destination == "private") moved["recipientDisplayName"] = "Reader";
            Assert.True((await mcp.CallToolAsync("submit_staged_text", moved)).IsError);
        }
        var otherDelivery = new Dictionary<string, object?>(stagedReply) { ["messageId"] = published, ["replyToMessageId"] = firstReplyId };
        Assert.True((await mcp.CallToolAsync("submit_staged_text", otherDelivery)).IsError);
        Assert.Equal(countBeforeReply + 1, (await History(a)).Length);
        var secondReply = Assert.Single(await History(b), m => m.GetProperty("replyToMessageId").ValueKind != JsonValueKind.Null
            && m.GetProperty("replyToMessageId").GetGuid() == firstReplyId);
        Assert.Equal(replyReceipt, secondReply.GetProperty("messageId").GetGuid());
        Assert.Equal(full, await Read(b, secondReply.GetProperty("messageId").GetGuid()));

        var namedSends = new[] { "short", full }.Select(content => new Dictionary<string, object?>
        {
            ["room"] = a, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = content, ["recipientDisplayName"] = "Reader"
        }).ToArray();
        var receipts = new List<string>();
        foreach (var args in namedSends) receipts.Add(await Call("send_private_message", args));
        var recipientId = secondReply.GetProperty("recipient").GetProperty("participantId").GetGuid();
        Assert.Equal("renamed", await Call("rename_self", new() { ["room"] = b, ["displayName"] = "Reader renamed" }));
        async Task CheckReceipts()
        {
            Assert.Equal(published, Guid.Parse((await Call("submit_staged_text", submit)).Trim('"')));
            for (var i = 0; i < namedSends.Length; i++)
            {
                Assert.Equal(receipts[i], await Call("send_private_message", namedSends[i]));
                var changedName = new Dictionary<string, object?>(namedSends[i]) { ["recipientDisplayName"] = "Reader renamed" };
                Assert.True((await mcp.CallToolAsync("send_private_message", changedName)).IsError);
                changedName.Remove("recipientDisplayName");
                changedName["recipientParticipantId"] = recipientId;
                Assert.True((await mcp.CallToolAsync("send_private_message", changedName)).IsError);
            }
            var changedDraft = new Dictionary<string, object?>(submit) { ["recipientDisplayName"] = "Reader renamed" };
            Assert.True((await mcp.CallToolAsync("submit_staged_text", changedDraft)).IsError);
        }
        await CheckReceipts();
        var newOwner = await Attend("Reader");
        await CheckReceipts();
        Assert.DoesNotContain(await History(newOwner), item => item.GetProperty("visibility").GetString() == "private");
        var stableAddress = new Dictionary<string, object?>
        {
            ["room"] = a, ["clientSubmissionId"] = Guid.NewGuid(), ["content"] = "identifier", ["recipientParticipantId"] = recipientId
        };
        var stableReceipt = await Call("send_private_message", stableAddress);
        Assert.Equal(stableReceipt, await Call("send_private_message", stableAddress));
        stableAddress.Remove("recipientParticipantId");
        stableAddress["recipientDisplayName"] = "Reader renamed";
        Assert.True((await mcp.CallToolAsync("send_private_message", stableAddress)).IsError);

        var recoveryRoom = await Attend("Recovery");
        var meetingId = meeting.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var modeUrl = $"/api/meetings/{meetingId}/discussion-mode";
        (await human.PutAsJsonAsync(modeUrl, new { mode = "free" })).EnsureSuccessStatusCode();
        (await human.PostAsJsonAsync($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "Recovery topic" })).EnsureSuccessStatusCode();
        var nextArgs = new Dictionary<string, object?> { ["rooms"] = new[] { recoveryRoom } };
        var turn = JsonDocument.Parse(await Call("next", nextArgs)).RootElement.GetProperty("message");
        Assert.True(turn.GetProperty("optionalAgentResponse").GetBoolean());
        var checkpointKey = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{code}/RECOVERY"));
        var checkpoint = Path.Combine(Path.GetDirectoryName(source.DatabasePath)!, "mcp-inbox", Convert.ToHexString(checkpointKey) + ".json");
        var pendingState = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(checkpoint))!;
        var recoveryDraft = Guid.NewGuid();
        await Call("stage_text_part", new() { ["room"] = recoveryRoom, ["draftId"] = recoveryDraft,
            ["partCount"] = 1, ["index"] = 0, ["totalBytes"] = 2, ["content"] = "ok" });
        var recoveryReply = new Dictionary<string, object?> { ["room"] = recoveryRoom, ["draftId"] = recoveryDraft,
            ["destination"] = "reply", ["messageId"] = turn.GetProperty("messageId").GetGuid() };
        var savedReply = await Call("submit_staged_text", recoveryReply);
        // Restore the durable state at the crash boundary: database append committed, cursor acknowledgement not yet saved.
        var handledState = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(checkpoint))!;
        pendingState["Decision"] = handledState["LastDecision"]!.GetValue<string>();
        await File.WriteAllTextAsync(checkpoint, pendingState.ToJsonString());
        (await human.PutAsJsonAsync(modeUrl, new { mode = "directed" })).EnsureSuccessStatusCode();
        var wake = await Call("send_private_message", new() { ["room"] = a, ["clientSubmissionId"] = Guid.NewGuid(),
            ["recipientDisplayName"] = "Recovery", ["content"] = "Wake after interruption" });
        var nextPending = await Call("next", nextArgs);
        Assert.Equal(JsonDocument.Parse(wake).RootElement.GetGuid(), JsonDocument.Parse(nextPending).RootElement.GetProperty("message").GetProperty("messageId").GetGuid());
        Assert.Equal(savedReply, await Call("submit_staged_text", recoveryReply));
        Assert.Equal("discussion_ended", await Call("submit_staged_text", new(recoveryReply) { ["draftId"] = Guid.NewGuid() }));
        Assert.True((await mcp.CallToolAsync("submit_staged_text", new Dictionary<string, object?>(recoveryReply) { ["attachmentIds"] = new[] { Guid.NewGuid() } })).IsError);
        Assert.Equal(nextPending, await Call("next", nextArgs));

        await using var replacementScope = site.Services.CreateAsyncScope();
        var sessions = replacementScope.ServiceProvider.GetRequiredService<CurrentMcpSessionService>();
        await sessions.JoinAsync(McpTransportSessionKey.FromDigest(Guid.NewGuid().ToByteArray()), code!, "Writer", "takeover", "test", CancellationToken.None);
        Assert.True((await mcp.CallToolAsync("stage_text_part", part)).IsError);
        Assert.True((await mcp.CallToolAsync("submit_staged_text", submit)).IsError);
        Assert.True((await mcp.CallToolAsync("read_message_text", new Dictionary<string, object?> { ["room"] = a, ["messageId"] = publicId })).IsError);
    }
}
