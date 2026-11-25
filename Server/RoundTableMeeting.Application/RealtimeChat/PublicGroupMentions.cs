using System.Text.RegularExpressions;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.RealtimeChat;

internal static partial class PublicGroupMentions
{
    // Offsets returned by .NET regexes are UTF-16 offsets, just like individual mentions.
    [GeneratedRegex(@"@(所有人类|所有Agent|所有人)(?![^\s\p{P}\p{S}])", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    public static IReadOnlyList<ParsedMention> Expand(string content, IReadOnlyList<ParsedMention> personal,
        IReadOnlyList<MeetingParticipant> members, out bool hasGroup)
    {
        var tokens = Tokens().Matches(content);
        hasGroup = false;
        if (tokens.Count == 0) return personal;

        var result = new List<ParsedMention>();
        var i = 0;
        foreach (Match token in tokens)
        {
            while (i < personal.Count && personal[i].StartOffset + personal[i].Length <= token.Index)
                result.Add(personal[i++]);
            // A nickname containing a group spelling keeps its original, longer match.
            if (i < personal.Count && personal[i].StartOffset <= token.Index && personal[i].Length > token.Length &&
                personal[i].StartOffset + personal[i].Length >= token.Index + token.Length) continue;
            hasGroup = true;
            while (i < personal.Count && personal[i].StartOffset < token.Index + token.Length) i++;
            foreach (var member in members)
            {
                if (member.MembershipState != MeetingMembershipState.Joined) continue;
                if (token.Value == "@所有人类" && member.ParticipantType != ParticipantType.Human ||
                    token.Value == "@所有Agent" && member.ParticipantType != ParticipantType.Agent) continue;
                result.Add(new(member.ID, member.DisplayName, token.Index, token.Length, IsGroup: true));
            }
        }
        while (i < personal.Count) result.Add(personal[i++]);
        return result;
    }
}
