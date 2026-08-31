using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class MeetingDomainTests
{
    [Fact]
    public void MeetingCreatesActiveWithRandomGuidCodeAndNormalizedContent()
    {
        var meeting = Meeting.Create("  Design review  ", "  Context  ", Guid.NewGuid(), DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        Assert.Equal("Design review", meeting.Name);
        Assert.Equal("Context", meeting.Description);
        Assert.NotEqual(Guid.Empty, meeting.MeetingCode);
        Assert.Equal(MeetingLifecycleState.Active, meeting.LifecycleState);
    }

    [Fact]
    public void MeetingRejectsInvalidNamesAndCannotReturnFromDeleting()
    {
        Assert.Throws<ArgumentException>(() => Meeting.Create("  ", null, Guid.NewGuid(), DateTimeOffset.UtcNow));
        var meeting = Meeting.Create("Meeting", null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        meeting.BeginDeleting();
        Assert.Equal(MeetingLifecycleState.Deleting, meeting.LifecycleState);
        Assert.Throws<InvalidOperationException>(meeting.BeginDeleting);
    }

    [Fact]
    public void ParticipantNormalizesNameAndEnforcesHumanIdentity()
    {
        var participant = MeetingParticipant.CreateHuman(Guid.NewGuid(), Guid.NewGuid(), "  Café  ", DateTimeOffset.UtcNow);
        Assert.Equal("Café", participant.DisplayName);
        Assert.Equal("CAFÉ", participant.NormalizedDisplayName);
        Assert.Throws<ArgumentException>(() => MeetingParticipant.CreateHuman(Guid.NewGuid(), Guid.Empty, "Alice", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReceiptBecomesDeletedAndExpiresAfterThirtyDays()
    {
        var now = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var receipt = MeetingCreationReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        receipt.MarkDeleted(now);
        Assert.Equal(MeetingCreationReceiptState.Deleted, receipt.State);
        Assert.Null(receipt.MeetingId);
        Assert.Equal(now.AddDays(30), receipt.ExpiresAtUtc);
    }
}
