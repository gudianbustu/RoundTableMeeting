using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Application.RealtimeChat;

namespace RoundTableMeeting.Application.Tests;

public sealed class MessageMentionParserTests
{
    private readonly MessageMentionParser parser = new();

    [Fact]
    public void ParseUsesLongestNfcCaseInsensitiveMatchAndPreservesOccurrences()
    {
        var analyst = new MentionParticipant(Guid.NewGuid(), "分析", "分析");
        var reviewer = new MentionParticipant(Guid.NewGuid(), "分析师", "分析师");
        var result = parser.Parse("@分析师，@分析师", [analyst, reviewer]);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal(reviewer.Id, item.ParticipantId));
        Assert.Equal([0, 5], result.Select(item => item.StartOffset));
    }

    [Fact]
    public void ValidCandidateIsAcceptedAndInvalidCandidateFallsBackToTextParsing()
    {
        var participant = new MentionParticipant(Guid.NewGuid(), "AI Reviewer", "AI REVIEWER");
        var valid = parser.Parse("@AI Reviewer", [participant], [new MentionCandidateDto(participant.Id, 0, 12)]);
        var invalid = parser.Parse("@AI Reviewer", [participant], [new MentionCandidateDto(Guid.NewGuid(), 0, 12)]);
        Assert.Single(valid);
        Assert.Single(invalid);
        Assert.Equal(participant.Id, valid[0].ParticipantId);
        Assert.Equal(participant.Id, invalid[0].ParticipantId);
    }
}
