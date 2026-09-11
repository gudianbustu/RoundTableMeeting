using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class AgentRecoveryStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T00:00:00Z");

    [Fact]
    public void ResumePreservesIdentityGenerationAndBudgetButFencesOldBinding()
    {
        var connection = Connection();
        var id = connection.ID;
        Assert.Null(connection.LeaseExpiresAtUtc);
        Assert.False(connection.Renew(1, Now, 120));
        Assert.True(connection.Activate(1, Now, 120));
        Assert.True(connection.Renew(1, Now.AddSeconds(1), 120, 300, 30));
        var longLease = connection.LeaseExpiresAtUtc;
        Assert.True(connection.Renew(1, Now.AddSeconds(2), 120));
        Assert.Equal(longLease, connection.LeaseExpiresAtUtc);
        Assert.True(connection.BeginResume(1, "second-binding", Now.AddSeconds(3)));
        Assert.Equal(AgentConnectionState.Activating, connection.State);
        Assert.False(connection.DisconnectBinding(1, "transport_closed", Now.AddSeconds(4)));
        Assert.False(connection.Activate(1, Now.AddSeconds(4), 120));
        Assert.True(connection.Activate(2, Now.AddSeconds(4), 120));
        Assert.Equal(id, connection.ID);
        Assert.Equal(1, connection.ConnectionGeneration);
        Assert.Equal(1000, connection.DeclaredContextCharacterLimit);
        Assert.True(connection.Supersede(Guid.NewGuid(), Now.AddSeconds(5)));
        Assert.False(connection.BeginResume(2, "third-binding", Now.AddSeconds(6)));
        Assert.False(connection.Disconnect(Now.AddSeconds(6)));
        Assert.Equal(AgentConnectionState.Superseded, connection.State);
    }

    [Fact]
    public void ExpiredLeaseCannotBeRenewedAndReliableIdentityCannotDowngradeAfterActivationFailure()
    {
        var connection = Connection();
        var cursor = AgentContextCursor.Create(connection.MeetingId, connection.ParticipantId, 10, Now);
        cursor.AssignConnection(connection, Now);
        Assert.Equal("explicit_ack_v1", cursor.RequiredReliabilityMode);
        Assert.True(connection.DisconnectBinding(1, "activation_failed", Now));
        var legacy = AgentConnectionSession.CreateActivating(connection.MeetingId, connection.ParticipantId, 1000,
            "legacy", 2, "legacy_v1", null, Now);
        Assert.Throws<InvalidOperationException>(() => cursor.AssignConnection(legacy, Now));
        Assert.Equal(connection.ID, cursor.CurrentConnectionId);
        Assert.True(connection.BeginResume(1, "second", Now));
        Assert.True(connection.Activate(2, Now, 120));
        Assert.False(connection.Renew(2, Now.AddSeconds(120), 120));
    }

    [Fact]
    public void ConfirmationReplayPreservesOriginalTimeButExpiredFirstConfirmationFails()
    {
        var connection = Connection();
        connection.Activate(1, Now, 120);
        var task = TaskFor(connection);
        var attempt = Attempt(task, connection, Now);
        Assert.True(attempt.Confirm(Now.AddSeconds(59)));
        Assert.False(attempt.Confirm(Now.AddHours(1)));
        Assert.Equal(Now.AddSeconds(59), attempt.ConfirmedAtUtc);
        var expired = Attempt(task, connection, Now);
        Assert.Throws<InvalidOperationException>(() => expired.Confirm(Now.AddSeconds(60)));
    }

    [Fact]
    public void FailurePreservesBlockedHeadUntilExplicitRetryAndNewCycleFirstDeliveryIsFree()
    {
        var connection = Connection();
        connection.Activate(1, Now, 120);
        var task = TaskFor(connection);
        var policy = new AgentTaskFailurePolicy(1, [2]);
        var first = Attempt(task, connection, Now);
        task.DeliverReliable(first, null, Now);
        first.Invalidate(AgentTaskFailureCode.ConfirmationTimeout, Now.AddSeconds(60));
        task.FailReliable(AgentTaskFailureCode.ConfirmationTimeout, policy, Now.AddSeconds(60));
        var retry = Attempt(task, connection, Now.AddSeconds(62));
        task.DeliverReliable(retry, first, Now.AddSeconds(62));
        Assert.Equal(first.ReplacementAttemptId, retry.ID);
        Assert.Equal(1, task.AutomaticRetryCount);
        retry.Confirm(Now.AddSeconds(63));
        task.MarkProcessing(Now.AddSeconds(63));
        retry.RecordReportedFailure(AgentTaskFailureCode.AgentTransientFailure, policy.Decide(AgentTaskFailureCode.AgentTransientFailure, 1, Now.AddSeconds(64)));
        retry.Invalidate(AgentTaskFailureCode.AgentTransientFailure, Now.AddSeconds(64));
        task.FailReliable(AgentTaskFailureCode.AgentTransientFailure, policy, Now.AddSeconds(64));
        Assert.True(task.RetryExhausted);
        Assert.False(task.FailReliable(AgentTaskFailureCode.ConnectionResumed, policy, Now.AddSeconds(65)));
        Assert.Equal("agent_transient_failure", task.FailureCode);
        Assert.Equal(retry.ID, task.CurrentAttemptId);
        task.BeginExplicitRetry(Now.AddSeconds(65));
        var newCycle = Attempt(task, connection, Now.AddSeconds(65));
        task.DeliverReliable(newCycle, retry, Now.AddSeconds(65));
        Assert.Equal(1, task.RetryCycle);
        Assert.Equal(0, task.AutomaticRetryCount);
        Assert.Equal(0, retry.FailureDecision!.RetryCycle);
        Assert.True(retry.FailureDecision.RetryExhausted);
        Assert.Equal(Now.AddSeconds(63), retry.ConfirmedAtUtc);
    }

    private static AgentConnectionSession Connection() => AgentConnectionSession.CreateActivating(Guid.NewGuid(), Guid.NewGuid(), 1000,
        "first-binding", 1, "explicit_ack_v1", new string('A', 64), Now);
    private static AgentReplyTask TaskFor(AgentConnectionSession connection) => AgentReplyTask.Create(Guid.NewGuid(), connection.MeetingId,
        connection.ParticipantId, Guid.NewGuid(), 1, Guid.NewGuid(), MessageAuthorKind.Human, Guid.NewGuid(), 0, Guid.NewGuid(), Now);
    private static AgentTaskDeliveryAttempt Attempt(AgentReplyTask task, AgentConnectionSession connection, DateTimeOffset now) =>
        AgentTaskDeliveryAttempt.CreateReliable(task, connection, new(1, 1, 1, 1, 1, 1000, 100, false, false, 0, "agent-context-v1"), now, 60);
}
