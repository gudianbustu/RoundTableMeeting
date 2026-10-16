using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
namespace RoundTableMeeting.RealtimeChat;

public sealed class AttachmentMeetingCleanup(IAttachmentStore store, IMeetingFileTransferGate transfers) : IMeetingDeletionResourceCleanup
{
    public async Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        await transfers.CancelAndDrainAsync(meetingId, cancellationToken);
        await store.DeleteMeetingAsync(meetingId, cancellationToken);
    }
}
public sealed class AttachmentMaintenanceService(IServiceScopeFactory scopes, IAttachmentStore store,
    TimeProvider time, ILogger<AttachmentMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await CleanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Attachment cleanup failed. ExceptionType: {ExceptionType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public async Task CleanAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddHours(-24);
        await using var scan = scopes.CreateAsyncScope();
        var candidates = await scan.ServiceProvider.GetRequiredService<IMeetingAttachmentRepository>().ExpiredAsync(cutoff, ct);
        foreach (var candidate in candidates)
        {
            await using var scope = scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMeetingAttachmentRepository>();
            await scope.ServiceProvider.GetRequiredService<IMeetingTransactionRunner>().RunAsync(async token =>
            {
                var item = await repo.FindAsync(candidate.MeetingId, candidate.ID, token);
                if (item is null || item.State != "staged" || item.CreatedAtUtc >= cutoff) return false;
                store.Delete(item.MeetingId, item.StorageKey);
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RegisterDelete(item); return true;
            }, ct);
        }
        foreach (var orphan in store.ListOldFiles(cutoff))
        {
            await using var scope = scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMeetingAttachmentRepository>();
            await scope.ServiceProvider.GetRequiredService<IMeetingTransactionRunner>().RunAsync(async token =>
            {
                if (await repo.FindStorageAsync(orphan.MeetingId, orphan.StorageKey, token) is not null) return false;
                store.Delete(orphan.MeetingId, orphan.StorageKey); return true;
            }, ct);
        }
    }
}
