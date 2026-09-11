using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.AgentCollaboration;

/// <summary>Compares committed-visible Agent facts once per transaction, including raw conditional SQL writes.</summary>
public sealed class AgentActivityTransactions(IMeetingTransactionRunner transactions, IUnitOfWork unitOfWork,
    IMeetingParticipantRepository participants, IAgentCollaborationRepository agents, TimeProvider time)
    : IScopedDependency<AgentActivityTransactions>
{
    public IReadOnlyList<Guid> ChangedParticipants { get; private set; } = [];
    public IReadOnlyList<AgentActivityDto> Snapshots { get; private set; } = [];

    public async Task<T> RunAsync<T>(Guid meetingId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ChangedParticipants = [];
        Snapshots = [];
        var outcome = await transactions.RunAsync(async token =>
        {
            var before = await ReadStatesAsync(meetingId, token);
            var result = await action(token);
            // Flush only; the enclosing transaction remains responsible for physical commit/rollback.
            await unitOfWork.CommitAsync(token);
            var after = await ReadStatesAsync(meetingId, token);
            var changed = new List<Guid>();
            foreach (var (id, state) in after)
            {
                if (before.TryGetValue(id, out var previous) && previous.Payload == state.Payload) continue;
                state.Cursor.RecordActivityChange(time.GetUtcNow());
                unitOfWork.RegisterEdit(state.Cursor);
                changed.Add(id);
            }
            return (Result: result, Changed: changed, Snapshot: after.Values.Select(state => state.Payload.ToDto(state.Cursor)).ToArray());
        }, cancellationToken);
        ChangedParticipants = outcome.Changed;
        Snapshots = outcome.Snapshot;
        return outcome.Result;
    }

    public Task<IReadOnlyList<AgentActivityDto>> ReadPersistedAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => transactions.RunAsync<IReadOnlyList<AgentActivityDto>>(async token =>
            (await ReadStatesAsync(meetingId, token)).Values.Select(state => state.Payload.ToDto(state.Cursor)).ToArray(), cancellationToken);

    private async Task<Dictionary<Guid, (AgentContextCursor Cursor, AgentActivityPayload Payload)>> ReadStatesAsync(Guid meetingId, CancellationToken token)
    {
        var result = new Dictionary<Guid, (AgentContextCursor, AgentActivityPayload)>();
        var connections = await agents.ListConnectionsAsync(meetingId, cancellationToken: token);
        foreach (var participant in (await participants.ListAsync(meetingId, token)).Where(item => item.ParticipantType == ParticipantType.Agent))
        {
            var cursor = await agents.FindCursorAsync(meetingId, participant.ID, token);
            if (cursor is null) continue;
            var connection = connections.SingleOrDefault(item => item.ID == cursor.CurrentConnectionId);
            var initial = connection is null ? null : await agents.FindInitialContextAsync(meetingId, participant.ID, connection.ID, token);
            var tasks = await agents.ListTasksAsync(meetingId, participant.ID, token);
            result[participant.ID] = (cursor, AgentActivityPayload.Build(connection, initial, tasks));
        }
        return result;
    }
}
