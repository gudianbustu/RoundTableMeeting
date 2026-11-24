using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Application.RealtimeChat;

namespace RoundTableMeeting.Application.Meetings;

public sealed class MeetingWorkspaceService(
    ICurrentUserContext currentUser,
    IUserAccountRepository users,
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants,
    IUnitOfWork unitOfWork,
    IMeetingTransactionRunner transactionRunner,
    IMeetingDeletionSignal deletionSignal,
    IMeetingRealtimeGate realtimeGate,
    RealtimeChatOptions realtimeChatOptions,
    TimeProvider timeProvider,
    IMeetingLifecycleRepository lifecycle,
    IMeetingMembershipService membership,
    IMeetingLifecycleService lifecycleService,
    IMeetingDiscussionRepository discussion,
    IResolvedMessageAppendCore appendCore,
    MeetingSiteAddress? site = null) : IMeetingWorkspaceService, IScopedDependency<IMeetingWorkspaceService>
{
    public async Task<MeetingOperationResult<CreateMeetingResultDto>> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        if (request.CreationRequestId == Guid.Empty) return Invalid<CreateMeetingResultDto>("creationRequestId");
        if (request.InitialDiscussionMode is not ("directed" or "free"))
            return Invalid<CreateMeetingResultDto>("initialDiscussionMode");
        var initiallyFree = request.InitialDiscussionMode == "free";
        var account = await users.FindByIdAsync(userId, cancellationToken);
        if (account is null) return new(MeetingOperationStatus.Unauthenticated);
        string normalizedName;
        string? normalizedDescription;
        try
        {
            normalizedName = Meeting.NormalizeName(request.Name);
            normalizedDescription = Meeting.NormalizeDescription(request.Description);
        }
        catch (ArgumentException ex) { return Invalid<CreateMeetingResultDto>(ex.ParamName == "description" ? "description" : "name"); }

        var outcome = await transactionRunner.RunAsync(async token =>
        {
            var receipt = await meetings.FindReceiptAsync(userId, request.CreationRequestId, token);
            if (receipt is not null)
            {
                if (receipt.State != MeetingCreationReceiptState.Active || receipt.MeetingId is null)
                    return (MeetingOperationStatus.CreationRequestConsumed, (Meeting?)null);
                var existing = await meetings.FindByIdAsync(receipt.MeetingId.Value, token);
                if (existing is null || existing.LifecycleState != Domain.Enums.MeetingLifecycleState.Active)
                    return (MeetingOperationStatus.CreationRequestConsumed, (Meeting?)null);
                if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal)
                    || !string.Equals(existing.Description, normalizedDescription, StringComparison.Ordinal)
                    || await discussion.IsFreeAsync(existing.ID, 0, token) != initiallyFree)
                    return (MeetingOperationStatus.CreationRequestMismatch, (Meeting?)null);
                return (MeetingOperationStatus.Replayed, existing);
            }

            var meeting = Meeting.Create(normalizedName, normalizedDescription, userId, UtcNow());
            var owner = MeetingParticipant.CreateHuman(meeting.ID, userId, account.Nickname, UtcNow());
            var newReceipt = MeetingCreationReceipt.Create(userId, request.CreationRequestId, meeting.ID, UtcNow());
            unitOfWork.RegisterAdd(meeting);
            unitOfWork.RegisterAdd(AgentCollaborationBoundary.Create(meeting.ID));
            if (initiallyFree) unitOfWork.RegisterAdd(MeetingDiscussionChange.Create(meeting.ID, 0, true));
            unitOfWork.RegisterAdd(owner);
            unitOfWork.RegisterAdd(newReceipt);
            return (MeetingOperationStatus.Created, meeting);
        }, cancellationToken);

        if (outcome.Item1 is MeetingOperationStatus.CreationRequestMismatch or MeetingOperationStatus.CreationRequestConsumed)
            return new(outcome.Item1);
        using var createLease = await realtimeGate.AcquireSharedAsync(outcome.Item2!.ID, cancellationToken);
        if (createLease is null || await meetings.FindActiveByIdAsync(outcome.Item2.ID, cancellationToken) is null)
            return new(MeetingOperationStatus.CreationRequestConsumed);
        var details = await BuildDetails(outcome.Item2!, userId, cancellationToken);
        return new(outcome.Item1, new CreateMeetingResultDto(details, new(outcome.Item2!.ID, outcome.Item2.MeetingCode.ToString("D").ToLowerInvariant(), site?.PublicUrl)), IsCreated: outcome.Item1 == MeetingOperationStatus.Created);
    }

    public async Task<MeetingOperationResult<object>> SetDiscussionModeAsync(Guid meetingId, string mode, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        if (mode is not ("directed" or "free")) return Invalid<object>("mode");
        using var lease = await realtimeGate.AcquireExclusiveMutationAsync(meetingId, TimeSpan.FromSeconds(realtimeChatOptions.GateCloseTimeoutSeconds), ct);
        if (lease is null) return new(MeetingOperationStatus.DeleteBusy);
        var status = await transactionRunner.RunAsync(async token =>
        {
            var meeting = await meetings.FindActiveByIdAsync(meetingId, token);
            var member = await participants.FindByUserAsync(meetingId, userId, token);
            if (meeting is null || member is null) return MeetingOperationStatus.MeetingNotFound;
            if (meeting.CreatorUserId != userId) return MeetingOperationStatus.CreatorRequired;
            var enabled = mode == "free";
            if (await discussion.IsFreeAsync(meetingId, long.MaxValue, token) != enabled)
            {
                var notification = await appendCore.AppendAsync(meetingId, new(null, "会议", Abstraction.Contracts.RealtimeChat.MessageAuthorKind.System),
                    enabled ? "已开启自由讨论" : "已切回点名讨论", Guid.NewGuid(), cancellationToken: token);
                unitOfWork.RegisterAdd(MeetingDiscussionChange.Create(meetingId, notification.Sequence, enabled));
            }
            return MeetingOperationStatus.Success;
        }, ct);
        return new(status);
    }

    public async Task<IReadOnlyList<MeetingListItemDto>> ListAsync(CancellationToken cancellationToken = default, bool history = false)
    {
        if (!TryUser(out var userId)) return [];
        var result = new List<MeetingListItemDto>();
        foreach (var row in await meetings.ListForUserAsync(userId, cancellationToken))
        {
            if ((row.Participant.MembershipState == Domain.Enums.MeetingMembershipState.Left) != history) continue;
            if (history && row.Participant.HiddenFromHistory) continue;
            result.Add(new(row.Meeting.ID, row.Meeting.Name, row.Participant.DisplayName, row.Meeting.CreatorUserId == userId, row.Participant.JoinedAtUtc)
            { MembershipState = MembershipName(row.Participant), Permissions = Permissions(row.Meeting, row.Participant) });
        }
        return result;
    }

    public async Task<MeetingOperationResult<MeetingDetailsDto>> GetAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        using var readLease = await realtimeGate.AcquireSharedAsync(meetingId, cancellationToken);
        if (readLease is null) return new(await UnavailableStatus(meetingId, userId, cancellationToken));
        var meeting = await meetings.FindActiveByIdAsync(meetingId, cancellationToken);
        if (meeting is null) return new(await UnavailableStatus(meetingId, userId, cancellationToken));
        if (await participants.FindByUserAsync(meetingId, userId, cancellationToken) is null) return new(MeetingOperationStatus.MeetingNotFound);
        return new(MeetingOperationStatus.Success, await BuildDetails(meeting, userId, cancellationToken));
    }

    public async Task<MeetingOperationResult<MeetingInvitationDto>> InvitationAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        using var readLease = await realtimeGate.AcquireSharedAsync(meetingId, cancellationToken);
        if (readLease is null) return new(await UnavailableStatus(meetingId, userId, cancellationToken));
        var meeting = await meetings.FindActiveByIdAsync(meetingId, cancellationToken);
        var participant = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, cancellationToken);
        if (meeting is null || participant is null) return new(MeetingOperationStatus.MeetingNotFound);
        if (participant.MembershipState == Domain.Enums.MeetingMembershipState.Left) return new(MeetingOperationStatus.MembershipLeft);
        return new(MeetingOperationStatus.Success, new(meeting.ID, meeting.MeetingCode.ToString("D").ToLowerInvariant(), site?.PublicUrl));
    }

    public async Task<MeetingOperationResult<JoinPreviewDto>> PreviewJoinAsync(PreviewJoinRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var parsed = ParseMeetingCode(request.MeetingCode);
        if (parsed.Status == MeetingOperationStatus.ValidationFailed) return Invalid<JoinPreviewDto>("meetingCode");
        if (parsed.Status == MeetingOperationStatus.MeetingNotFound) return new(MeetingOperationStatus.MeetingNotFound);
        var code = parsed.Code;
        var meeting = await meetings.FindActiveByCodeAsync(code, cancellationToken);
        if (meeting is null) return new(MeetingOperationStatus.MeetingNotFound);
        var member = await participants.FindByUserAsync(meeting.ID, userId, cancellationToken);
        var account = await users.FindByIdAsync(userId, cancellationToken);
        if (account is null) return new(MeetingOperationStatus.Unauthenticated);
        return new(MeetingOperationStatus.Success, new(meeting.ID, meeting.Name, meeting.Description, account.Nickname, member?.MembershipState == Domain.Enums.MeetingMembershipState.Joined, member?.DisplayName));
    }

    public async Task<MeetingOperationResult<MeetingDetailsDto>> JoinAsync(JoinMeetingRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var parsed = ParseMeetingCode(request.MeetingCode);
        if (parsed.Status == MeetingOperationStatus.ValidationFailed) return Invalid<MeetingDetailsDto>("meetingCode");
        if (parsed.Status == MeetingOperationStatus.MeetingNotFound) return new(MeetingOperationStatus.MeetingNotFound);
        var code = parsed.Code;
        var preflight = await meetings.FindActiveByCodeAsync(code, cancellationToken);
        if (preflight is null) return new(MeetingOperationStatus.MeetingNotFound);
        using var joinLease = await realtimeGate.AcquireSharedAsync(preflight.ID, cancellationToken);
        if (joinLease is null) return new(MeetingOperationStatus.MeetingNotFound);
        string displayName;
        try { displayName = MeetingParticipant.NormalizeDisplayName(request.DisplayName); } catch (ArgumentException) { return Invalid<MeetingDetailsDto>("displayName"); }
        try
        {
            var outcome = await transactionRunner.RunAsync(async token =>
            {
                var meeting = await meetings.FindActiveByCodeAsync(code, token);
                if (meeting is null) return (MeetingOperationStatus.MeetingNotFound, (Meeting?)null);
                var current = await participants.FindByUserAsync(meeting.ID, userId, token);
                if (current is not null)
                {
                    if (current.MembershipState == Domain.Enums.MeetingMembershipState.Left)
                    {
                        current.Rejoin();
                        unitOfWork.RegisterEdit(current);
                        return (MeetingOperationStatus.Success, meeting);
                    }
                    return (MeetingOperationStatus.AlreadyMember, meeting);
                }
                if (await participants.IsNameTakenAsync(meeting.ID, displayName.ToUpperInvariant(), token))
                    return (MeetingOperationStatus.DisplayNameTaken, (Meeting?)null);
                unitOfWork.RegisterAdd(MeetingParticipant.CreateHuman(meeting.ID, userId, displayName, UtcNow()));
                return (MeetingOperationStatus.Success, meeting);
            }, cancellationToken);
            if (outcome.Item1 is MeetingOperationStatus.MeetingNotFound or MeetingOperationStatus.DisplayNameTaken)
                return new(outcome.Item1);
            return new(outcome.Item1, await BuildDetails(outcome.Item2!, userId, cancellationToken));
        }
        catch (Domain.Exceptions.MeetingJoinConflictException)
        {
            return new(MeetingOperationStatus.JoinConflict);
        }
    }

    public async Task<MeetingOperationStatus> UpdateHistoryAsync(Guid meetingId, bool rejoin, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return MeetingOperationStatus.Unauthenticated;
        using var lease = await realtimeGate.AcquireSharedAsync(meetingId, ct);
        if (lease is null) return MeetingOperationStatus.MeetingNotFound;
        return await transactionRunner.RunAsync(async token =>
        {
            var meeting = await meetings.FindActiveByIdAsync(meetingId, token);
            var member = await participants.FindByUserAsync(meetingId, userId, token);
            if (meeting is null || member is null) return MeetingOperationStatus.MeetingNotFound;
            if (member.MembershipState != Domain.Enums.MeetingMembershipState.Left)
                return rejoin ? MeetingOperationStatus.Success : MeetingOperationStatus.AlreadyMember;
            if (rejoin) member.Rejoin();
            else member.HideFromHistory();
            unitOfWork.RegisterEdit(member);
            return MeetingOperationStatus.Success;
        }, ct);
    }

    public async Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, LeaveMeetingRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var participant = await participants.FindByUserAsync(meetingId, userId, cancellationToken);
        if (participant is null) return new(MeetingOperationStatus.MeetingNotFound);
        return await membership.LeaveAsync(meetingId, participant.ID, request.OperationId, cancellationToken);
    }

    public async Task<MeetingOperationResult<MeetingDeletionStatusDto>> DeletionStatusAsync(Guid meetingId, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var deletion = await lifecycle.FindDeletionAsync(meetingId, cancellationToken);
        if (deletion is null || deletion.CreatorUserId != userId || deletion.OperationId != operationId) return new(MeetingOperationStatus.MeetingNotFound);
        return new(MeetingOperationStatus.Success, DeletionDto(deletion));
    }

    public async Task<MeetingOperationResult<object>> DeleteAsync(Guid meetingId, string confirmationName, CancellationToken cancellationToken = default, Guid? operationId = null)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        if (operationId == Guid.Empty) return Invalid<object>("operationId");
        var previous = await lifecycle.FindDeletionAsync(meetingId, cancellationToken);
        if (previous is not null)
            return previous.CreatorUserId != userId ? new(MeetingOperationStatus.MeetingNotFound)
                : new(previous.Completed ? MeetingOperationStatus.Success : MeetingOperationStatus.Accepted, DeletionDto(previous));
        string normalizedConfirmationName;
        try { normalizedConfirmationName = Meeting.NormalizeName(confirmationName); }
        catch (ArgumentException) { return Invalid<object>("confirmationName"); }
        var preflightMeeting = await meetings.FindActiveByIdAsync(meetingId, cancellationToken);
        if (preflightMeeting is null || await participants.FindByUserAsync(meetingId, userId, cancellationToken) is null)
            return new(MeetingOperationStatus.MeetingNotFound);
        if (preflightMeeting.CreatorUserId != userId) return new(MeetingOperationStatus.CreatorRequired);
        if (!string.Equals(normalizedConfirmationName, preflightMeeting.Name, StringComparison.Ordinal))
            return Invalid<object>("confirmationName");

        using var closeLease = await realtimeGate.AcquireExclusiveCloseAsync(
            meetingId, TimeSpan.FromSeconds(realtimeChatOptions.GateCloseTimeoutSeconds), cancellationToken);
        if (closeLease is null) return new(MeetingOperationStatus.DeleteBusy);
        var outcome = await transactionRunner.RunAsync(async token =>
        {
            var meeting = await meetings.FindActiveByIdAsync(meetingId, token);
            if (meeting is null || await participants.FindByUserAsync(meetingId, userId, token) is null)
                return MeetingOperationStatus.MeetingNotFound;
            if (meeting.CreatorUserId != userId) return MeetingOperationStatus.CreatorRequired;
            if (!string.Equals(normalizedConfirmationName, meeting.Name, StringComparison.Ordinal))
                return MeetingOperationStatus.ValidationFailed;
            meeting.BeginDeleting();
            unitOfWork.RegisterEdit(meeting);
            unitOfWork.RegisterAdd(MeetingDeletionOperation.Create(meetingId, userId, operationId));
            return MeetingOperationStatus.Accepted;
        }, cancellationToken);
        if (outcome == MeetingOperationStatus.Accepted)
        {
            closeLease.CommitClosed();
            deletionSignal.Notify();
            var deletion = await lifecycle.FindDeletionAsync(meetingId, cancellationToken)
                ?? throw new InvalidOperationException("The committed deletion receipt is missing.");
            return new(outcome, DeletionDto(deletion));
        }
        return outcome == MeetingOperationStatus.ValidationFailed ? Invalid<object>("confirmationName") : new(outcome);
    }

    private static MeetingDeletionStatusDto DeletionDto(MeetingDeletionOperation deletion)
        => new(deletion.MeetingId, deletion.OperationId, deletion.Completed ? "deleted" : "deleting",
            $"/api/meetings/{deletion.MeetingId:D}/deletion/{deletion.OperationId:D}");

    private async Task<MeetingDetailsDto> BuildDetails(Meeting meeting, Guid userId, CancellationToken cancellationToken)
    {
        var all = await participants.ListAsync(meeting.ID, cancellationToken);
        var mapped = all.Select(p => new ParticipantDto(p.ID, p.DisplayName, p.ParticipantType == Domain.Enums.ParticipantType.Human ? ParticipantKind.Human : ParticipantKind.Agent, p.UserId == userId, p.UserId == meeting.CreatorUserId, p.JoinedAtUtc) { MembershipState = MembershipName(p) }).ToArray();
        var current = all.Single(p => p.UserId == userId);
        var permissions = Permissions(meeting, current);
        return new(meeting.ID, meeting.Name, meeting.Description, meeting.CreatedAtUtc, mapped.FirstOrDefault(p => p.IsCurrentUser), permissions.CanDelete, permissions.CanInvite, mapped)
        { FreeDiscussion = await discussion.IsFreeAsync(meeting.ID, long.MaxValue, cancellationToken), MembershipState = MembershipName(current), Permissions = permissions };
    }
    private async Task<MeetingOperationStatus> UnavailableStatus(Guid meetingId, Guid userId, CancellationToken cancellationToken)
        => await lifecycleService.GetMemberStateAsync(meetingId, userId, cancellationToken) switch
        {
            "deleting" => MeetingOperationStatus.MeetingDeleting,
            "deleted" => MeetingOperationStatus.MeetingDeleted,
            _ => MeetingOperationStatus.MeetingNotFound
        };
    private static string MembershipName(MeetingParticipant participant) => participant.MembershipState == Domain.Enums.MeetingMembershipState.Joined ? "joined" : "left";
    private static MeetingPermissionsDto Permissions(Meeting meeting, MeetingParticipant participant)
    {
        var joined = participant.MembershipState == Domain.Enums.MeetingMembershipState.Joined;
        var creator = participant.UserId == meeting.CreatorUserId;
        return new(true, joined, joined, joined && participant.ParticipantType == Domain.Enums.ParticipantType.Human,
            joined && creator, joined && creator, joined && !creator, participant.LeftThroughSequence, participant.MembershipRevision);
    }
    private bool TryUser(out Guid userId) { userId = currentUser.UserId.GetValueOrDefault(); return currentUser.IsAuthenticated && userId != Guid.Empty; }
    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();
    private static (MeetingOperationStatus Status, Guid Code) ParseMeetingCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return (MeetingOperationStatus.ValidationFailed, Guid.Empty);
        return Guid.TryParseExact(value.Trim(), "D", out var code)
            ? (MeetingOperationStatus.Success, code)
            : (MeetingOperationStatus.MeetingNotFound, Guid.Empty);
    }
    private static MeetingOperationResult<T> Invalid<T>(string field) => new(MeetingOperationStatus.ValidationFailed, Errors: new Dictionary<string,string[]> { [field] = ["The value is invalid."] });
}
