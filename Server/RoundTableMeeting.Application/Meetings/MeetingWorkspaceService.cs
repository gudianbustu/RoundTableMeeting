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
    TimeProvider timeProvider) : IMeetingWorkspaceService, IScopedDependency<IMeetingWorkspaceService>
{
    public async Task<MeetingOperationResult<CreateMeetingResultDto>> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        if (request.CreationRequestId == Guid.Empty) return Invalid<CreateMeetingResultDto>("creationRequestId");
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
                    || !string.Equals(existing.Description, normalizedDescription, StringComparison.Ordinal))
                    return (MeetingOperationStatus.CreationRequestMismatch, (Meeting?)null);
                return (MeetingOperationStatus.Replayed, existing);
            }

            var meeting = Meeting.Create(normalizedName, normalizedDescription, userId, UtcNow());
            var owner = MeetingParticipant.CreateHuman(meeting.ID, userId, account.Nickname, UtcNow());
            var newReceipt = MeetingCreationReceipt.Create(userId, request.CreationRequestId, meeting.ID, UtcNow());
            unitOfWork.RegisterAdd(meeting);
            unitOfWork.RegisterAdd(AgentCollaborationBoundary.Create(meeting.ID));
            unitOfWork.RegisterAdd(owner);
            unitOfWork.RegisterAdd(newReceipt);
            return (MeetingOperationStatus.Created, meeting);
        }, cancellationToken);

        if (outcome.Item1 is MeetingOperationStatus.CreationRequestMismatch or MeetingOperationStatus.CreationRequestConsumed)
            return new(outcome.Item1);
        var details = await BuildDetails(outcome.Item2!, userId, cancellationToken);
        return new(outcome.Item1, new CreateMeetingResultDto(details, new(outcome.Item2!.ID, outcome.Item2.MeetingCode.ToString("D").ToLowerInvariant())), IsCreated: outcome.Item1 == MeetingOperationStatus.Created);
    }

    public async Task<IReadOnlyList<MeetingListItemDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return [];
        var result = new List<MeetingListItemDto>();
        foreach (var row in await meetings.ListForUserAsync(userId, cancellationToken))
            result.Add(new(row.Meeting.ID, row.Meeting.Name, row.Participant.DisplayName, row.Meeting.CreatorUserId == userId, row.Participant.JoinedAtUtc));
        return result;
    }

    public async Task<MeetingOperationResult<MeetingDetailsDto>> GetAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var meeting = await meetings.FindActiveByIdAsync(meetingId, cancellationToken);
        if (meeting is null || await participants.FindByUserAsync(meetingId, userId, cancellationToken) is null) return new(MeetingOperationStatus.MeetingNotFound);
        return new(MeetingOperationStatus.Success, await BuildDetails(meeting, userId, cancellationToken));
    }

    public async Task<MeetingOperationResult<MeetingInvitationDto>> InvitationAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var meeting = await meetings.FindActiveByIdAsync(meetingId, cancellationToken);
        if (meeting is null || meeting.CreatorUserId != userId) return new(MeetingOperationStatus.MeetingNotFound);
        return new(MeetingOperationStatus.Success, new(meeting.ID, meeting.MeetingCode.ToString("D").ToLowerInvariant()));
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
        return new(MeetingOperationStatus.Success, new(meeting.ID, meeting.Name, meeting.Description, account.Nickname, member is not null, member?.DisplayName));
    }

    public async Task<MeetingOperationResult<MeetingDetailsDto>> JoinAsync(JoinMeetingRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
        var parsed = ParseMeetingCode(request.MeetingCode);
        if (parsed.Status == MeetingOperationStatus.ValidationFailed) return Invalid<MeetingDetailsDto>("meetingCode");
        if (parsed.Status == MeetingOperationStatus.MeetingNotFound) return new(MeetingOperationStatus.MeetingNotFound);
        var code = parsed.Code;
        string displayName;
        try { displayName = MeetingParticipant.NormalizeDisplayName(request.DisplayName); } catch (ArgumentException) { return Invalid<MeetingDetailsDto>("displayName"); }
        try
        {
            var outcome = await transactionRunner.RunAsync(async token =>
            {
                var meeting = await meetings.FindActiveByCodeAsync(code, token);
                if (meeting is null) return (MeetingOperationStatus.MeetingNotFound, (Meeting?)null);
                var current = await participants.FindByUserAsync(meeting.ID, userId, token);
                if (current is not null) return (MeetingOperationStatus.AlreadyMember, meeting);
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

    public async Task<MeetingOperationResult<object>> DeleteAsync(Guid meetingId, string confirmationName, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var userId)) return new(MeetingOperationStatus.Unauthenticated);
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
            meetingId,
            TimeSpan.FromSeconds(realtimeChatOptions.GateCloseTimeoutSeconds),
            cancellationToken);
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
            return MeetingOperationStatus.Accepted;
        }, cancellationToken);
        if (outcome == MeetingOperationStatus.Accepted)
        {
            closeLease.CommitClosed();
            deletionSignal.Notify();
        }
        return outcome switch
        {
            MeetingOperationStatus.ValidationFailed => Invalid<object>("confirmationName"),
            MeetingOperationStatus.Accepted => new(outcome, new { meetingId }),
            _ => new(outcome)
        };
    }

    private async Task<MeetingDetailsDto> BuildDetails(Meeting meeting, Guid userId, CancellationToken cancellationToken)
    {
        var all = await participants.ListAsync(meeting.ID, cancellationToken);
        var mapped = all.Select(p => new ParticipantDto(p.ID, p.DisplayName, p.ParticipantType == Domain.Enums.ParticipantType.Human ? ParticipantKind.Human : ParticipantKind.Agent, p.UserId == userId, p.UserId == meeting.CreatorUserId, p.JoinedAtUtc)).ToArray();
        return new(meeting.ID, meeting.Name, meeting.Description, meeting.CreatedAtUtc, mapped.FirstOrDefault(p => p.IsCurrentUser), meeting.CreatorUserId == userId, meeting.CreatorUserId == userId, mapped);
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
