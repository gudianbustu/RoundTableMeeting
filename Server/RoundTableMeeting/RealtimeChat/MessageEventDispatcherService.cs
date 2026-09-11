using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MessageEventDispatcherService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, RealtimeChatOptions options, ILogger<MessageEventDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (await DispatchBatchAsync(stoppingToken) == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError("Message event dispatch scan failed. ExceptionType: {ExceptionType}", exception.GetType().Name); await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var candidates = await ListCandidatesAsync(cancellationToken);
        var processed = 0;
        foreach (var candidate in candidates)
        {
            var leaseId = Guid.NewGuid();
            if (!await TryLeaseAsync(candidate, leaseId, cancellationToken)) continue;
            try
            {
                MessageAppendedEnvelope? envelope;
                await using (var eventScope = scopeFactory.CreateAsyncScope())
                {
                    var db = eventScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                    envelope = await db.MessageAppendedEvents.AsNoTracking().Where(item => item.ID == candidate.EventId)
                        .Select(item => new MessageAppendedEnvelope(item.ID, item.MessageId, item.MeetingId, item.Sequence, item.OccurredAtUtc)).SingleOrDefaultAsync(cancellationToken);
                }
                if (envelope is not null)
                {
                    await using var consumerScope = scopeFactory.CreateAsyncScope();
                    var catalog = consumerScope.ServiceProvider.GetRequiredService<IMessageEventConsumerCatalog>();
                    var consumer = (IMessageEventConsumer)consumerScope.ServiceProvider.GetRequiredService(catalog.GetRequiredServiceType(candidate.ConsumerName));
                    await consumer.ConsumeAsync(envelope, cancellationToken);
                }
                await CompleteAsync(candidate.Id, candidate.ConsumerName, leaseId, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var suspended = await FailAsync(candidate.Id, candidate.ConsumerName, leaseId, candidate.AttemptCount, CancellationToken.None);
                if (suspended) logger.LogWarning("Message delivery suspended. DeliveryId: {DeliveryId}, ConsumerName: {ConsumerName}, AttemptCount: {AttemptCount}", candidate.Id, candidate.ConsumerName, candidate.AttemptCount + 1);
                logger.LogError("Message event consumer failed. DeliveryId: {DeliveryId}, ExceptionType: {ExceptionType}", candidate.Id, exception.GetType().Name);
            }
        }
        return processed;
    }

    private async Task<IReadOnlyList<DeliveryCandidate>> ListCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var now = timeProvider.GetUtcNow();
        var pendingRows = await db.MessageEventDeliveries.AsNoTracking().Where(item => item.State == MessageEventDeliveryState.Pending)
            .OrderBy(item => item.ID).Take(options.EventDispatcherBatchSize * 4)
            .Select(item => new DeliveryCandidate(item.ID, item.EventId, item.ConsumerName, item.State, item.AttemptCount, item.AvailableAtUtc, item.LeaseId, item.LeaseExpiresAtUtc)).ToListAsync(cancellationToken);
        var due = pendingRows.Where(item => item.AvailableAtUtc <= now).Take(options.EventDispatcherBatchSize).ToList();
        if (due.Count == options.EventDispatcherBatchSize) return due;
        var leasedRows = await db.MessageEventDeliveries.AsNoTracking().Where(item => item.State == MessageEventDeliveryState.Leased)
            .OrderBy(item => item.ID).Take(options.EventDispatcherBatchSize * 4)
            .Select(item => new DeliveryCandidate(item.ID, item.EventId, item.ConsumerName, item.State, item.AttemptCount, item.AvailableAtUtc, item.LeaseId, item.LeaseExpiresAtUtc)).ToListAsync(cancellationToken);
        due.AddRange(leasedRows.Where(item => item.LeaseExpiresAtUtc <= now).Take(options.EventDispatcherBatchSize - due.Count));
        return due;
    }

    private async Task<bool> TryLeaseAsync(DeliveryCandidate candidate, Guid leaseId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var now = timeProvider.GetUtcNow();
        var query = db.MessageEventDeliveries.Where(item => item.ID == candidate.Id && item.ConsumerName == candidate.ConsumerName && item.State == candidate.State);
        query = candidate.State == MessageEventDeliveryState.Leased ? query.Where(item => item.LeaseId == candidate.LeaseId) : query.Where(item => item.LeaseId == null);
        return await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.State, MessageEventDeliveryState.Leased)
            .SetProperty(item => item.LeaseId, leaseId)
            .SetProperty(item => item.LeaseExpiresAtUtc, now.AddSeconds(options.EventLeaseSeconds)), cancellationToken) == 1;
    }

    private async Task CompleteAsync(Guid deliveryId, string consumerName, Guid leaseId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        await db.MessageEventDeliveries.Where(item => item.ID == deliveryId && item.ConsumerName == consumerName && item.State == MessageEventDeliveryState.Leased && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.State, MessageEventDeliveryState.Completed)
                .SetProperty(item => item.CompletedAtUtc, timeProvider.GetUtcNow()).SetProperty(item => item.LeaseId, (Guid?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken);
    }

    private async Task<bool> FailAsync(Guid deliveryId, string consumerName, Guid leaseId, int attemptCount, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var nextAttempt = attemptCount + 1;
        var suspended = nextAttempt >= options.EventRetryAlertAfterAttempts;
        var delay = Math.Min(options.EventRetryMaxSeconds, options.EventRetryBaseSeconds * Math.Pow(2, Math.Min(attemptCount, 8)));
        var changed = await db.MessageEventDeliveries.Where(item => item.ID == deliveryId && item.ConsumerName == consumerName && item.State == MessageEventDeliveryState.Leased && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.State, suspended ? MessageEventDeliveryState.Suspended : MessageEventDeliveryState.Pending)
                .SetProperty(item => item.AttemptCount, nextAttempt).SetProperty(item => item.AvailableAtUtc, timeProvider.GetUtcNow().AddSeconds(delay))
                .SetProperty(item => item.LeaseId, (Guid?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken);
        return changed == 1 && suspended;
    }

    private sealed record DeliveryCandidate(Guid Id, Guid EventId, string ConsumerName, MessageEventDeliveryState State, int AttemptCount, DateTimeOffset AvailableAtUtc, Guid? LeaseId, DateTimeOffset? LeaseExpiresAtUtc);
}
