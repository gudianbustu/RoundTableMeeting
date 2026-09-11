using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentActivityService(IMeetingRepository meetings, IMeetingParticipantRepository participants,
    IAgentCollaborationRepository agents, IAgentRecoveryRepository recovery, IUnitOfWork unitOfWork,
    IAgentConnectionRegistry registry, IAgentJoinCoordinator joins, AgentActivityTransactions transactions,
    AgentCollaborationOptions options, TimeProvider time, IAgentTaskSignalRegistry signals, IAgentActivityNotifier notifier)
    : IAgentActivityService, IScopedDependency<IAgentActivityService>
{
    // Subscription owns the meeting shared gate. Participant locks prevent observing
    // an activation between its two commits. New roster members require a fresh pass.
    public async Task<IReadOnlyList<AgentActivityDto>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var roster = (await participants.ListAsync(meetingId, cancellationToken)).Where(item => item.ParticipantType == ParticipantType.Agent)
                .OrderBy(item => item.NormalizedDisplayName, StringComparer.Ordinal).ToArray();
            var leases = new List<IAsyncDisposable>();
            var retired = new List<AgentConnectionRegistration>();
            var active = false;
            try
            {
                foreach (var participant in roster)
                    leases.Add(await joins.AcquireAsync(meetingId, participant.NormalizedDisplayName, cancellationToken));
                active = await transactions.RunAsync(meetingId, async token =>
                {
                    if (await meetings.FindActiveByIdAsync(meetingId, token) is null) return false;
                    var currentAgents = (await participants.ListAsync(meetingId, token)).Where(item => item.ParticipantType == ParticipantType.Agent).ToArray();
                    if (currentAgents.Any(item => !roster.Any(locked => locked.ID == item.ID))) throw new SnapshotRosterChangedException();
                    foreach (var participant in currentAgents)
                        await ConvergeConnectionAsync(meetingId, participant.ID, retired, token);
                    return true;
                }, cancellationToken);
            }
            catch (SnapshotRosterChangedException) { continue; }
            finally
            {
                for (var index = leases.Count - 1; index >= 0; index--) await leases[index].DisposeAsync();
            }
            if (!active) return [];
            var snapshot = transactions.Snapshots;
            foreach (var registration in retired)
            {
                registry.Retire(registration);
                signals.Signal(registration.MeetingId, registration.ParticipantId);
            }
            foreach (var changed in snapshot.Where(item => transactions.ChangedParticipants.Contains(item.ParticipantId)))
                await notifier.NotifyAsync(changed, notifyPresence: true, cancellationToken);
            return snapshot;
        }
    }

    // Event publication reads committed facts only. Agent operations still own their
    // participant lock here, so this path must not re-enter snapshot convergence.
    public async Task<AgentActivityDto?> FindAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default)
        => (await transactions.ReadPersistedAsync(meetingId, cancellationToken)).SingleOrDefault(item => item.ParticipantId == participantId);

    private async Task ConvergeConnectionAsync(Guid meetingId, Guid participantId, List<AgentConnectionRegistration> retired, CancellationToken token)
    {
        var cursor = await agents.FindCursorAsync(meetingId, participantId, token);
        if (cursor?.CurrentConnectionId is not Guid id) return;
        var connection = await agents.FindConnectionAsync(meetingId, id, token);
        if (connection is null || connection.State is not (AgentConnectionState.Connected or AgentConnectionState.Activating)) return;
        var now = time.GetUtcNow();
        AgentTaskFailureCode? reason = !registry.IsRegistered(meetingId, participantId, id, connection.McpTransportBinding)
            ? AgentTaskFailureCode.ServerRestart : connection.ReliabilityMode == "explicit_ack_v1" && connection.LeaseExpiresAtUtc <= now
                ? AgentTaskFailureCode.ConnectionLeaseExpired : null;
        if (reason is null) return;
        var fence = new AgentConnectionFence(meetingId, participantId, id, connection.ConnectionGeneration, connection.BindingVersion, connection.McpTransportBinding);
        if (!await recovery.DisconnectAsync(fence, AgentTaskFailurePolicy.ToStoredCode(reason.Value), now, token)) return;
        var policy = new AgentTaskFailurePolicy(options.MaximumAutomaticRetries, options.RetryBackoffSeconds);
        foreach (var task in await agents.ListTasksAsync(meetingId, participantId, token))
        {
            if (connection.ReliabilityMode == "legacy_v1" && task.InitializeLegacyFailureDecision()) unitOfWork.RegisterEdit(task);
            if (task.State is not (AgentReplyTaskState.Delivered or AgentReplyTaskState.Processing)) continue;
            if (task.CurrentAttemptId is Guid attemptId)
            {
                var attempt = await agents.FindDeliveryAttemptAsync(meetingId, task.ID, attemptId, token);
                if (attempt is not null && attempt.Invalidate(reason.Value, now)) unitOfWork.RegisterEdit(attempt);
            }
            if (task.FailReliable(reason.Value, policy, now)) unitOfWork.RegisterEdit(task);
        }
        retired.Add(new(meetingId, participantId, id, connection.McpTransportBinding)
        { ConnectionGeneration = connection.ConnectionGeneration, BindingVersion = connection.BindingVersion,
            ReliabilityMode = connection.ReliabilityMode });
    }

    private sealed class SnapshotRosterChangedException : Exception;
}
