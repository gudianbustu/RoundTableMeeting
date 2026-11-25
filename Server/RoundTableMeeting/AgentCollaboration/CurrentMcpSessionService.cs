using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.AgentCollaboration;

/// <summary>Current MCP adapter. Historical task/delivery cursors are never used as message positions.</summary>
public sealed class CurrentMcpSessionService(IMeetingRepository meetings, IMeetingParticipantRepository participants,
    IAgentCollaborationRepository agents, IUnitOfWork unitOfWork, AgentActivityTransactions transactions,
    IAgentConnectionRegistry registry, IAgentJoinCoordinator joins, IMeetingRealtimeGate gate,
    MeetingAgentContext actor, IMeetingLifecycleService lifecycle, IAgentActivityNotifier notifier,
    IAgentActivityService activity, TimeProvider time, IAgentJoinRateLimiter rateLimiter,
    IAgentRecoveryRepository recovery, IMeetingLifecycleRepository membershipLifecycle, IMessageVisibilityRepository visibility, IMeetingRealtimeTermination termination, RoundTableMeeting.RealtimeChat.MeetingPresenceRegistry presence,
    RoundTableMeeting.RealtimeChat.HumanAccountPresence accounts,
    Microsoft.AspNetCore.SignalR.IHubContext<RoundTableMeeting.RealtimeChat.MeetingChatHub> hub, IAvatarRepository avatars)
{
    public const int LeaseSeconds = 300;
    private bool notifyAfterOperation;
    // Called while the subscription holds the meeting gate; never revive historical task connections.
    public async Task<IReadOnlyList<Guid>> GetOnlineAsync(Guid meetingId, CancellationToken ct)
    {
        var online = new List<Guid>();
        foreach (var r in registry.ListRegistrations(meetingId))
        {
            var c = await agents.FindConnectionAsync(meetingId, r.ConnectionSessionId, ct);
            var cursor = await agents.FindCursorAsync(meetingId, r.ParticipantId, ct);
            if (c?.Transport == AgentConnectionSession.ParticipantSequenceTransport && c.State == AgentConnectionState.Connected
                && c.LeaseExpiresAtUtc > time.GetUtcNow() && c.ConnectionGeneration == r.ConnectionGeneration
                && cursor?.CurrentConnectionId == c.ID && c.McpTransportBinding == r.TransportBinding)
                online.Add(r.ParticipantId);
        }
        return online;
    }    public async Task<McpConnection> JoinAsync(McpTransportSessionKey transport, string meetingCode, string displayName,
        string joinMode, string? address, CancellationToken ct, Guid? expectedConnection = null, long? expectedRemovalEpoch = null, string? invitationDigest = null, string? previousInvitationBinding = null)
    {
        if (!Guid.TryParse(meetingCode, out var code) || joinMode is not ("create" or "reconnect" or "takeover")) throw new McpOperationException("validation_failed");
        var name = MeetingParticipant.NormalizeDisplayName(displayName);
        if (!rateLimiter.TryAcquire(address, meetingCode, name, transport, out _)) throw new McpOperationException("rate_limited");
        await using var reservation = await registry.ReserveTransportAsync(transport, ct);
        var meeting = await meetings.FindActiveByCodeAsync(code, ct) ?? throw new McpOperationException("meeting_not_found");
        using var shared = await gate.AcquireSharedAsync(meeting.ID, ct);
        if (shared is null) throw new McpOperationException("meeting_deleting");
        await using var participantLock = await joins.AcquireAsync(meeting.ID, name.ToUpperInvariant(), ct);
        if (registry.TryGetRegistration(transport, out var already))
        {
            if (expectedConnection.HasValue && already.ConnectionSessionId != expectedConnection)
                throw new McpOperationException("connection_unavailable");
            if (!registry.IsRegistered(already.MeetingId, already.ParticipantId, already.ConnectionSessionId, already.TransportBinding)) throw new McpOperationException("new_transport_required");
            var existing = (await participants.ListAsync(meeting.ID, ct)).SingleOrDefault(p => p.ID == already.ParticipantId);
            if (already.MeetingId != meeting.ID || existing?.NormalizedDisplayName != name.ToUpperInvariant()) throw new McpOperationException("transport_already_bound");
            var connection = await ValidateAsync(transport, already.ConnectionSessionId, ct);
            return await DescribeAsync(connection, existing, ct);
        }
        if (registry.ContainsTransport(transport)) throw new McpOperationException("new_transport_required");
        AgentConnectionRegistration? previous = null;
        var joined = await transactions.RunAsync(meeting.ID, async token =>
        {
            if (await meetings.FindActiveByIdAsync(meeting.ID, token) is null) throw new McpOperationException("meeting_not_found");
            var p = await participants.FindByNormalizedNameAsync(meeting.ID, name.ToUpperInvariant(), token);
            if (expectedRemovalEpoch.HasValue && (p?.RemovalEpoch ?? 0) != expectedRemovalEpoch) throw new McpOperationException("membership_left");
            if (p is not null && (p.ParticipantType != ParticipantType.Agent || joinMode == "create")) throw new McpOperationException("display_name_taken");
            if (p is null && joinMode != "create") throw new McpOperationException("participant_not_found");
            if (p is null) { p = MeetingParticipant.CreateAgent(meeting.ID, name, time.GetUtcNow()); unitOfWork.RegisterAdd(p); }
            if (invitationDigest is not null && !await avatars.BindInvitationAsync(meeting.ID, p.NormalizedDisplayName, invitationDigest, p.ID, joinMode == "create", previousInvitationBinding, token))
                throw new McpOperationException("invalid_invitation");
            var cursor = await agents.FindCursorAsync(meeting.ID, p.ID, token);
            if (expectedConnection.HasValue)
            {
                if (p.MembershipState != MeetingMembershipState.Joined) throw new McpOperationException("membership_left");
                if (cursor?.CurrentConnectionId != expectedConnection) throw new McpOperationException("connection_unavailable");
            }
            if (cursor is null) { cursor = AgentContextCursor.Create(meeting.ID, p.ID, 0, time.GetUtcNow()); unitOfWork.RegisterAdd(cursor); }
            previous = registry.ListRegistrations(meeting.ID).SingleOrDefault(r => r.ParticipantId == p.ID);
            var old = cursor.CurrentConnectionId is Guid oldId ? await agents.FindConnectionAsync(meeting.ID, oldId, token) : null;
            if (joinMode == "reconnect" && previous is not null && old?.LeaseExpiresAtUtc > time.GetUtcNow()) throw new McpOperationException("agent_online_use_takeover");
            var now = time.GetUtcNow();
            var next = AgentConnectionSession.CreateParticipantSequence(meeting.ID, p.ID, transport.ToStorageBinding(), cursor.NextConnectionGeneration(), now);
            if (old is not null && !await recovery.SupersedeAsync(new AgentConnectionFence(old.MeetingId, old.ParticipantId, old.ID,
                old.ConnectionGeneration, old.BindingVersion, old.McpTransportBinding), next.ID, now, token))
                throw new McpOperationException("connection_conflict");
            p.Rejoin(); unitOfWork.RegisterEdit(p);
            next.Activate(1, now, LeaseSeconds); unitOfWork.RegisterAdd(next);
            cursor.AssignConnection(next, now); unitOfWork.RegisterEdit(cursor);
            return (Connection: next, Participant: p);
        }, ct);
        var registration = Registration(joined.Connection);
        if (!registry.TryActivate(transport, registration, previous)) throw new McpOperationException("new_transport_required");
        await participantLock.DisposeAsync(); shared.Dispose();
        await NotifyAsync(meeting.ID, joined.Participant.ID);
        await hub.Clients.Group(RoundTableMeeting.RealtimeChat.MeetingChatHub.Group(meeting.ID)).SendAsync("membershipChanged", ct);
        return await DescribeAsync(joined.Connection, joined.Participant, ct);
    }

    public async Task<T> ExecuteAsync<T>(McpTransportSessionKey transport, Guid connectionId,
        Func<AgentConnectionSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (!registry.TryGetRegistration(transport, out var r) || r.ConnectionSessionId != connectionId) throw new McpOperationException("connection_unavailable");
        using var shared = await gate.AcquireSharedAsync(r.MeetingId, ct);
        if (shared is null) throw new McpOperationException("meeting_deleting");
        var participant = (await participants.ListAsync(r.MeetingId, ct)).SingleOrDefault(p => p.ID == r.ParticipantId)
            ?? throw new McpOperationException("meeting_not_found");
        await using var participantLock = await joins.AcquireAsync(r.MeetingId, participant.NormalizedDisplayName, ct);
        var connection = await ValidateAsync(transport, connectionId, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registry.GetBindingCancellationToken(transport, r));
        var remaining = connection.LeaseExpiresAtUtc!.Value - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new McpOperationException("connection_expired_reconnect");
        linked.CancelAfter(remaining);
        using var identity = actor.Enter(r.MeetingId, r.ParticipantId);
        var result = await operation(connection, linked.Token);
        identity.Dispose(); await participantLock.DisposeAsync(); shared.Dispose();
        if (notifyAfterOperation) { notifyAfterOperation = false; await NotifyAsync(r.MeetingId, r.ParticipantId); }
        return result;
    }

    public async Task<AgentConnectionSession> ValidateAsync(McpTransportSessionKey transport, Guid connectionId, CancellationToken ct)
    {
        if (!registry.TryGetRegistration(transport, out var r) || r.ConnectionSessionId != connectionId) throw new McpOperationException("connection_unavailable");
        if (!registry.IsRegistered(r.MeetingId, r.ParticipantId, r.ConnectionSessionId, r.TransportBinding)) throw new McpOperationException("connection_unavailable");
        if (await meetings.FindActiveByIdAsync(r.MeetingId, ct) is null) throw new McpOperationException("meeting_not_found");
        var p = (await participants.ListAsync(r.MeetingId, ct)).SingleOrDefault(p => p.ID == r.ParticipantId);
        if (p?.MembershipState != MeetingMembershipState.Joined) throw new McpOperationException("membership_left");
        var c = await agents.FindConnectionAsync(r.MeetingId, connectionId, ct);
        var cursor = await agents.FindCursorAsync(r.MeetingId, r.ParticipantId, ct);
        if (c is null || c.Transport != AgentConnectionSession.ParticipantSequenceTransport || cursor?.CurrentConnectionId != c.ID || c.McpTransportBinding != transport.ToStorageBinding()
            || c.ConnectionGeneration != r.ConnectionGeneration || c.State != AgentConnectionState.Connected
            || c.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new McpOperationException("connection_expired_reconnect");
        return c;
    }
    public async Task<McpConnection> RenewAsync(AgentConnectionSession c, CancellationToken ct)
    {
        await transactions.RunAsync(c.MeetingId, token =>
        {
            if (!c.Renew(c.BindingVersion, time.GetUtcNow(), LeaseSeconds)) throw new McpOperationException("connection_expired_reconnect");
            unitOfWork.RegisterEdit(c); return Task.FromResult(true);
        }, ct);
        var p = (await participants.ListAsync(c.MeetingId, ct)).Single(p => p.ID == c.ParticipantId);
        return await DescribeAsync(c, p, ct, issueProof: false);
    }
    public async Task<McpMeetingInfo> InfoAsync(AgentConnectionSession c, CancellationToken ct)
    {
        var m = await meetings.FindActiveByIdAsync(c.MeetingId, ct) ?? throw new McpOperationException("meeting_not_found");
        var humanOnline = presence.GetOnline(m.ID).ToHashSet();
        var agentOnline = (await GetOnlineAsync(m.ID, ct)).ToHashSet();
        return new(m.ID, m.Name, m.Description, (await participants.ListAsync(m.ID, ct))
            .Select(p => new McpParticipant(p.ID, p.DisplayName, p.ParticipantType.ToString().ToLowerInvariant(),
                p.MembershipState.ToString().ToLowerInvariant(), p.MembershipState == MeetingMembershipState.Joined
                    && (p.ParticipantType == ParticipantType.Agent ? agentOnline.Contains(p.ID) : accounts.IsOnline(p.UserId) || humanOnline.Contains(p.ID)) ? "online" : "offline", p.UserId == m.CreatorUserId)).ToArray());
    }
    public async Task<McpAcknowledgement> DisconnectAsync(AgentConnectionSession c, CancellationToken ct)
    {
        await transactions.RunAsync(c.MeetingId, token => { c.DisconnectBinding(c.BindingVersion, "client_disconnected", time.GetUtcNow()); unitOfWork.RegisterEdit(c); return Task.FromResult(true); }, ct);
        registry.Retire(Registration(c));
        notifyAfterOperation = true;
        return new("disconnected");
    }
    public async Task<MeetingMembershipDto> LeaveAsync(McpTransportSessionKey transport, Guid connectionId, Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new McpOperationException("validation_failed");
        if (!registry.TryGetRegistration(transport, out var r) || r.ConnectionSessionId != connectionId) throw new McpOperationException("connection_unavailable");
        using var exclusive = await gate.AcquireExclusiveMutationAsync(r.MeetingId, TimeSpan.FromSeconds(10), ct);
        if (exclusive is null) throw new McpOperationException("meeting_busy");
        var p = (await participants.ListAsync(r.MeetingId, ct)).SingleOrDefault(p => p.ID == r.ParticipantId) ?? throw new McpOperationException("meeting_not_found");
        await using var participantLock = await joins.AcquireAsync(r.MeetingId, p.NormalizedDisplayName, ct);
        var c = await ValidateAsync(transport, connectionId, ct);
        var result = await transactions.RunAsync(r.MeetingId, async token =>
        {
            var prior = await membershipLifecycle.FindLeaveAsync(r.MeetingId, p.ID, operationId, token);
            if (prior is not null) return new MeetingMembershipDto(p.ID, "left", prior.VisibleThroughSequence, prior.MembershipRevision);
            p.Leave(await visibility.HighWatermarkAsync(r.MeetingId, p.ID, token));
            c.DisconnectBinding(c.BindingVersion, "membership_left", time.GetUtcNow());
            unitOfWork.RegisterEdit(c); unitOfWork.RegisterEdit(p); unitOfWork.RegisterAdd(MeetingLeaveOperation.Create(p, operationId));
            return new MeetingMembershipDto(p.ID, "left", p.LeftThroughSequence, p.MembershipRevision);
        }, ct);
        await termination.TerminateParticipantAsync(r.MeetingId, p.ID, CancellationToken.None);
        await participantLock.DisposeAsync(); exclusive.Dispose();
        await NotifyAsync(r.MeetingId, p.ID);
        return result;
    }
    private async Task<McpConnection> DescribeAsync(AgentConnectionSession c, MeetingParticipant p, CancellationToken ct, bool issueProof = true)
        => new(c.ID, c.MeetingId, p.ID, p.DisplayName, c.ConnectionGeneration, c.LeaseExpiresAtUtc, issueProof ? await lifecycle.IssueProofAsync(c.MeetingId, p.ID, ct) : null);
    private async Task NotifyAsync(Guid meetingId, Guid participantId)
    {
        var snapshot = await activity.FindAsync(meetingId, participantId, CancellationToken.None);
        if (snapshot is not null) await notifier.NotifyAsync(snapshot, true, CancellationToken.None);
    }
    public static AgentConnectionRegistration Registration(AgentConnectionSession c) => new(c.MeetingId, c.ParticipantId, c.ID, c.McpTransportBinding)
        { ConnectionGeneration = c.ConnectionGeneration, BindingVersion = c.BindingVersion, ReliabilityMode = c.ReliabilityMode };
}
