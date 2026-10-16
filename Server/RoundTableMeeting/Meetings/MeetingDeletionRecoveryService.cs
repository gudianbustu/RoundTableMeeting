using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingDeletionRecoveryService(
    IServiceScopeFactory scopeFactory,
    MeetingDeletionSignal signal,
    TimeProvider timeProvider,
    IMeetingRealtimeTermination realtimeTermination,
    IMeetingRealtimeGate realtimeGate,
    IAgentConnectionRegistry agentConnections,
    IAgentTaskSignalRegistry agentSignals,
    ILogger<MeetingDeletionRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RecoverAsync(stoppingToken);
                await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scanScope = scopeFactory.CreateAsyncScope();
            var repository = scanScope.ServiceProvider.GetRequiredService<IMeetingRepository>();
            var meetingIds = (await repository.ListDeletingAsync(cancellationToken)).Select(meeting => meeting.ID).ToArray();
            foreach (var meetingId in meetingIds) await RecoverMeetingAsync(meetingId, cancellationToken);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Meeting deletion recovery scan failed. ExceptionType: {ExceptionType}", exception.GetType().Name);
        }
    }

    private async Task RecoverMeetingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            var deletedAt = timeProvider.GetUtcNow();
            agentConnections.TerminateMeeting(meetingId);
            agentSignals.TerminateMeeting(meetingId);
            await realtimeTermination.NotifyDeletingAsync(meetingId, cancellationToken);
            await using var scope = scopeFactory.CreateAsyncScope();
            var meetings = scope.ServiceProvider.GetRequiredService<IMeetingRepository>();
            var participants = scope.ServiceProvider.GetRequiredService<IMeetingParticipantRepository>();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IMeetingLifecycleRepository>();
            var messages = scope.ServiceProvider.GetRequiredService<IMessageCleanupService>();
            var agents = scope.ServiceProvider.GetRequiredService<IAgentCollaborationCleanupService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var runner = scope.ServiceProvider.GetRequiredService<IMeetingTransactionRunner>();
            foreach (var resources in scope.ServiceProvider.GetServices<IMeetingDeletionResourceCleanup>())
                await resources.DeleteForMeetingAsync(meetingId, cancellationToken);
            await runner.RunAsync(async token =>
            {
                var meeting = await meetings.FindByIdAsync(meetingId, token);
                if (meeting is null) return true;
                if (meeting.LifecycleState != Domain.Enums.MeetingLifecycleState.Deleting)
                    throw new InvalidOperationException("Deletion recovery requires a Deleting meeting.");
                var deletion = await lifecycle.FindDeletionAsync(meetingId, token);
                if (deletion is null)
                {
                    deletion = MeetingDeletionOperation.Create(meetingId, meeting.CreatorUserId);
                    unitOfWork.RegisterAdd(deletion);
                }
                else unitOfWork.RegisterEdit(deletion);
                await agents.DeleteForMeetingAsync(meetingId, deletedAt, token);
                await messages.DeleteForMeetingAsync(meetingId, token);
                foreach (var leave in await lifecycle.ListLeavesAsync(meetingId, token)) unitOfWork.RegisterDelete(leave);
                foreach (var participant in await participants.ListAsync(meetingId, token)) unitOfWork.RegisterDelete(participant);
                var receipt = await meetings.FindActiveReceiptByMeetingIdAsync(meetingId, token);
                if (receipt is not null)
                {
                    receipt.MarkDeleted(timeProvider.GetUtcNow());
                    unitOfWork.RegisterEdit(receipt);
                }
                unitOfWork.RegisterDelete(meeting);
                deletion.Complete(timeProvider.GetUtcNow());
                return true;
            }, cancellationToken);
            await realtimeTermination.NotifyAndTerminateAsync(meetingId, timeProvider.GetUtcNow(), cancellationToken);
            realtimeGate.Forget(meetingId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Meeting deletion recovery failed for {MeetingId}. ExceptionType: {ExceptionType}", meetingId, exception.GetType().Name);
        }
    }

}
