using System.Security.Cryptography;
using System.Text;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
namespace RoundTableMeeting.Application.Meetings;

public sealed class MeetingLifecycleService(IMeetingRepository meetings, IMeetingParticipantRepository participants,
    IMeetingLifecycleRepository lifecycle, IMeetingTransactionRunner runner, IUnitOfWork unitOfWork)
    : IMeetingLifecycleService, IScopedDependency<IMeetingLifecycleService>
{
    public Task<string?> IssueProofAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default)
        => runner.RunAsync(async token =>
        {
            if (await meetings.FindActiveByIdAsync(meetingId, token) is null
                || !(await participants.ListAsync(meetingId, token)).Any(p => p.ID == participantId)) return null;
            var proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            unitOfWork.RegisterAdd(MeetingLifecycleProof.Create(meetingId, Hash(proof)));
            return proof;
        }, cancellationToken);

    public async Task<string?> GetMemberStateAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) return null;
        var meeting = await meetings.FindByIdAsync(meetingId, cancellationToken);
        if (meeting is not null && await participants.FindByUserAsync(meetingId, userId, cancellationToken) is not null)
            return meeting.LifecycleState == Domain.Enums.MeetingLifecycleState.Active ? "active" : "deleting";
        var deletion = await lifecycle.FindDeletionAsync(meetingId, cancellationToken);
        return deletion?.CreatorUserId == userId ? deletion.Completed ? "deleted" : "deleting" : null;
    }

    public async Task<MeetingLifecycleStatusDto?> GetStatusAsync(MeetingLifecycleStatusRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MeetingId == Guid.Empty || request.LifecycleProof is not { Length: 64 } proof
            || !proof.All(Uri.IsHexDigit)) return null;
        if (!await lifecycle.HasProofAsync(request.MeetingId, Hash(proof), cancellationToken)) return null;
        var deletion = await lifecycle.FindDeletionAsync(request.MeetingId, cancellationToken);
        if (deletion is not null) return new(request.MeetingId, deletion.Completed ? "deleted" : "deleting");
        var meeting = await meetings.FindByIdAsync(request.MeetingId, cancellationToken);
        return meeting is null ? null : new(request.MeetingId, meeting.LifecycleState == Domain.Enums.MeetingLifecycleState.Active ? "active" : "deleting");
    }
    private static string Hash(string proof) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof)));
}
