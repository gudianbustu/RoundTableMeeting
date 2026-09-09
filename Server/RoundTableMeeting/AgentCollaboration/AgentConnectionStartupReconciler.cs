using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentConnectionStartupReconciler(
    IServiceScopeFactory scopes,
    IAgentConnectionRegistry registry,
    IAgentJoinCoordinator joins,
    TimeProvider time,
    ILogger<AgentConnectionStartupReconciler> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError("Agent connection reconciliation failed. ExceptionType: {ExceptionType}", exception.GetType().Name);
            }
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentCollaborationRepository>();
        var participants = scope.ServiceProvider.GetRequiredService<IMeetingParticipantRepository>();
        foreach (var connection in await repository.ListConnectedConnectionsAsync(cancellationToken))
        {
            var participant = (await participants.ListAsync(connection.MeetingId, cancellationToken))
                .SingleOrDefault(item => item.ID == connection.ParticipantId);
            if (participant is null) continue;
            // Join holds this lease through database commit and registry registration.
            await using var lease = await joins.AcquireAsync(connection.MeetingId, participant.NormalizedDisplayName, cancellationToken);
            if (registry.IsRegistered(connection.MeetingId, connection.ParticipantId, connection.ID, connection.McpTransportBinding)) continue;
            await repository.DisconnectConnectionAsync(connection.MeetingId, connection.ParticipantId,
                connection.ID, connection.McpTransportBinding, time.GetUtcNow(), cancellationToken);
        }
    }
}
