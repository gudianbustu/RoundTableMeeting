using RoundTableMeeting.Abstraction.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.RealtimeChat;
namespace RoundTableMeeting.IntegrationTests;

public sealed class AttachmentTests
{
    [Fact]
    public async Task AttachmentSelectionOrderSurvivesHistoryAndRetry()
    {
        using var factory = new TestApplicationFactory();
        using var client = Client(factory);
        await Login(client, "alice");
        var meeting = await Body(await Post(client, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Order" }), HttpStatusCode.Created);
        var id = meeting.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var endpoint = $"/api/meetings/{id}/messages";
        var files = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var uploaded = await Body(await Upload(client, id, Guid.NewGuid(), $"{i}.png", i.ToString()), HttpStatusCode.OK);
            files.Add(uploaded.GetProperty("attachmentId").GetGuid());
        }
        var selected = files.OrderDescending().ToArray();
        var clientSubmissionId = Guid.NewGuid();
        var submission = new { clientSubmissionId, content = "", attachmentIds = selected };
        var sent = await Body(await Post(client, endpoint, submission), HttpStatusCode.Created);
        static Guid[] FileIds(JsonElement message) => message.GetProperty("attachments").EnumerateArray()
            .Select(file => file.GetProperty("attachmentId").GetGuid()).ToArray();
        Assert.Equal(selected, FileIds(sent.GetProperty("message")));
        var page = await Body(await client.GetAsync(endpoint, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(selected, FileIds(page.GetProperty("items").EnumerateArray().Single()));
        Assert.Equal(HttpStatusCode.OK, (await Post(client, endpoint, submission)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, endpoint, new { clientSubmissionId, content = "", attachmentIds = selected.Reverse() })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        await db.Set<MeetingAttachment>().Where(file => file.MeetingId == id).ExecuteUpdateAsync(update => update.SetProperty(file => file.MessagePosition, (int?)null), cancellationToken: TestContext.Current.CancellationToken);
        var legacy = await Body(await client.GetAsync(endpoint, TestContext.Current.CancellationToken), HttpStatusCode.OK);
        Assert.Equal(files.Order().ToArray(), FileIds(legacy.GetProperty("items").EnumerateArray().Single()));
        Assert.Equal(HttpStatusCode.OK, (await Post(client, endpoint, submission)).StatusCode);
    }

    [Fact]
    public async Task MediaPreviewUsesWhitelistedTypesAndRangesWithoutExposingUnboundOrAnonymousFiles()
    {
        using var factory = new TestApplicationFactory(); using var a = Client(factory); using var b = Client(factory);
        await Login(a, "alice"); await Login(b, "bob");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Media" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        await Body(await Post(b, "/api/meetings/join", new { meetingCode = code, displayName = "Bob" }), HttpStatusCode.OK);
        var formats = new Dictionary<string, string> { ["MP3"] = "audio/mpeg", ["wav"] = "audio/wav", ["ogg"] = "audio/ogg", ["oga"] = "audio/ogg", ["m4a"] = "audio/mp4", ["aac"] = "audio/aac", ["flac"] = "audio/flac", ["mp4"] = "video/mp4", ["m4v"] = "video/mp4", ["webm"] = "video/webm", ["ogv"] = "video/ogg", ["mov"] = "video/quicktime" };
        foreach (var (extension, type) in formats)
        {
            var uploaded = await Body(await Upload(a, id, Guid.NewGuid(), "sample." + extension, "media-bytes"), HttpStatusCode.OK);
            var fileId = uploaded.GetProperty("attachmentId").GetGuid();
            var path = $"/api/meetings/{id}/attachments/{fileId}/content";
            Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync(path + "?mode=preview", TestContext.Current.CancellationToken)).StatusCode);
            await Body(await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "", attachmentIds = new[] { fileId } }), HttpStatusCode.Created);
            using var request = new HttpRequestMessage(HttpMethod.Get, path + "?mode=preview"); request.Headers.Range = new(0, 3);
            using var response = await b.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("medi", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            using var download = await b.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        }
        // A nonmember cannot read the same media even when asking for a valid byte range.
        using var outsider = Client(factory);
        var list = await b.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/attachments", cancellationToken: TestContext.Current.CancellationToken);
        var first = list.GetProperty("items")[0].GetProperty("attachment").GetProperty("attachmentId").GetGuid();
        Assert.False((await outsider.GetAsync($"/api/meetings/{id}/attachments/{first}/content?mode=preview", TestContext.Current.CancellationToken)).IsSuccessStatusCode);
    }
    [Fact]
    public async Task TextPreviewTypesStayPlainTextAndPrivateWhileActiveContentIsNotInline()
    {
        using var factory = new TestApplicationFactory(); using var a = Client(factory); using var b = Client(factory);
        await Login(a, "alice"); await Login(b, "bob");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Preview" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        await Body(await Post(b, "/api/meetings/join", new { meetingCode = code, displayName = "Bob" }), HttpStatusCode.OK);
        var names = new[] { "中文.TXT", "note.md", "app.log", "table.csv", "table.tsv", "data.json", "data.xml", "config.yaml", "config.yml", "config.ini", "config.conf", "active.html", "active.svg", "archive.zip" };
        foreach (var name in names)
        {
            var content = "中文 <script>window.previewExecuted=true</script>\n第二行";
            var uploaded = await Body(await Upload(a, id, Guid.NewGuid(), name, content), HttpStatusCode.OK);
            var fileId = uploaded.GetProperty("attachmentId").GetGuid();
            await Body(await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "", attachmentIds = new[] { fileId } }), HttpStatusCode.Created);
            var path = $"/api/meetings/{id}/attachments/{fileId}/content?mode=preview";
            using var response = await b.GetAsync(path, TestContext.Current.CancellationToken);
            var safeText = Array.IndexOf(names, name) < 11;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(safeText ? "text/plain" : "application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(safeText ? "inline" : "attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal(content, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        var privateUpload = await Body(await Upload(a, id, Guid.NewGuid(), "private.txt", "private"), HttpStatusCode.OK);
        var privateId = privateUpload.GetProperty("attachmentId").GetGuid();
        // Unbound files are never previewable, even by another current member.
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/meetings/{id}/attachments/{privateId}/content?mode=preview", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/meetings/{id}/attachments/{privateId}/content?mode=preview", TestContext.Current.CancellationToken)).StatusCode);
        var empty = await Body(await Upload(a, id, Guid.NewGuid(), "empty.txt", ""), HttpStatusCode.OK);
        var emptyId = empty.GetProperty("attachmentId").GetGuid();
        await Body(await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "", attachmentIds = new[] { emptyId } }), HttpStatusCode.Created);
        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/meetings/{id}/attachments/{emptyId}/content?mode=preview");
        // Empty text uses an ordinary preview request because a byte range cannot overlap an empty file.
        using var emptyResponse = await b.SendAsync(range, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        Assert.Empty(await emptyResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task UploadBindPrivateRangeAndDeleteEnforceTheMessagePermission()
    {
        using var factory = new TestApplicationFactory();
        using var a = Client(factory); using var b = Client(factory); using var c = Client(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var user = UserAccount.Create("charlie", "Charlie");
            user.InitializePasswordHash(scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(user, "test-password-charlie"));
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>(); db.Add(user); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Login(a, "alice"); await Login(b, "bob"); await Login(c, "charlie");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Files" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var joined = await Body(await Post(b, "/api/meetings/join", new { meetingCode = code, displayName = "Bob" }), HttpStatusCode.OK);
        var peer = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        await Body(await Post(c, "/api/meetings/join", new { meetingCode = code, displayName = "Charlie" }), HttpStatusCode.OK);
        var uploadId = Guid.NewGuid(); var content = "<svg onload=alert(1)>arbitrary extension</svg>";
        var uploadResponse = await Upload(a, id, uploadId, "test.svg", content);
        Assert.True(uploadResponse.StatusCode == HttpStatusCode.OK, string.Join("\n", factory.LogSink.Events.Where(e => e.Exception is not null).Select(e => e.Exception!.ToString())));
        var uploaded = await Body(uploadResponse, HttpStatusCode.OK);
        var fileId = uploaded.GetProperty("attachmentId").GetGuid();
        var url = $"/api/meetings/{id}/attachments/{fileId}/content";
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        var replay = await Body(await Upload(a, id, uploadId, "test.svg", content), HttpStatusCode.OK);
        Assert.Equal(fileId, replay.GetProperty("attachmentId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(a, id, uploadId, "test.svg", "different")).StatusCode);
        var request = new { clientSubmissionId = Guid.NewGuid(), content = "", visibility = "private", recipientParticipantId = peer, attachmentIds = new[] { fileId } };
        var sent = await Body(await Post(a, $"/api/meetings/{id}/messages", request), HttpStatusCode.Created);
        Assert.Single(sent.GetProperty("message").GetProperty("attachments").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Post(a, $"/api/meetings/{id}/messages", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "reuse", attachmentIds = new[] { fileId } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        var otherFiles = await c.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/attachments", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(otherFiles.GetProperty("items").EnumerateArray());
        using var range = new HttpRequestMessage(HttpMethod.Get, url); range.Headers.Range = new(0, 3);
        using var partial = await b.SendAsync(range, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode); Assert.Equal("<svg", await partial.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var preview = await b.GetAsync(url + "?mode=preview", TestContext.Current.CancellationToken);
        Assert.Equal("attachment", preview.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", preview.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", preview.Headers.GetValues("X-Content-Type-Options").Single());
        var delete = await Body(await Post(a, $"/api/meetings/{id}/deletion", new { confirmationName = "Files" }), HttpStatusCode.Accepted);
        for (var i = 0; i < 100; i++)
        {
            var status = await a.GetFromJsonAsync<JsonElement>(delete.GetProperty("statusUrl").GetString(), cancellationToken: TestContext.Current.CancellationToken);
            if (status.GetProperty("status").GetString() == "deleted") break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "attachments", id.ToString("N"))));
        await using var final = factory.Services.CreateAsyncScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().Set<MeetingAttachment>().AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task ConfiguredLimitIsAdvertisedAndStreamingUploadRejectsExcessWithoutMetadata()
    {
        using var factory = new TestApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Configure<AttachmentOptions>(o => o.MaxFileBytes = 4)));
        using var client = Client(configured); await Login(client, "alice");
        var capability = await client.GetFromJsonAsync<JsonElement>("/api/system/capabilities", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, capability.GetProperty("maxFileBytes").GetInt64());
        var created = await Body(await Post(client, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Limit" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var rejected = await Body(await Upload(client, id, Guid.NewGuid(), "large.exe", "12345"), HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("attachment_too_large", rejected.GetProperty("code").GetString()); Assert.Equal(4, rejected.GetProperty("limitBytes").GetInt64());
        await using var scope = configured.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().Set<MeetingAttachment>().AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, id, Guid.NewGuid(), "allowed.exe", "1234")).StatusCode);
    }
    [Fact]
    public async Task DeletionCancelsFileLeaseAndWaitsForItsDisposal()
    {
        var gate = new MeetingRealtimeGate(); var id = Guid.NewGuid();
        using var file = gate.Acquire(id)!;
        using var close = await gate.AcquireExclusiveCloseAsync(id, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.NotNull(close); close.CommitClosed(); Assert.True(file.CancellationToken.IsCancellationRequested);
        Assert.Null(gate.Acquire(id));
        var drain = gate.CancelAndDrainAsync(id, TestContext.Current.CancellationToken); Assert.False(drain.IsCompleted);
        file.Dispose(); await drain.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task DefaultHundredMegabyteBoundaryAndInterruptedStreamLeaveNoPartialFiles()
    {
        using var factory = new TestApplicationFactory();
        var store = factory.Services.GetRequiredService<IAttachmentStore>();
        var id = Guid.NewGuid();
        var file = await store.WriteAsync(id, new GeneratedStream(100_000_000), CancellationToken.None);
        Assert.Equal(100_000_000, file.SizeBytes);
        using (var read = store.Open(id, file.StorageKey)) Assert.Equal(100_000_000, read.Length);
        store.Delete(id, file.StorageKey);
        var excess = await Assert.ThrowsAsync<AttachmentOperationException>(() => store.WriteAsync(id, new GeneratedStream(100_000_001), CancellationToken.None));
        Assert.Equal(413, excess.Status); Assert.Equal(100_000_000, excess.LimitBytes);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(id, new GeneratedStream(100_000, failAfterFirstRead: true), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "attachments", id.ToString("N"))));
    }
    [Fact]
    public async Task ExpiredStagedAndOrphanFilesAreCleanedButBoundFilesRemain()
    {
        using var factory = new TestApplicationFactory(); using var client = Client(factory); await Login(client, "alice");
        var created = await Body(await Post(client, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Cleanup" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var staged = await Body(await Upload(client, id, Guid.NewGuid(), "staged.bin", "staged"), HttpStatusCode.OK);
        var bound = await Body(await Upload(client, id, Guid.NewGuid(), "bound.bin", "bound"), HttpStatusCode.OK);
        var boundId = bound.GetProperty("attachmentId").GetGuid();
        await Body(await Post(client, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "", attachmentIds = new[] { boundId } }), HttpStatusCode.Created);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            foreach (var file in await db.Set<MeetingAttachment>().AsTracking().ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
                db.Entry(file).Property(x => x.CreatedAtUtc).CurrentValue = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var store = factory.Services.GetRequiredService<IAttachmentStore>();
        var orphan = await store.WriteAsync(id, new MemoryStream([1, 2, 3]), CancellationToken.None);
        var directory = Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "attachments", id.ToString("N"));
        File.SetLastWriteTimeUtc(Path.Combine(directory, orphan.StorageKey), DateTime.UtcNow.AddDays(-2));
        var cleanup = new AttachmentMaintenanceService(factory.Services.GetRequiredService<IServiceScopeFactory>(), store, TimeProvider.System, NullLogger<AttachmentMaintenanceService>.Instance);
        await cleanup.CleanAsync(CancellationToken.None);
        await using var final = factory.Services.CreateAsyncScope();
        var remaining = await final.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().Set<MeetingAttachment>().ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(boundId, Assert.Single(remaining).ID);
        Assert.Single(Directory.EnumerateFiles(directory));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/meetings/{id}/attachments/{boundId}/content", TestContext.Current.CancellationToken)).StatusCode);
    }
    private sealed class GeneratedStream : Stream
    {
        private readonly long count;
        private readonly bool failAfterFirstRead;
        private long remaining;
        public GeneratedStream(long count, bool failAfterFirstRead = false) { this.count = count; remaining = count; this.failAfterFirstRead = failAfterFirstRead; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => count - remaining; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (failAfterFirstRead && remaining < count) throw new IOException("Simulated read interruption");
            var read = (int)Math.Min(buffer.Length, remaining); buffer.Span[..read].Clear(); remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static HttpClient Client(WebApplicationFactory<Program> f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
    private static async Task Login(HttpClient c, string name) => await Body(await Post(c, "/api/auth/login", new { username = name, password = "test-password-" + name }), HttpStatusCode.OK);
    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost:5173"); return await c.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient c, Guid meeting, Guid requestId, string name, string content)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(requestId.ToString()), "uploadRequestId");
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting}/attachments") { Content = multipart };
        request.Headers.Add("Origin", "http://localhost:5173"); return await c.SendAsync(request);
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
