using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class AgentRecoveryRepository(RoundTableMeetingDbContext db)
    : IAgentRecoveryRepository, IScopedDependency<IAgentRecoveryRepository>
{
    public Task<AgentConnectionSession?> FindConnectionAsync(Guid connectionSessionId, CancellationToken cancellationToken = default)
        => db.AgentConnectionSessions.AsNoTracking().SingleOrDefaultAsync(item => item.ID == connectionSessionId, cancellationToken);

    public Task<AgentRecoveryOperation?> FindJoinOperationAsync(Guid meetingId, string normalizedName, Guid operationId,
        CancellationToken cancellationToken = default)
        => db.AgentRecoveryOperations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationKind == AgentRecoveryOperationKind.Join
            && item.MeetingId == meetingId && item.NormalizedDisplayName == normalizedName && item.OperationId == operationId, cancellationToken);

    public Task<AgentRecoveryOperation?> FindResumeOperationAsync(Guid meetingId, Guid connectionSessionId, Guid operationId,
        CancellationToken cancellationToken = default)
        => db.AgentRecoveryOperations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationKind == AgentRecoveryOperationKind.Resume
            && item.MeetingId == meetingId && item.ConnectionSessionId == connectionSessionId && item.OperationId == operationId, cancellationToken);

    public Task<AgentRecoveryOperation?> FindRetryOperationAsync(Guid meetingId, Guid participantId, Guid taskId, Guid operationId,
        CancellationToken cancellationToken = default)
        => db.AgentRecoveryOperations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationKind == AgentRecoveryOperationKind.Retry
            && item.MeetingId == meetingId && item.ParticipantId == participantId && item.TaskId == taskId && item.OperationId == operationId, cancellationToken);

    public async Task<bool> SupersedeAsync(AgentConnectionFence fence, Guid replacementId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (replacementId == Guid.Empty || replacementId == fence.ConnectionSessionId)
            throw new ArgumentException("A new replacement connection is required.", nameof(replacementId));
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Takeover requires the transaction that inserts the replacement and changes its current pointer.");
        // Release the filtered active slot before EF inserts the new connection. The forward replacement
        // reference must exist when the enclosing transaction commits; a failed insertion rolls everything back.
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON", cancellationToken);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions
            SET State = 'Superseded', SupersededByConnectionId = {replacementId},
                DisconnectedAtUtc = {now}, LastActivityAtUtc = {now}, DisconnectReason = 'connection_taken_over'
            WHERE ID = {fence.ConnectionSessionId} AND MeetingId = {fence.MeetingId} AND ParticipantId = {fence.ParticipantId}
              AND ConnectionGeneration = {fence.ConnectionGeneration} AND BindingVersion = {fence.BindingVersion}
              AND McpTransportBinding = {fence.TransportBinding} AND State IN ('Activating', 'Connected', 'Disconnected') AND LastActivityAtUtc <= {now}
              AND EXISTS (SELECT 1 FROM AgentContextCursors p WHERE p.MeetingId = AgentConnectionSessions.MeetingId
                  AND p.ParticipantId = AgentConnectionSessions.ParticipantId AND p.CurrentConnectionId = AgentConnectionSessions.ID
                  AND p.ConnectionGeneration = AgentConnectionSessions.ConnectionGeneration)
              AND EXISTS (SELECT 1 FROM Meetings m WHERE m.ID = AgentConnectionSessions.MeetingId AND m.LifecycleState = 'Active')
            """, cancellationToken) == 1;
    }

    public async Task<bool> RebindAsync(AgentConnectionFence fence, string newTransportBinding, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Resume requires the transaction that records its operation and task invalidation.");
        if (string.IsNullOrWhiteSpace(newTransportBinding) || newTransportBinding.Length > 128
            || newTransportBinding == fence.TransportBinding) throw new ArgumentException("A new transport binding is required.", nameof(newTransportBinding));
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions
            SET State = 'Activating', McpTransportBinding = {newTransportBinding}, BindingVersion = BindingVersion + 1,
                DisconnectedAtUtc = NULL, DisconnectReason = NULL, LeaseExpiresAtUtc = NULL, LastActivityAtUtc = {now}
            WHERE ID = {fence.ConnectionSessionId} AND MeetingId = {fence.MeetingId} AND ParticipantId = {fence.ParticipantId}
              AND ConnectionGeneration = {fence.ConnectionGeneration} AND BindingVersion = {fence.BindingVersion}
              AND BindingVersion < 9007199254740991 AND McpTransportBinding = {fence.TransportBinding}
              AND State IN ('Connected', 'Disconnected') AND ReliabilityMode = 'explicit_ack_v1' AND LastActivityAtUtc <= {now}
              AND EXISTS (SELECT 1 FROM AgentContextCursors p WHERE p.MeetingId = AgentConnectionSessions.MeetingId
                  AND p.ParticipantId = AgentConnectionSessions.ParticipantId AND p.CurrentConnectionId = AgentConnectionSessions.ID
                  AND p.ConnectionGeneration = AgentConnectionSessions.ConnectionGeneration)
              AND EXISTS (SELECT 1 FROM Meetings m WHERE m.ID = AgentConnectionSessions.MeetingId AND m.LifecycleState = 'Active')
            """, cancellationToken) == 1;
    }

    public async Task<bool> ActivateAsync(AgentConnectionFence fence, DateTimeOffset now, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions
            SET State = 'Connected', ConnectedAtUtc = {now}, LastActivityAtUtc = {now},
                LeaseExpiresAtUtc = CASE WHEN ReliabilityMode = 'explicit_ack_v1' THEN {now.AddSeconds(leaseSeconds)} ELSE NULL END,
                DisconnectedAtUtc = NULL, DisconnectReason = NULL
            WHERE ID = {fence.ConnectionSessionId} AND MeetingId = {fence.MeetingId} AND ParticipantId = {fence.ParticipantId}
              AND ConnectionGeneration = {fence.ConnectionGeneration} AND BindingVersion = {fence.BindingVersion}
              AND McpTransportBinding = {fence.TransportBinding} AND State = 'Activating' AND LastActivityAtUtc <= {now}
              AND EXISTS (SELECT 1 FROM AgentContextCursors p WHERE p.MeetingId = AgentConnectionSessions.MeetingId
                  AND p.ParticipantId = AgentConnectionSessions.ParticipantId AND p.CurrentConnectionId = AgentConnectionSessions.ID
                  AND p.ConnectionGeneration = AgentConnectionSessions.ConnectionGeneration)
              AND EXISTS (SELECT 1 FROM Meetings m WHERE m.ID = AgentConnectionSessions.MeetingId AND m.LifecycleState = 'Active')
            """, cancellationToken) == 1;
    }

    public async Task<bool> RenewAsync(AgentConnectionFence fence, DateTimeOffset now, DateTimeOffset minimumExpiry,
        CancellationToken cancellationToken = default)
    {
        if (minimumExpiry <= now) throw new ArgumentOutOfRangeException(nameof(minimumExpiry));
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions
            SET LeaseExpiresAtUtc = max(LeaseExpiresAtUtc, {minimumExpiry}), LastActivityAtUtc = {now}
            WHERE ID = {fence.ConnectionSessionId} AND MeetingId = {fence.MeetingId} AND ParticipantId = {fence.ParticipantId}
              AND ConnectionGeneration = {fence.ConnectionGeneration} AND BindingVersion = {fence.BindingVersion}
              AND McpTransportBinding = {fence.TransportBinding} AND State = 'Connected' AND ReliabilityMode = 'explicit_ack_v1'
              AND LeaseExpiresAtUtc > {now} AND LastActivityAtUtc <= {now}
              AND EXISTS (SELECT 1 FROM AgentContextCursors p WHERE p.MeetingId = AgentConnectionSessions.MeetingId
                  AND p.ParticipantId = AgentConnectionSessions.ParticipantId AND p.CurrentConnectionId = AgentConnectionSessions.ID
                  AND p.ConnectionGeneration = AgentConnectionSessions.ConnectionGeneration)
              AND EXISTS (SELECT 1 FROM Meetings m WHERE m.ID = AgentConnectionSessions.MeetingId AND m.LifecycleState = 'Active')
            """, cancellationToken) == 1;
    }

    public async Task<bool> DisconnectAsync(AgentConnectionFence fence, string reason, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64) throw new ArgumentException("Disconnect reason is required.", nameof(reason));
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions
            SET State = 'Disconnected', DisconnectedAtUtc = {now}, LastActivityAtUtc = {now}, DisconnectReason = {reason}
            WHERE ID = {fence.ConnectionSessionId} AND MeetingId = {fence.MeetingId} AND ParticipantId = {fence.ParticipantId}
              AND ConnectionGeneration = {fence.ConnectionGeneration} AND BindingVersion = {fence.BindingVersion}
              AND McpTransportBinding = {fence.TransportBinding} AND State IN ('Activating', 'Connected') AND LastActivityAtUtc <= {now}
              AND EXISTS (SELECT 1 FROM AgentContextCursors p WHERE p.MeetingId = AgentConnectionSessions.MeetingId
                  AND p.ParticipantId = AgentConnectionSessions.ParticipantId AND p.CurrentConnectionId = AgentConnectionSessions.ID
                  AND p.ConnectionGeneration = AgentConnectionSessions.ConnectionGeneration)
            """, cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<AgentConnectionSession>> ListUnsettledConnectionsAsync(CancellationToken cancellationToken = default)
        => await db.AgentConnectionSessions.AsNoTracking().Where(item =>
            item.State == AgentConnectionState.Activating || item.State == AgentConnectionState.Connected).ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentConnectionSession>> ListUnsettledBatchAsync(Guid? afterId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        // Use the stored UUID's SQLite ordering for both the boundary and ordering.
        // Offset paging would skip rows as earlier connections leave the candidate set.
        return afterId is Guid previous
            ? await db.AgentConnectionSessions.FromSqlInterpolated($"""
                SELECT * FROM AgentConnectionSessions WHERE State IN ('Activating', 'Connected') AND ID > {previous}
                ORDER BY ID LIMIT {limit}
                """).AsNoTracking().ToArrayAsync(cancellationToken)
            : await db.AgentConnectionSessions.AsNoTracking().Where(item => item.State == AgentConnectionState.Activating || item.State == AgentConnectionState.Connected)
                .OrderBy(item => item.ID).Take(limit).ToArrayAsync(cancellationToken);
    }
}
