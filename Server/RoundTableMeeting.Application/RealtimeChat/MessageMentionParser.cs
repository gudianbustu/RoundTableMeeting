using System.Globalization;
using System.Text;
using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Application.RealtimeChat;
public sealed record MentionParticipant(Guid Id, string DisplayName, string NormalizedDisplayName);
public sealed record ParsedMention(Guid ParticipantId, string DisplayName, int StartOffset, int Length, bool IsGroup = false);
public interface IMessageMentionParser
{
    IReadOnlyList<ParsedMention> Parse(string content, IReadOnlyList<MentionParticipant> participants, IReadOnlyList<RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MentionCandidateDto>? candidates = null);
}
public sealed class MessageMentionParser : IMessageMentionParser
{
    public IReadOnlyList<ParsedMention> Parse(string content, IReadOnlyList<MentionParticipant> participants, IReadOnlyList<RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MentionCandidateDto>? candidates = null)
    {
        var parsed = ParseText(content, participants);
        if (candidates is null || candidates.Count == 0) return parsed;
        var byId = participants.ToDictionary(participant => participant.Id);
        var validated = new List<ParsedMention>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.StartOffset))
        {
            if (!byId.TryGetValue(candidate.ParticipantId, out var participant)
                || candidate.StartOffset < 0
                || candidate.Length <= 1
                || candidate.StartOffset + candidate.Length > content.Length
                || validated.Any(item => item.StartOffset < candidate.StartOffset + candidate.Length && candidate.StartOffset < item.StartOffset + item.Length)) continue;
            var expected = "@" + participant.DisplayName;
            if (!string.Equals(content.Substring(candidate.StartOffset, candidate.Length).Normalize(NormalizationForm.FormC), expected, StringComparison.Ordinal)) continue;
            validated.Add(new(participant.Id, participant.DisplayName, candidate.StartOffset, candidate.Length));
        }
        return validated.Count == 0 ? parsed : validated.Concat(parsed)
            .GroupBy(item => item.StartOffset)
            .Select(group => group.OrderByDescending(item => item.Length).First())
            .OrderBy(item => item.StartOffset).ToArray();
    }

    private static IReadOnlyList<ParsedMention> ParseText(string content, IReadOnlyList<MentionParticipant> participants)
    {
        var result = new List<ParsedMention>();
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '@') continue;
            ParsedMention? best = null;
            foreach (var p in participants.OrderByDescending(p => p.NormalizedDisplayName.Length))
            {
                var length = p.DisplayName.Length;
                if (i + 1 + length > content.Length) continue;
                var candidate = content.Substring(i + 1, length).Normalize(NormalizationForm.FormC).ToUpperInvariant();
                if (!string.Equals(candidate, p.NormalizedDisplayName, StringComparison.Ordinal)) continue;
                var end = i + 1 + length;
                if (end < content.Length && !char.IsWhiteSpace(content[end]) && !char.IsPunctuation(content[end]) && !char.IsSymbol(content[end])) continue;
                best = new(p.Id, p.DisplayName, i, length + 1); break;
            }
            if (best is not null && !result.Any(m => m.StartOffset < best.StartOffset + best.Length && best.StartOffset < m.StartOffset + m.Length)) result.Add(best);
        }
        return result;
    }
}
