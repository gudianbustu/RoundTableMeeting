using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingDeletionRecoveryService(
    IServiceScopeFactory scopeFactory,
    MeetingDeletionSignal signal,
    TimeProvider timeProvider,
    IMeetingRealtimeTermination realtimeTermination,
    IMeetingRealtimeGate realtimeGate,
    ILogger<MeetingDeletionRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RecoverAsync(stoppingToken);
            await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scanScope = scopeFactory.CreateAsyncScope();
            var repository = scanScope.ServiceProvider.GetRequiredService<IMeetingRepository>();
            var meetingIds = (await repository.ListDeletingAsync(cancellationToken)).Select(meeting => meeting.ID).ToArray();
            foreach (var meetingId in meetingIds) await RecoverMeetingAsync(meetingId, cancellationToken);
            await DeleteExpiredReceiptsAsync(cancellationToken);
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
            await realtimeTermination.NotifyAndTerminateAsync(meetingId, deletedAt, cancellationToken);
            await using var scope = scopeFactory.CreateAsyncScope();
            var meetings = scope.ServiceProvider.GetRequiredService<IMeetingRepository>();
            var participants = scope.ServiceProvider.GetRequiredService<IMeetingParticipantRepository>();
            var messages = scope.ServiceProvider.GetRequiredService<IMessageCleanupService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var runner = scope.ServiceProvider.GetRequiredService<IMeetingTransactionRunner>();
            await runner.RunAsync(async token =>
            {
                var meeting = await meetings.FindByIdAsync(meetingId, token);
                if (meeting is null) return true;
                await messages.DeleteForMeetingAsync(meetingId, token);
                foreach (var participant in await participants.ListAsync(meetingId, token)) unitOfWork.RegisterDelete(participant);
                var receipt = await meetings.FindActiveReceiptByMeetingIdAsync(meetingId, token);
                if (receipt is not null)
                {
                    receipt.MarkDeleted(timeProvider.GetUtcNow());
                    unitOfWork.RegisterEdit(receipt);
                }
                unitOfWork.RegisterDelete(meeting);
                return true;
            }, cancellationToken);
            realtimeGate.Forget(meetingId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Meeting deletion recovery failed for {MeetingId}. ExceptionType: {ExceptionType}", meetingId, exception.GetType().Name);
        }
    }

    private async Task DeleteExpiredReceiptsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var meetings = scope.ServiceProvider.GetRequiredService<IMeetingRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var runner = scope.ServiceProvider.GetRequiredService<IMeetingTransactionRunner>();
        await runner.RunAsync(async token =>
        {
            foreach (var receipt in await meetings.ListExpiredDeletedReceiptsAsync(timeProvider.GetUtcNow(), token)) unitOfWork.RegisterDelete(receipt);
            return true;
        }, cancellationToken);
    }
}
