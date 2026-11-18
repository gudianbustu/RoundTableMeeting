using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Configuration;

namespace RoundTableMeeting.AgentCollaboration;

// One stable adapter per invitation identity. HTTP/MCP sessions may be replaced by the host.
public sealed class SimpleMeetingTools
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly IServiceScopeFactory scopes;
    private readonly string code, name, checkpoint, owner;
    private readonly string? address;
    private readonly SemaphoreSlim gate;

    private sealed class State
    {
        public string? Owner { get; set; }
        public Guid Binding { get; set; }
        public Guid Connection { get; set; }
        public DateTimeOffset RenewAfter { get; set; }
        public long Cursor { get; set; }
        public Guid? Pending { get; set; }
        public long PendingSequence { get; set; }
        public Guid Submission { get; set; }
        public string? Decision { get; set; }
        public Guid? LastHandled { get; set; }
        public string? LastDecision { get; set; }
        public Guid? Leaving { get; set; }
        public bool Left { get; set; }
    }

    internal SimpleMeetingTools(IServiceScopeFactory scopes, string databaseDirectory, string? address, string code, string name, Guid invitationKey)
    {
        this.scopes = scopes;
        this.address = address;
        owner = Convert.ToHexString(SHA256.HashData(invitationKey.ToByteArray()));
        this.code = code;
        this.name = name;
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(code + "/" + name.ToUpperInvariant()));
        gate = Gates[key[0] % Gates.Length];
        checkpoint = Path.Combine(databaseDirectory, "mcp-inbox", Convert.ToHexString(key) + ".json");
    }

    public static void Configure(HttpContext http, McpServerOptions options, JsonSerializerOptions json)
    {
        var headers = http.Request.Headers;
        var multi = headers["X-RoundTable-Mode"] == "rooms";
        var legacy = headers.ContainsKey("X-RoundTable-Meeting") || headers.ContainsKey("X-RoundTable-Agent") || headers.ContainsKey("X-RoundTable-Key");
        if (!multi && !legacy) return;
        if (multi && legacy) throw new BadHttpRequestException("Use either site or meeting configuration.");
        var db = http.RequestServices.GetRequiredService<IOptions<SqliteDatabaseOptions>>().Value.ConnectionString;
        var normalized = SqlitePathResolver.NormalizeConnectionString(db);
        var directory = SqlitePathResolver.GetDatabaseDirectory(normalized);
        var scopes = http.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var address = http.Connection.RemoteIpAddress?.ToString();
        object instance;
        if (multi)
        {
            instance = new MultiMeetingTools(scopes, directory, address,
                http.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
        }
        else
        {
            if (!Guid.TryParse(headers["X-RoundTable-Meeting"], out var code) || !Guid.TryParseExact(headers["X-RoundTable-Key"], "N", out var invitationKey) || invitationKey == Guid.Empty)
                throw new BadHttpRequestException("Invalid meeting invitation.");
            var name = MeetingParticipant.NormalizeDisplayName(Uri.UnescapeDataString(headers["X-RoundTable-Agent"].ToString()));
            instance = new SimpleMeetingTools(scopes, directory, address, code.ToString(), name, invitationKey);
        }
        options.ToolCollection = new();
        foreach (var method in instance.GetType().GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute))))
            options.ToolCollection.Add(McpServerTool.Create(method, instance, new() { SerializerOptions = json }));
    }

    private void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        var temp = checkpoint + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state));
        File.Move(temp, checkpoint, true);
    }

    private async Task<T> Locked<T>(Func<State, IServiceProvider, Task<T>> work, CancellationToken ct, bool invited = false)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = File.Exists(checkpoint)
                ? JsonSerializer.Deserialize<State>(await File.ReadAllBytesAsync(checkpoint, ct))! : new State();
            await using var scope = scopes.CreateAsyncScope();
            if (state.Owner is not null && state.Owner != owner)
            {
                if (!invited) throw new McpOperationException("invitation_changed");
                // A fresh invitation must pass the ordinary offline-only reconnect check.
                state.Connection = Guid.Empty;
            }
            state.Owner = owner;
            return await work(state, scope.ServiceProvider);
        }
        catch (McpOperationException e) { throw new ModelContextProtocol.McpException(e.Message); }
        finally { gate.Release(); }
    }

    private static McpTransportSessionKey Key(State state) => McpTransportSessionKey.FromDigest(state.Binding.ToByteArray());

    private async Task Connect(State s, IServiceProvider sp, CancellationToken ct)
    {
        var service = sp.GetRequiredService<CurrentMcpSessionService>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        if (s.Connection != Guid.Empty)
        {
            var registry = sp.GetRequiredService<IAgentConnectionRegistry>();
            if (registry.TryGetRegistration(Key(s), out var registered) && registered.ConnectionSessionId == s.Connection
                && registry.IsRegistered(registered.MeetingId, registered.ParticipantId, s.Connection, registered.TransportBinding))
            {
                if (now < s.RenewAfter) return;
                try
                {
                    await service.ExecuteAsync(Key(s), s.Connection, service.RenewAsync, ct);
                    s.RenewAfter = now.AddMinutes(1);
                    Save(s);
                    return;
                }
                catch (McpOperationException e) when (e.Message is "connection_unavailable" or "connection_expired_reconnect") { }
            }
        }
        // Use the fenced reconnect path. An existing online participant is never taken over.
        var binding = Guid.NewGuid();
        var key = McpTransportSessionKey.FromDigest(binding.ToByteArray());
        if (!File.Exists(checkpoint))
        {
            var meeting = await sp.GetRequiredService<IMeetingRepository>().FindActiveByCodeAsync(Guid.Parse(code), ct);
            var participant = meeting is null ? null : await sp.GetRequiredService<IMeetingParticipantRepository>()
                .FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
            if (participant is not null)
                s.Cursor = await sp.GetRequiredService<IMessageVisibilityRepository>().HighWatermarkAsync(meeting!.ID, participant.ID, ct);
        }
        McpConnection joined;
        try { joined = await service.JoinAsync(key, code, name, "reconnect", address, ct); }
        catch (McpOperationException e) when (e.Message == "participant_not_found")
        { joined = await service.JoinAsync(key, code, name, "create", address, ct); }
        if (s.Binding != Guid.Empty) sp.GetRequiredService<IAgentConnectionRegistry>().RemoveRegistrations(Key(s));
        s.Binding = binding;
        s.Connection = joined.ConnectionSessionId;
        s.RenewAfter = now.AddMinutes(1);
        Save(s);
    }

    private static void RequireActive(State s)
    {
        if (s.Left || s.Leaving.HasValue) throw new McpOperationException("meeting_left");
    }

    [McpServerTool(Name = "attend")]
    [Description("Join this invitation or resume it. Call once when invited, then call next.")]
    public Task<string> Attend(CancellationToken ct) => Locked(async (s, sp) =>
    {
        if (s.Leaving.HasValue) throw new McpOperationException("finish_leave_first");
        await Connect(s, sp, ct);
        if (s.Left) { s.Left = false; Save(s); }
        return "joined";
    }, ct, invited: true);

    public sealed record Delivery(string State, MessageDto? Message = null, bool FromMeetingCreator = false, string? Room = null, string? Error = null);

    [McpServerTool(Name = "next")]
    [Description("Wait for one public mention or private message. On idle call next immediately. After message, reply or skip. An interrupted wait is safe to retry.")]
    public async Task<Delivery> Next(McpServer host, IProgress<ProgressNotificationValue> progress, CancellationToken ct)
        => await WaitForMessage(() => Poll(ct), host, progress, ct);

    internal Task<Delivery> Poll(CancellationToken ct)
        => Locked(async (s, sp) =>
        {
            RequireActive(s);
            await Connect(s, sp, ct);
            var service = sp.GetRequiredService<CurrentMcpSessionService>();
            return await service.ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                var page = await sp.GetRequiredService<IMessageHistoryService>().GetPageAsync(c.MeetingId, new(AfterSequence: s.Cursor, Limit: 25), token);
                if (page.Data is null) throw new McpOperationException("history_unavailable");
                var found = page.Data.Items.FirstOrDefault(m => m.Mentions.Any(x => x.ParticipantId == c.ParticipantId)
                    || (m.Visibility == "private" && m.Recipient?.ParticipantId == c.ParticipantId));
                if (found is not null)
                {
                    if (s.Pending != found.MessageId)
                    {
                        s.Pending = found.MessageId;
                        s.PendingSequence = found.Sequence;
                        s.Submission = Guid.NewGuid();
                        s.Decision = null;
                        Save(s);
                    }
                    var meeting = await sp.GetRequiredService<IMeetingRepository>().FindActiveByIdAsync(c.MeetingId, token);
                    var creator = meeting is not null && (await sp.GetRequiredService<IMeetingParticipantRepository>().ListAsync(c.MeetingId, token))
                        .Any(p => p.ID == found.Author.ParticipantId && p.UserId == meeting.CreatorUserId);
                    return new Delivery("message", found, creator);
                }
                if (page.Data.Items.Count > 0) { s.Cursor = page.Data.Items[^1].Sequence; Save(s); }
                return new Delivery(page.Data.HasMoreAfter ? "more" : "idle");
            }, ct);
        }, ct);

    internal static async Task<Delivery> WaitForMessage(Func<Task<Delivery>> poll, McpServer host, IProgress<ProgressNotificationValue> progress, CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var nextPing = TimeSpan.FromSeconds(20);
        do
        {
            var result = await poll();
            if (result.State is "message" or "room_error") return result;
            if (elapsed.Elapsed >= nextPing)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await host.SendRequestAsync(new JsonRpcRequest { Method = "ping", Id = new RequestId(Guid.NewGuid().ToString()) }, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Delivery("idle"); }
                // Some hosts reply with method-not-found; that round trip also proves transport liveness.
                catch (McpProtocolException e) when (e.ErrorCode == McpErrorCode.MethodNotFound) { }
                catch (McpException) { return new Delivery("idle"); }
                progress.Report(new() { Progress = (int)elapsed.Elapsed.TotalSeconds, Message = "Waiting for a meeting message" });
                nextPing = elapsed.Elapsed + TimeSpan.FromSeconds(20);
            }
            if (result.State == "idle") await Task.Delay(750, ct);
        } while (elapsed.Elapsed < TimeSpan.FromMinutes(12));
        return new Delivery("idle");
    }

    [McpServerTool(Name = "reply")]
    [Description("Reply to the pending message. Private replies stay private. Retrying the same decision is safe.")]
    public Task<string> Reply(Guid messageId, string content, CancellationToken ct, Guid[]? attachmentIds = null)
        => Handle(messageId, content, attachmentIds, ct);

    [McpServerTool(Name = "skip")]
    [Description("Mark the pending message handled without sending a reply.")]
    public Task<string> Skip(Guid messageId, CancellationToken ct) => Handle(messageId, null, null, ct);

    private Task<string> Handle(Guid messageId, string? content, Guid[]? attachmentIds, CancellationToken ct) => Locked(async (s, sp) =>
    {
        RequireActive(s);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { content, attachmentIds })));
        if (s.LastHandled == messageId && s.LastDecision == digest) return "handled";
        if (s.Pending != messageId) throw new McpOperationException("no_matching_pending_message");
        if (s.Decision is not null && s.Decision != digest) throw new McpOperationException("decision_already_in_progress");
        await Connect(s, sp, ct);
        if (content is not null)
        {
            s.Decision = digest;
            Save(s);
            await sp.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                var sent = await sp.GetRequiredService<IMessageAppendService>().AppendHumanAsync(c.MeetingId,
                    new(s.Submission, content, AttachmentIds: attachmentIds, ReplyToMessageId: messageId), token);
                if (sent.Data is null)
                {
                    s.Decision = null;
                    Save(s);
                    throw new McpOperationException(JsonNamingPolicy.SnakeCaseLower.ConvertName(sent.Status.ToString()));
                }
                return true;
            }, ct);
        }
        s.Cursor = s.PendingSequence;
        s.Pending = null;
        s.LastHandled = messageId;
        s.LastDecision = digest;
        Save(s);
        return "handled";
    }, ct);

    [McpServerTool(Name = "attachment_transfer")]
    [Description("Request an HTTP transfer for one attachment. Download with GET and attachmentId, or upload raw bytes with POST and query name and uploadRequestId. Resolve URL against this server only; send bearerToken in Authorization. Use uploaded attachmentIds with reply.")]
    public Task<McpTransferTicket> Transfer(CancellationToken ct, Guid? attachmentId = null) => Locked(async (s, sp) =>
    {
        RequireActive(s);
        await Connect(s, sp, ct);
        return sp.GetRequiredService<McpTransferTickets>().Issue(Key(s), s.Connection, attachmentId.HasValue ? "GET" : "POST", attachmentId);
    }, ct);

    [McpServerTool(Name = "leave")]
    [Description("Leave on explicit request from your user or the verified meeting creator. Once left, stop waiting.")]
    public Task<string> Leave(CancellationToken ct) => Locked(async (s, sp) =>
    {
        if (s.Left) return "left";
        var meeting = await sp.GetRequiredService<IMeetingRepository>().FindActiveByCodeAsync(Guid.Parse(code), ct)
            ?? throw new McpOperationException("meeting_not_found");
        var member = await sp.GetRequiredService<IMeetingParticipantRepository>().FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
        if (member is null || member.ParticipantType != ParticipantType.Agent) throw new McpOperationException("not_joined");
        if (member.MembershipState != MeetingMembershipState.Left)
        {
            await Connect(s, sp, ct);
            s.Leaving ??= Guid.NewGuid();
            Save(s);
            await sp.GetRequiredService<CurrentMcpSessionService>().LeaveAsync(Key(s), s.Connection, s.Leaving.Value, ct);
        }
        var departed = await sp.GetRequiredService<IMeetingParticipantRepository>().FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
        s.Cursor = Math.Max(s.Cursor, departed?.LeftThroughSequence ?? 0);
        s.Leaving = null;
        s.Left = true;
        if (s.Binding != Guid.Empty) sp.GetRequiredService<IAgentConnectionRegistry>().RemoveRegistrations(Key(s));
        s.Connection = Guid.Empty;
        s.Pending = null;
        Save(s);
        return "left";
    }, ct);
}
