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
    private readonly string memberDirectory, invitationAlias;
    private readonly string? address;
    private readonly SemaphoreSlim gate;

    private sealed class State
    {
        public string? Owner { get; set; }
        public Guid? PinnedParticipantId { get; set; }
        public Guid Binding { get; set; }
        public Guid Connection { get; set; }
        public DateTimeOffset RenewAfter { get; set; }
        public long Cursor { get; set; }
        public Guid? Pending { get; set; }
        public long PendingSequence { get; set; }
        public Guid Submission { get; set; }
        public string? Decision { get; set; }
        public Guid? LastInterrupted { get; set; }
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
        // Renames can join two nickname paths to one checkpoint. Serialize checkpoint resolution per meeting.
        gate = Gates[SHA256.HashData(Encoding.UTF8.GetBytes(code))[0] % Gates.Length];
        var inbox = Path.Combine(databaseDirectory, "mcp-inbox");
        checkpoint = Path.Combine(inbox, Convert.ToHexString(key) + ".json");
        memberDirectory = Path.Combine(inbox, "members");
        invitationAlias = Path.Combine(memberDirectory, Convert.ToHexString(key) + "." + owner + ".member");
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
            instance = MultiMeetingTools.ForRequest(http);
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

    private string MemberCheckpoint(Guid id) => Path.Combine(memberDirectory, id.ToString("N") + ".json");

    private static void WriteAtomically(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }

    private void Save(State state) => WriteAtomically(state.PinnedParticipantId is Guid id ? MemberCheckpoint(id) : checkpoint,
        JsonSerializer.SerializeToUtf8Bytes(state));

    private void RememberInvitation(Guid id) => WriteAtomically(invitationAlias, id.ToByteArray());

    private async Task<string> ResolveCheckpoint(IServiceProvider services, CancellationToken ct)
    {
        if (!Directory.Exists(memberDirectory)) return checkpoint;
        if (File.Exists(invitationAlias)) return MemberCheckpoint(new Guid(await File.ReadAllBytesAsync(invitationAlias, ct)));
        if (File.Exists(checkpoint)) return checkpoint;
        var meeting = await services.GetRequiredService<IMeetingRepository>().FindActiveByCodeAsync(Guid.Parse(code), ct);
        if (meeting is null) return checkpoint;
        var participant = await services.GetRequiredService<IMeetingParticipantRepository>()
            .FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
        return participant is not null && File.Exists(MemberCheckpoint(participant.ID)) ? MemberCheckpoint(participant.ID) : checkpoint;
    }

    private async Task<T> Locked<T>(Func<State, IServiceProvider, Task<T>> work, CancellationToken ct, bool invited = false)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var path = await ResolveCheckpoint(scope.ServiceProvider, ct);
            var state = File.Exists(path) ? JsonSerializer.Deserialize<State>(await File.ReadAllBytesAsync(path, ct))! : new State();
            if (state.Owner is not null && state.Owner != owner)
            {
                if (!invited) throw new McpOperationException("invitation_changed");
                // A fresh invitation must pass the ordinary offline-only reconnect check.
                state.Connection = Guid.Empty;
            }
            state.Owner = owner;
            var result = await work(state, scope.ServiceProvider);
            if (invited && state.PinnedParticipantId is Guid id) RememberInvitation(id);
            return result;
        }
        catch (McpOperationException e) { throw new ModelContextProtocol.McpException(e.Message); }
        catch (AttachmentOperationException e) { throw new ModelContextProtocol.McpException(e.Code); }
        finally { gate.Release(); }
    }

    private static McpTransportSessionKey Key(State state) => McpTransportSessionKey.FromDigest(state.Binding.ToByteArray());

    private async Task Connect(State s, IServiceProvider sp, CancellationToken ct, bool resumeOnly = false)
    {
        var service = sp.GetRequiredService<CurrentMcpSessionService>();
        if (resumeOnly && s.Connection == Guid.Empty) throw new McpOperationException("connection_unavailable");
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
        var currentName = name;
        if (s.PinnedParticipantId is Guid stableId)
        {
            var meeting = await sp.GetRequiredService<IMeetingRepository>().FindActiveByCodeAsync(Guid.Parse(code), ct)
                ?? throw new McpOperationException("meeting_not_found");
            var member = (await sp.GetRequiredService<IMeetingParticipantRepository>().ListAsync(meeting.ID, ct))
                .SingleOrDefault(p => p.ID == stableId) ?? throw new McpOperationException("participant_not_found");
            currentName = member.DisplayName;
        }
        else if (!File.Exists(checkpoint))
        {
            var meeting = await sp.GetRequiredService<IMeetingRepository>().FindActiveByCodeAsync(Guid.Parse(code), ct);
            var participant = meeting is null ? null : await sp.GetRequiredService<IMeetingParticipantRepository>()
                .FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
            if (participant is not null)
                s.Cursor = await sp.GetRequiredService<IMessageVisibilityRepository>().HighWatermarkAsync(meeting!.ID, participant.ID, ct);
        }
        McpConnection joined;
        try { joined = await service.JoinAsync(key, code, currentName, "reconnect", address, ct, resumeOnly ? s.Connection : null); }
        catch (McpOperationException e) when (!resumeOnly && e.Message == "participant_not_found" && !s.PinnedParticipantId.HasValue)
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

    public sealed record Delivery(string State, MessageDto? Message = null, bool FromMeetingCreator = false, string? Room = null, string? Error = null, IReadOnlyList<MessageDto>? DiscussionContext = null);

    [McpServerTool(Name = "next")]
    [Description("Wait for a mention, private message, or voluntary discussion turn (message.optionalAgentResponse=true). On idle call next immediately. After message, reply or skip. An interrupted wait is safe to retry.")]
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
                if (!s.Pending.HasValue) s.Cursor = Math.Max(s.Cursor, await sp.GetRequiredService<IMeetingDiscussionRepository>().InboxStartAsync(c.MeetingId, c.ParticipantId, token));
                var page = await sp.GetRequiredService<IMessageHistoryService>().GetPageAsync(c.MeetingId, new(AfterSequence: s.Cursor, Limit: 25), token);
                if (page.Data is null) throw new McpOperationException("history_unavailable");
                if (s.Pending is Guid pending && page.Data.Items.Any(m => m.MessageId == pending && m.DiscussionInterrupted))
                {
                    if (s.Decision is not null && (await sp.GetRequiredService<IMessageLookupService>()
                        .GetSubmissionAsync(c.MeetingId, s.Submission, token)).Data?.Status == "saved")
                    {
                        s.LastHandled = pending;
                        s.LastDecision = s.Decision;
                    }
                    else s.LastInterrupted = pending;
                    s.Cursor = s.PendingSequence;
                    s.Pending = null;
                    s.Decision = null;
                    Save(s);
                }
                var eligible = page.Data.Items.Where(m => m.Sequence > s.Cursor && m.IsForAgent(c.ParticipantId)).ToArray();
                var found = eligible.FirstOrDefault();
                if (found is not null)
                {
                    MessageDto[] context = [];
                    if (found.OptionalAgentResponse && found.MessageId != found.DiscussionTopicId && found.DiscussionTopicId.HasValue)
                    {
                        var windowEnd = found.CreatedAt.AddMilliseconds(750);
                        if (!s.Pending.HasValue && sp.GetRequiredService<TimeProvider>().GetUtcNow() < windowEnd)
                            return new Delivery("idle");
                        var batch = eligible.TakeWhile(m => m.OptionalAgentResponse && m.DiscussionTopicId == found.DiscussionTopicId
                            && m.CreatedAt <= windowEnd && (!s.Pending.HasValue || m.Sequence <= s.PendingSequence)).Take(4).ToArray();
                        context = batch[..^1];
                        found = batch[^1];
                    }
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
                    return new Delivery("message", found, creator, DiscussionContext: context.Length > 0 ? context : null);
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

    private Task<T> WithTextSession<T>(Func<McpLongText, AgentConnectionSession, CancellationToken, Task<T>> action, CancellationToken ct)
        => Locked(async (state, services) =>
        {
            RequireActive(state);
            await Connect(state, services, ct, resumeOnly: true);
            var text = services.GetRequiredService<McpLongText>();
            return await services.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(state), state.Connection,
                (connection, token) => action(text, connection, token), ct);
        }, ct);

    internal Task<TextPartReceipt> StageTextPart(Guid draftId, int index, int partCount, long totalBytes, string content, CancellationToken ct)
        => WithTextSession((text, connection, token) => text.Stage(connection, draftId, index, partCount, totalBytes, content, token), ct);

    internal Task<MessageTextPage> ReadMessageText(Guid messageId, long offsetBytes, int limitBytes, CancellationToken ct)
        => WithTextSession((text, connection, token) => text.Read(connection, messageId, offsetBytes, limitBytes, token), ct);

    internal async Task<string> SubmitStagedText(Guid draftId, string destination, Guid? recipientId, string? recipientName,
        Guid? messageId, Guid? replyToMessageId, Guid[]? attachments, CancellationToken ct)
    {
        if (draftId == Guid.Empty) throw new McpException("invalid_draft_id");
        if (destination == "reply" && messageId.HasValue && recipientId is null && recipientName is null)
            return await Handle(messageId.Value, "", attachments, ct, replyToMessageId, draftId);
        if (messageId.HasValue || replyToMessageId.HasValue || destination is not ("public" or "private") ||
            (destination == "public" && (recipientId.HasValue || recipientName is not null)))
            throw new McpException("invalid_text_destination");
        return (await SendMessage(draftId, "", ct, destination == "public", recipientId, recipientName, attachments, draftId)).ToString();
    }

    internal Task<string> RenameSelf(string displayName, CancellationToken ct) => Locked(async (state, services) =>
    {
        RequireActive(state);
        await Connect(state, services, ct, resumeOnly: true);
        var session = services.GetRequiredService<CurrentMcpSessionService>();
        return await session.ExecuteAsync(Key(state), state.Connection, async (connection, token) =>
        {
            if (!state.PinnedParticipantId.HasValue)
            {
                // Pin the invitation before releasing its original nickname, also across a restart.
                state.PinnedParticipantId = connection.ParticipantId;
                Save(state);
                File.Delete(checkpoint);
            }
            // After interruption the unchanged nickname resolves the member checkpoint until this alias is durable.
            RememberInvitation(connection.ParticipantId);
            var result = await services.GetRequiredService<Meetings.MeetingNicknameService>()
                .RenameAgentAsync(connection.MeetingId, connection.ParticipantId, displayName, token);
            return result switch
            {
                Abstraction.Enums.MeetingOperationStatus.Success => "renamed",
                Abstraction.Enums.MeetingOperationStatus.DisplayNameTaken => throw new McpOperationException("display_name_taken"),
                Abstraction.Enums.MeetingOperationStatus.ValidationFailed => throw new McpOperationException("validation_failed"),
                _ => throw new McpOperationException("meeting_not_found")
            };
        }, ct);
    }, ct);

    internal Task<McpMeetingInfo> ListParticipants(CancellationToken ct)
        => Locked(async (state, services) =>
        {
            RequireActive(state);
            await Connect(state, services, ct, resumeOnly: true);
            var session = services.GetRequiredService<CurrentMcpSessionService>();
            return await session.ExecuteAsync(Key(state), state.Connection, session.InfoAsync, ct);
        }, ct);

    [McpServerTool(Name = "history")]
    [Description("Read recent messages visible to you only when earlier context is needed. This leaves next and its pending decision untouched. Use nextBeforeSequence for an older page.")]
    public Task<MessagePageDto> History(CancellationToken ct, long? beforeSequence = null, int limit = 5)
        => Locked(async (s, sp) =>
        {
            RequireActive(s);
            if (limit is < 1 or > 10 || beforeSequence < 1) throw new McpOperationException("validation_failed");
            await Connect(s, sp, ct);
            return await sp.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                var page = await sp.GetRequiredService<IMessageHistoryService>().GetPageAsync(c.MeetingId, new(BeforeSequence: beforeSequence, Limit: limit), token);
                return page.Data ?? throw new McpOperationException("history_unavailable");
            }, ct);
        }, ct);

    [McpServerTool(Name = "reply")]
    [Description("messageId acknowledges the pending delivery. For a voluntary turn, set replyToMessageId to the specific public contribution you are answering within that topic. Omit it to quote the delivered message. Private replies stay private. Retrying the same decision is safe. On discussion_ended discard the draft and call next.")]
    public Task<string> Reply(Guid messageId, string content, CancellationToken ct, Guid[]? attachmentIds = null, Guid? replyToMessageId = null)
        => Handle(messageId, content, attachmentIds, ct, replyToMessageId);

    [McpServerTool(Name = "send_public_message")]
    [Description("Post a new public message visible to everyone in this meeting, independently of pending deliveries. Use a fresh clientSubmissionId UUID per message and reuse it with the same payload on retry. Returns the message ID.")]
    public Task<Guid> SendPublicMessage(Guid clientSubmissionId, string content, CancellationToken ct, Guid[]? attachmentIds = null)
        => SendMessage(clientSubmissionId, content, ct, true, null, null, attachmentIds);

    [McpServerTool(Name = "send_private_message")]
    [Description("Start a private message to one member of this meeting. Specify exactly one of recipientParticipantId or recipientDisplayName (exact meeting nickname). Save a fresh clientSubmissionId UUID per message and reuse it with the same payload on retry. Returns the message ID. Independent of pending deliveries.")]
    public Task<Guid> SendPrivateMessage(Guid clientSubmissionId, string content, CancellationToken ct,
        Guid? recipientParticipantId = null, string? recipientDisplayName = null, Guid[]? attachmentIds = null)
        => SendMessage(clientSubmissionId, content, ct, false, recipientParticipantId, recipientDisplayName, attachmentIds);

    private Task<Guid> SendMessage(Guid clientSubmissionId, string content, CancellationToken ct, bool isPublic,
        Guid? recipientParticipantId, string? recipientDisplayName, Guid[]? attachmentIds, Guid? draftId = null)
        => Locked(async (s, sp) =>
        {
            RequireActive(s);
            if (clientSubmissionId == Guid.Empty || (!isPublic && recipientParticipantId.HasValue == (recipientDisplayName is not null)))
                throw new McpOperationException("validation_failed");
            await Connect(s, sp, ct, resumeOnly: draftId.HasValue);
            return await sp.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                var recipient = recipientParticipantId;
                var currentlyResolved = recipientParticipantId;
                if (recipientDisplayName is not null)
                {
                    string normalized;
                    try { normalized = MeetingParticipant.NormalizeDisplayName(recipientDisplayName).ToUpperInvariant(); }
                    catch (ArgumentException) { throw new McpOperationException("validation_failed"); }
                    var member = await sp.GetRequiredService<IMeetingParticipantRepository>()
                        .FindByNormalizedNameAsync(c.MeetingId, normalized, token);
                    currentlyResolved = member?.ID;
                    var receipt = await sp.GetRequiredService<IMessageRepository>()
                        .FindBySubmissionAsync(c.MeetingId, c.ParticipantId, clientSubmissionId, token);
                    if (receipt is not null && receipt.RecipientParticipantId is null) throw new McpOperationException("submission_mismatch");
                    recipient = receipt?.RecipientParticipantId ?? currentlyResolved ?? throw new McpOperationException("recipient_unavailable");
                }
                var payload = new SubmitMessageRequest(clientSubmissionId, content, Visibility: isPublic ? "public" : "private",
                    RecipientParticipantId: recipient, AttachmentIds: attachmentIds)
                {
                    StartsPublicTopic = isPublic,
                    SourceRecipient = isPublic ? null : new(recipientDisplayName, currentlyResolved)
                };
                var prepared = await sp.GetRequiredService<McpLongText>().Prepare(c, payload, draftId, token);
                var sent = await sp.GetRequiredService<IMessageAppendService>().AppendHumanAsync(c.MeetingId, prepared, token);
                return sent.Data?.MessageId ?? throw new McpOperationException(JsonNamingPolicy.SnakeCaseLower.ConvertName(sent.Status.ToString()));
            }, ct);
        }, ct);

    [McpServerTool(Name = "skip")]
    [Description("Mark the pending message handled without sending a reply.")]
    public Task<string> Skip(Guid messageId, CancellationToken ct) => Handle(messageId, null, null, ct);

    private Task<string> Handle(Guid messageId, string? content, Guid[]? attachmentIds, CancellationToken ct, Guid? replyToMessageId = null, Guid? draftId = null) => Locked(async (s, sp) =>
    {
        RequireActive(s);
        var target = replyToMessageId ?? messageId;
        var decision = draftId.HasValue ? JsonSerializer.SerializeToUtf8Bytes(new { draftId, attachmentIds, target })
            : target == messageId ? JsonSerializer.SerializeToUtf8Bytes(new { content, attachmentIds })
            : JsonSerializer.SerializeToUtf8Bytes(new { content, attachmentIds, replyToMessageId });
        var digest = Convert.ToHexString(SHA256.HashData(decision));
        async Task<MessageOperationResult<MessageDto>> PublishReply(AgentConnectionSession c, CancellationToken token)
        {
            var request = new SubmitMessageRequest(draftId ?? s.Submission, content!, AttachmentIds: attachmentIds, ReplyToMessageId: target)
            {
                SourcePendingMessageId = draftId.HasValue ? messageId : null
            };
            var prepared = await sp.GetRequiredService<McpLongText>().Prepare(c, request, draftId, token);
            return await sp.GetRequiredService<IMessageAppendService>().AppendHumanAsync(c.MeetingId, prepared, token);
        }
        if (draftId is null && s.LastHandled == messageId && s.LastDecision == digest) return "handled";
        if (draftId is null && s.LastInterrupted == messageId) return "discussion_ended";
        if (s.Pending != messageId)
        {
            if (draftId is not Guid submittedDraft) throw new McpOperationException("no_matching_pending_message");
            await Connect(s, sp, ct, resumeOnly: true);
            return await sp.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                // A retry may arrive after other deliveries. Only replay a durable receipt, without touching the cursor.
                var existing = await sp.GetRequiredService<IMessageRepository>().FindBySubmissionAsync(c.MeetingId, c.ParticipantId, submittedDraft, token);
                if (existing is null)
                {
                    if (s.LastInterrupted == messageId) return "discussion_ended";
                    throw new McpOperationException("no_matching_pending_message");
                }
                var replay = await PublishReply(c, token);
                return replay.Data?.MessageId.ToString() ?? throw new McpOperationException(JsonNamingPolicy.SnakeCaseLower.ConvertName(replay.Status.ToString()));
            }, ct);
        }
        Guid? publishedId = null;
        if (s.Decision is not null && s.Decision != digest) throw new McpOperationException("decision_already_in_progress");
        await Connect(s, sp, ct, resumeOnly: draftId.HasValue);
        if (content is not null)
        {
            s.Decision = digest;
            Save(s);
            var accepted = await sp.GetRequiredService<CurrentMcpSessionService>().ExecuteAsync(Key(s), s.Connection, async (c, token) =>
            {
                if (target != messageId)
                {
                    var discussion = sp.GetRequiredService<IMeetingDiscussionRepository>();
                    var pending = await discussion.FindTurnAsync(messageId, token);
                    var quoted = await discussion.FindTurnAsync(target, token);
                    if (pending?.Optional != true || quoted?.Optional != true || pending.MeetingId != c.MeetingId
                        || quoted.MeetingId != c.MeetingId || pending.TopicId != quoted.TopicId)
                    {
                        s.Decision = null;
                        Save(s);
                        throw new McpOperationException("invalid_reply_target");
                    }
                }
                MessageOperationResult<MessageDto> sent;
                try
                {
                    sent = await PublishReply(c, token);
                }
                catch
                {
                    s.Decision = null;
                    Save(s);
                    throw;
                }
                if (sent.Status == MessageOperationStatus.DiscussionEnded) return false;
                if (sent.Data is null)
                {
                    s.Decision = null;
                    Save(s);
                    throw new McpOperationException(JsonNamingPolicy.SnakeCaseLower.ConvertName(sent.Status.ToString()));
                }
                publishedId = sent.Data.MessageId;
                return true;
            }, ct);
            if (!accepted)
            {
                s.LastInterrupted = messageId;
                s.Cursor = s.PendingSequence;
                s.Pending = null;
                s.Decision = null;
                Save(s);
                return "discussion_ended";
            }
        }
        s.Cursor = s.PendingSequence;
        s.Pending = null;
        s.LastHandled = messageId;
        s.LastDecision = digest;
        Save(s);
        return draftId.HasValue ? publishedId!.Value.ToString() : "handled";
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
        var repository = sp.GetRequiredService<IMeetingParticipantRepository>();
        var member = s.PinnedParticipantId is Guid stableId
            ? (await repository.ListAsync(meeting.ID, ct)).SingleOrDefault(p => p.ID == stableId)
            : await repository.FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), ct);
        if (member is null || member.ParticipantType != ParticipantType.Agent) throw new McpOperationException("not_joined");
        if (member.MembershipState != MeetingMembershipState.Left)
        {
            await Connect(s, sp, ct);
            s.Leaving ??= Guid.NewGuid();
            Save(s);
            await sp.GetRequiredService<CurrentMcpSessionService>().LeaveAsync(Key(s), s.Connection, s.Leaving.Value, ct);
        }
        var departed = (await repository.ListAsync(meeting.ID, ct)).SingleOrDefault(p => p.ID == member.ID);
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
