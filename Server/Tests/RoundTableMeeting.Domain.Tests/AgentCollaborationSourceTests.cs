using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class AgentCollaborationSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T00:00:00Z");

    [Fact]
    public void HumanTargetsShareMessageRootButHaveIndependentTaskIdentity()
    {
        var meeting = Guid.NewGuid();
        var message = Guid.NewGuid();
        var first = Root(meeting, message);
        var second = Root(meeting, message);
        Assert.Equal(message, first.ChainId);
        Assert.Equal(first.ChainId, second.ChainId);
        Assert.Null(first.ParentTaskId);
        Assert.NotEqual(first.ID, second.ID);
        Assert.NotEqual(first.AgentParticipantId, second.AgentParticipantId);
    }

    [Theory]
    [InlineData(MessageAuthorKind.Agent)]
    [InlineData(MessageAuthorKind.System)]
    public void NonHumanMessagesCannotPretendToBeRoots(MessageAuthorKind kind)
        => Assert.Throws<ArgumentException>(() => AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), kind,
            kind == MessageAuthorKind.System ? null : Guid.NewGuid(), 0, Guid.NewGuid(), Now));

    [Fact]
    public void DerivedTasksInheritRootAcrossBranchesAndFortyGenerations()
    {
        var parent = Root(Guid.NewGuid(), Guid.NewGuid());
        var rootId = parent.ChainId;
        for (var index = 0; index < 40; index++)
        {
            var reply = Reply(parent);
            var child = Derive(parent, reply, Guid.NewGuid());
            var branch = Derive(parent, reply, parent.AgentParticipantId);
            Assert.Equal(rootId, child.ChainId);
            Assert.Equal(parent.ID, child.ParentTaskId);
            Assert.Equal(parent.ID, branch.ParentTaskId);
            Assert.NotEqual(child.ID, branch.ID);
            parent = child;
        }
        parent.MarkFailed("failure", Now.AddMinutes(1));
        parent.Cancel(Now.AddMinutes(2));
        Assert.Equal(rootId, parent.ChainId);
        Assert.NotNull(parent.ParentTaskId);
    }

    [Fact]
    public void DerivedTaskRejectsUncommittedWrongAuthorCrossMeetingAndBackwardReplies()
    {
        var parent = Root(Guid.NewGuid(), Guid.NewGuid());
        var uncommitted = Message(parent, parent.MeetingId, parent.AgentParticipantId, 2);
        Assert.Throws<ArgumentException>(() => Derive(parent, uncommitted, Guid.NewGuid()));
        Reply(parent);
        Assert.Throws<ArgumentException>(() => Derive(parent,
            Message(parent, Guid.NewGuid(), parent.AgentParticipantId, 2), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Derive(parent,
            Message(parent, parent.MeetingId, Guid.NewGuid(), 2), Guid.NewGuid()));

        var backwardsParent = Root(Guid.NewGuid(), Guid.NewGuid());
        var backwardsReply = Message(backwardsParent, backwardsParent.MeetingId, backwardsParent.AgentParticipantId, 1);
        Complete(backwardsParent, backwardsReply);
        Assert.Throws<ArgumentException>(() => Derive(backwardsParent, backwardsReply, Guid.NewGuid()));
    }

    [Fact]
    public void DerivedTaskRejectsMentionForDifferentMessageOrTarget()
    {
        var parent = Root(Guid.NewGuid(), Guid.NewGuid());
        var reply = Reply(parent);
        var target = Guid.NewGuid();
        var wrongMessage = MessageMention.Create(reply.MeetingId, Guid.NewGuid(), target, "B", 0, 2);
        Assert.Throws<ArgumentException>(() => AgentReplyTask.CreateDerived(Guid.NewGuid(), target, reply,
            wrongMessage, parent, 0, Guid.NewGuid(), Now));
        var wrongTarget = MessageMention.Create(reply.MeetingId, reply.ID, Guid.NewGuid(), "B", 0, 2);
        Assert.Throws<ArgumentException>(() => AgentReplyTask.CreateDerived(Guid.NewGuid(), target, reply,
            wrongTarget, parent, 0, Guid.NewGuid(), Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9007199254740991)]
    public void BoundaryAcceptsBothInclusiveLimits(long sequence)
    {
        var meeting = Guid.NewGuid();
        var boundary = AgentCollaborationBoundary.Create(meeting, sequence);
        Assert.Equal(meeting, boundary.MeetingId);
        Assert.NotEqual(Guid.Empty, boundary.ID);
        Assert.Equal(sequence, boundary.AgentTriggerStartSequence);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9007199254740992)]
    public void BoundaryRejectsOutOfRangeSequence(long sequence)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AgentCollaborationBoundary.Create(Guid.NewGuid(), sequence));

    [Fact]
    public void BoundaryRequiresMeeting()
        => Assert.Throws<ArgumentException>(() => AgentCollaborationBoundary.Create(Guid.Empty));

    private static AgentReplyTask Root(Guid meeting, Guid message)
        => AgentReplyTask.Create(Guid.NewGuid(), meeting, Guid.NewGuid(), message, 1,
            Guid.NewGuid(), MessageAuthorKind.Human, Guid.NewGuid(), 0, Guid.NewGuid(), Now);

    private static MeetingMessage Message(AgentReplyTask parent, Guid meeting, Guid author, long sequence)
        => MeetingMessage.CreateParticipant(meeting, sequence, author, MessageAuthorKind.Agent,
            "A", "@B", Guid.NewGuid(), Now);

    private static MeetingMessage Reply(AgentReplyTask parent)
    {
        var reply = Message(parent, parent.MeetingId, parent.AgentParticipantId, parent.TriggerSequence + 1);
        Complete(parent, reply);
        return reply;
    }

    private static void Complete(AgentReplyTask parent, MeetingMessage reply)
    {
        parent.MarkDelivered(Now);
        parent.MarkProcessing(Now);
        parent.MarkReplied(reply.ID, Now);
    }

    private static AgentReplyTask Derive(AgentReplyTask parent, MeetingMessage reply, Guid target)
        => AgentReplyTask.CreateDerived(Guid.NewGuid(), target, reply,
            MessageMention.Create(reply.MeetingId, reply.ID, target, "B", 0, 2),
            parent, 0, Guid.NewGuid(), Now);
}
