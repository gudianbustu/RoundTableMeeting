using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class AgentRecoveryRecordTests
{
    [Fact]
    public void InitialConfirmationPreservesWindowAndOriginalConfirmationTimeWithoutTaskDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new AgentContextWindow(10, 1, 10, 7, 10, 12000, 11000, true, false, 6, "agent-context-v1");
        var initial = AgentInitialContextDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), window, now);
        var id = initial.ID;
        Assert.Equal(InitialContextState.Prepared, initial.State);
        Assert.True(initial.Confirm(now.AddDays(30)));
        Assert.False(initial.Confirm(now.AddDays(31)));
        Assert.Equal(now.AddDays(30), initial.ConfirmedAtUtc);
        Assert.Equal(id, initial.ID);
        Assert.Equal(window, initial.GetWindow());
    }

    [Fact]
    public void EmptyInitialSnapshotIsValidButTaskRangeCannotMasqueradeAsInitialHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var empty = new AgentContextWindow(0, null, null, null, null, 12000, 0, false, false, 0, "agent-context-v1");
        var initial = AgentInitialContextDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), empty, now);
        Assert.Throws<ArgumentOutOfRangeException>(() => initial.Confirm(now.AddTicks(-1)));
        Assert.Null(initial.ConfirmedAtUtc);
        Assert.True(initial.Confirm(now));
        var task = new AgentContextWindow(10, 5, 10, 7, 10, 12000, 11000, true, false, 2, "agent-context-v1");
        Assert.Throws<ArgumentException>(() => AgentInitialContextDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), task, now));
    }

    [Fact]
    public void ContextWindowRejectsMissingEndpointAndConflictingBudgetMetadata()
    {
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(10, 1, 10, 5, 9, 12000, 11000, true, false, 4, "agent-context-v1"));
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(10, 1, 10, 5, 10, 12000, 11000, true, false, 3, "agent-context-v1"));
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(10, 1, 10, 5, 10, 12000, 13000, true, false, 4, "agent-context-v1"));
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(0, 1, 1, 1, 1, 12000, 0, false, false, 0, "agent-context-v1"));
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(1, null, null, null, null, 12000, 0, false, false, 0, "agent-context-v1"));
        Assert.Throws<ArgumentException>(() => new AgentContextWindow(10, 1, 10, 5, 10, 12000, 13000, true, true, 4, "agent-context-v1"));
        Assert.True(new AgentContextWindow(10, 1, 10, 10, 10, 12000, 13000, true, true, 9, "agent-context-v1").OverLimit);
    }

    [Fact]
    public void RecoveryReceiptsKeepDistinctScopesAndOriginalResultVersions()
    {
        var meeting = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var digest = new string('A', 64);
        var join = AgentRecoveryOperation.ForJoin(meeting, participant, "WORKER", operation, digest, connection, 3, 1, "binding-1", now);
        var resume = AgentRecoveryOperation.ForResume(meeting, participant, connection, operation, digest, 3, 2, "binding-2", now);
        var retry = AgentRecoveryOperation.ForRetry(meeting, participant, Guid.NewGuid(), operation, digest, 4, now);
        Assert.Equal(AgentRecoveryOperationKind.Join, join.OperationKind);
        Assert.Equal("WORKER", join.NormalizedDisplayName);
        Assert.Null(join.ConnectionSessionId);
        Assert.Equal(connection, join.ResultConnectionId);
        Assert.Equal(1, join.ResultBindingVersion);
        Assert.Equal(connection, resume.ConnectionSessionId);
        Assert.Equal(2, resume.ResultBindingVersion);
        Assert.Equal(join.ResultConnectionGeneration, resume.ResultConnectionGeneration);
        Assert.Null(retry.ResultConnectionId);
        Assert.Equal(4, retry.ResultRetryCycle);
        Assert.Equal(operation, retry.OperationId);
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentRecoveryOperation.ForResume(meeting, participant, connection, operation, digest, 3, 0, "binding", now));
        Assert.Throws<ArgumentException>(() => AgentRecoveryOperation.ForRetry(meeting, participant, Guid.NewGuid(), operation, "not-a-digest", 1, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentRecoveryOperation.ForRetry(meeting, participant, Guid.NewGuid(), operation, digest, 0, now));
    }
}
