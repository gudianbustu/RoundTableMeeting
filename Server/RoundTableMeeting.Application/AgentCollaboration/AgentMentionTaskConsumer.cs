using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentMentionTaskConsumer(
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants,
    IMessageRepository messages,
    IAgentCollaborationRepository agentRepository,
    IUnitOfWork unitOfWork,
    AgentActivityTransactions transactionRunner,
    IMeetingRealtimeGate realtimeGate,
    IAgentTaskSignalRegistry taskSignals,
    TimeProvider timeProvider) : IMessageEventConsumer
{
    public async Task ConsumeAsync(
        MessageAppendedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(envelope.MeetingId, cancellationToken);
        if (sharedLease is null || await meetings.FindActiveByIdAsync(envelope.MeetingId, cancellationToken) is null)
            return;

        var createdAgents = await transactionRunner.RunAsync(envelope.MeetingId,
            token => AllocateTasksAsync(envelope, token),
            cancellationToken);

        foreach (var agentParticipantId in createdAgents.Distinct())
            taskSignals.Signal(envelope.MeetingId, agentParticipantId);
    }

    private async Task<IReadOnlyList<Guid>> AllocateTasksAsync(
        MessageAppendedEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (await meetings.FindActiveByIdAsync(envelope.MeetingId, cancellationToken) is null)
            return [];

        var eventMessage = await messages.FindByIdAsync(
            envelope.MeetingId,
            envelope.MessageId,
            cancellationToken);
        if (eventMessage is null
            || eventMessage.Sequence != envelope.Sequence
            || eventMessage.AuthorKind != RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human)
            return [];

        var eventMentions = await messages.ListMentionsAsync(
            envelope.MeetingId,
            eventMessage.ID,
            cancellationToken);
        var eventAgentIds = eventMentions
            .Select(mention => mention.TargetParticipantId)
            .ToHashSet();
        if (eventAgentIds.Count == 0) return [];

        var agents = (await participants.ListAsync(envelope.MeetingId, cancellationToken))
            .Where(participant => participant.ParticipantType == ParticipantType.Agent
                && eventAgentIds.Contains(participant.ID))
            .ToArray();
        if (agents.Length == 0) return [];

        var createdAgents = new List<Guid>();
        var now = timeProvider.GetUtcNow();
        foreach (var agent in agents)
        {
            var cursor = await agentRepository.FindCursorAsync(
                envelope.MeetingId,
                agent.ID,
                cancellationToken);
            if (cursor is null || cursor.AllocatedThroughSequence >= envelope.Sequence)
                continue;

            var candidates = await messages.ListAfterSequenceThroughAsync(
                envelope.MeetingId,
                cursor.AllocatedThroughSequence,
                envelope.Sequence,
                cancellationToken);
            var existingTasks = (await agentRepository.ListTasksAsync(
                    envelope.MeetingId,
                    agent.ID,
                    cancellationToken))
                .ToDictionary(task => task.TriggerMessageId);
            var allocationCursor = cursor.AllocatedThroughSequence;

            foreach (var candidate in candidates)
            {
                if (candidate.AuthorKind != RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human) continue;

                if (existingTasks.TryGetValue(candidate.ID, out var existingTask))
                {
                    allocationCursor = Math.Max(allocationCursor, existingTask.TriggerSequence);
                    continue;
                }

                var mentions = candidate.ID == eventMessage.ID
                    ? eventMentions
                    : await messages.ListMentionsAsync(
                        envelope.MeetingId,
                        candidate.ID,
                        cancellationToken);
                var mention = mentions.FirstOrDefault(item => item.TargetParticipantId == agent.ID);
                if (mention is null) continue;

                var taskId = Guid.NewGuid();
                var task = AgentReplyTask.Create(
                    taskId,
                    envelope.MeetingId,
                    agent.ID,
                    candidate.ID,
                    candidate.Sequence,
                    mention.ID,
                    candidate.AuthorKind,
                    candidate.AuthorParticipantId,
                    allocationCursor,
                    AgentReplyIdempotencyKeyFactory.Create(taskId),
                    now);
                unitOfWork.RegisterAdd(task);
                existingTasks.Add(candidate.ID, task);
                allocationCursor = candidate.Sequence;
                createdAgents.Add(agent.ID);
            }

            if (allocationCursor > cursor.AllocatedThroughSequence)
            {
                cursor.AdvanceAllocatedThrough(allocationCursor, now);
                unitOfWork.RegisterEdit(cursor);
            }
        }

        return createdAgents;
    }
}
