using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentConnectionStartupReconciler(
    IServiceScopeFactory scopes,
    AgentCollaborationOptions options,
    TimeProvider time,
    IAgentConnectionRegistry registry,
    ILogger<AgentConnectionStartupReconciler> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken, currentProtocolOnly: true);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.RecoveryScanIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReconcileAsync(stoppingToken, currentProtocolOnly: true); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError("Agent connection reconciliation failed. ExceptionType: {ExceptionType}", exception.GetType().Name);
            }
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default, bool currentProtocolOnly = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRecoveryRepository>();
        Guid? afterId = null;
        while (true)
        {
            var batch = await repository.ListUnsettledBatchAsync(afterId, 100, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var connection in batch)
            {
                if (currentProtocolOnly)
                {
                    if (connection.Transport != RoundTableMeeting.Domain.Entities.AgentConnectionSession.ParticipantSequenceTransport) continue;
                    if (connection.LeaseExpiresAtUtc > time.GetUtcNow() &&
                        registry.IsRegistered(connection.MeetingId, connection.ParticipantId, connection.ID, connection.McpTransportBinding)) continue;
                }
                await using var itemScope = scopes.CreateAsyncScope();
                await itemScope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(
                    new AgentConnectionRegistration(connection.MeetingId, connection.ParticipantId, connection.ID, connection.McpTransportBinding)
                    { ConnectionGeneration = connection.ConnectionGeneration, BindingVersion = connection.BindingVersion,
                        ReliabilityMode = connection.ReliabilityMode }, cancellationToken: cancellationToken);
            }
            afterId = batch[^1].ID;
        }
    }
}
