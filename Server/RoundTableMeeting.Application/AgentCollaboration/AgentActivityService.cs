using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentActivityService(
    IMeetingParticipantRepository participants,
    IAgentCollaborationRepository agentRepository,
    IAgentConnectionRegistry connectionRegistry) : IAgentActivityService, IScopedDependency<IAgentActivityService>
{
    public async Task<IReadOnlyList<AgentActivityDto>> ListAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        var agents = (await participants.ListAsync(meetingId, cancellationToken))
            .Where(participant => participant.ParticipantType == ParticipantType.Agent)
            .ToArray();
        if (agents.Length == 0) return [];

        var registrations = connectionRegistry.ListRegistrations(meetingId)
            .ToDictionary(registration => registration.ParticipantId);
        var connections = await agentRepository.ListConnectionsAsync(meetingId, cancellationToken: cancellationToken);
        var result = new List<AgentActivityDto>(agents.Length);
        foreach (var agent in agents)
        {
            registrations.TryGetValue(agent.ID, out var registration);
            var participantConnections = connections.Where(item => item.ParticipantId == agent.ID).ToArray();
            var connection = registration is null
                ? participantConnections.FirstOrDefault()
                : participantConnections.FirstOrDefault(item => item.ID == registration.ConnectionSessionId);
            var tasks = await agentRepository.ListTasksAsync(meetingId, agent.ID, cancellationToken);
            result.Add(Build(agent, registration, connection, tasks));
        }
        return result;
    }

    public async Task<AgentActivityDto?> FindAsync(
        Guid meetingId,
        Guid participantId,
        CancellationToken cancellationToken = default)
        => (await ListAsync(meetingId, cancellationToken))
            .SingleOrDefault(activity => activity.ParticipantId == participantId);

    private static AgentActivityDto Build(
        MeetingParticipant agent,
        AgentConnectionRegistration? registration,
        AgentConnectionSession? connection,
        IReadOnlyList<AgentReplyTask> tasks)
    {
        var connected = registration is not null
            && connection is not null
            && connection.State == AgentConnectionState.Connected
            && connection.MeetingId == registration.MeetingId
            && connection.ParticipantId == registration.ParticipantId
            && string.Equals(connection.McpTransportBinding, registration.TransportBinding, StringComparison.Ordinal);
        if (!connected)
        {
            var changedAt = connection?.DisconnectedAtUtc
                ?? connection?.LastActivityAtUtc
                ?? agent.JoinedAtUtc;
            return new(agent.MeetingId, agent.ID, "disconnected", null, null, changedAt);
        }

        var head = tasks.FirstOrDefault(task => task.State is not AgentReplyTaskState.Replied and not AgentReplyTaskState.Cancelled);
        var latestCompletedAt = tasks.Select(task => task.RepliedAtUtc ?? task.CancelledAtUtc)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(connection!.LastActivityAtUtc)
            .Max();
        return head?.State switch
        {
            AgentReplyTaskState.Processing => new(agent.MeetingId, agent.ID, "processing", head.ID, null, head.ProcessingAtUtc ?? connection!.LastActivityAtUtc),
            AgentReplyTaskState.Delivered => new(agent.MeetingId, agent.ID, "blocked", head.ID, "delivery_not_finalized", head.DeliveredAtUtc ?? connection!.LastActivityAtUtc),
            AgentReplyTaskState.Failed => new(agent.MeetingId, agent.ID, "blocked", head.ID, head.FailureCode, head.FailedAtUtc ?? connection!.LastActivityAtUtc),
            _ => new(agent.MeetingId, agent.ID, "waiting", null, null, latestCompletedAt)
        };
    }
}
