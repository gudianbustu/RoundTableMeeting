using System.Text;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentContextBuilder(IMessageRepository messages)
{
    public async Task<AgentContextDto> RebuildFrozenAsync(Guid meetingId, AgentContextWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (meetingId == Guid.Empty) throw new ArgumentException("Meeting is required.", nameof(meetingId));
        if (window.ContextFormatVersion != "agent-context-v1")
            throw new ArgumentException("The saved context format is unsupported.", nameof(window));
        if (window.SnapshotSequence == 0) return Empty(0, window.DeclaredCharacterLimit);

        var selected = await messages.ListAfterSequenceThroughAsync(meetingId,
            window.DeliveredFirstSequence!.Value - 1, window.DeliveredLastSequence!.Value, cancellationToken);
        ValidateCandidates(selected, meetingId, window.DeliveredFirstSequence.Value - 1,
            window.DeliveredLastSequence.Value, window.DeclaredCharacterLimit);
        var rebuilt = await BuildAsync(selected, window.SnapshotSequence, window.DeclaredCharacterLimit, cancellationToken);
        if (rebuilt.DeliveredFirstSequence != window.DeliveredFirstSequence
            || rebuilt.DeliveredLastSequence != window.DeliveredLastSequence
            || rebuilt.DeliveredCharacterCount != window.DeliveredCharacterCount
            || rebuilt.OverLimit != window.OverLimit)
            throw new ArgumentException("The saved context no longer matches its immutable messages.", nameof(window));
        return rebuilt with
        {
            CandidateFirstSequence = window.CandidateFirstSequence,
            CandidateLastSequence = window.CandidateLastSequence,
            Truncated = window.Truncated,
            TruncatedMessageCount = window.TruncatedMessageCount
        };
    }

    public async Task<AgentContextDto> BuildInitialAsync(
        Guid meetingId,
        long snapshotSequence,
        int characterLimit,
        CancellationToken cancellationToken = default)
    {
        var candidates = await messages.ListThroughSequenceAsync(meetingId, snapshotSequence, cancellationToken);
        ValidateCandidates(candidates, meetingId, 0, snapshotSequence, characterLimit);
		return await BuildAsync(candidates, snapshotSequence, characterLimit, cancellationToken);
	}

	public async Task<AgentContextDto> BuildTaskAsync(
		Guid meetingId,
		long afterSequence,
		long triggerSequence,
		int characterLimit,
		CancellationToken cancellationToken = default)
	{
		var candidates = await messages.ListAfterSequenceThroughAsync(meetingId, afterSequence, triggerSequence, cancellationToken);
        if (afterSequence >= triggerSequence) throw new ArgumentException("The task range is invalid.");
        ValidateCandidates(candidates, meetingId, afterSequence, triggerSequence, characterLimit);
		return await BuildAsync(candidates, triggerSequence, characterLimit, cancellationToken);
	}

	private async Task<AgentContextDto> BuildAsync(
		IReadOnlyList<MeetingMessage> candidates,
		long snapshotSequence,
		int characterLimit,
		CancellationToken cancellationToken)
	{
        if (candidates.Count == 0)
            return Empty(snapshotSequence, characterLimit);

        var blocks = new string[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var message = candidates[index];
            blocks[index] = FormatBlock(message);
        }

        var selectedStart = candidates.Count - 1;
        var deliveredCharacterCount = blocks[selectedStart].Length;
        var overLimit = deliveredCharacterCount > characterLimit;
        while (selectedStart > 0)
        {
            var previousLength = blocks[selectedStart - 1].Length;
            if (deliveredCharacterCount + previousLength > characterLimit) break;
            selectedStart--;
            deliveredCharacterCount += previousLength;
        }

        var selected = candidates.Skip(selectedStart).ToArray();
        var mapped = new List<AgentContextMessageDto>(selected.Length);
        foreach (var message in selected)
        {
            var mentions = await messages.ListMentionsAsync(message.MeetingId, message.ID, cancellationToken);
            mapped.Add(new(
                message.ID,
                message.MeetingId,
                message.Sequence,
                ToWireAuthorKind(message.AuthorKind),
                message.AuthorParticipantId,
                message.AuthorDisplayName,
                message.Content,
                mentions.Select(mention => new AgentContextMentionDto(
                    mention.TargetParticipantId, mention.TargetDisplayName, mention.StartOffset, mention.Length)).ToArray(),
                message.CreatedAtUtc));
        }

        return new(
            "agent-context-v1",
            snapshotSequence,
            candidates[0].Sequence,
            candidates[^1].Sequence,
            selected[0].Sequence,
            selected[^1].Sequence,
            characterLimit,
            deliveredCharacterCount,
            selectedStart > 0,
            overLimit,
            selectedStart,
            mapped);
    }

    private static void ValidateCandidates(IReadOnlyList<MeetingMessage> candidates, Guid meetingId,
        long afterSequence, long throughSequence, int characterLimit)
    {
        if (meetingId == Guid.Empty || afterSequence < 0 || throughSequence < afterSequence || characterLimit <= 0)
            throw new ArgumentException("The context range is invalid.");
        var previous = afterSequence;
        foreach (var message in candidates)
        {
            if (message.MeetingId != meetingId || message.Sequence != previous + 1
                || message.Sequence > throughSequence || string.IsNullOrWhiteSpace(message.AuthorDisplayName)
                || string.IsNullOrWhiteSpace(message.Content))
                throw new ArgumentException("The context contains invalid messages.");
            previous = message.Sequence;
        }
        if (previous != throughSequence) throw new ArgumentException("The context endpoint is missing.");
    }

    private static AgentContextDto Empty(long snapshotSequence, int characterLimit)
        => new("agent-context-v1", snapshotSequence, null, null, null, null, characterLimit, 0, false, false, 0, []);

    private static string FormatBlock(MeetingMessage message)
    {
        var builder = new StringBuilder();
        builder.Append("messageId=").Append(message.ID.ToString("D").ToLowerInvariant()).Append('\n');
        builder.Append("sequence=").Append(message.Sequence).Append('\n');
        builder.Append("authorKind=").Append(ToWireAuthorKind(message.AuthorKind)).Append('\n');
        builder.Append("authorDisplayName=").Append(Escape(message.AuthorDisplayName)).Append('\n');
        builder.Append("content=").Append(Escape(message.Content)).Append('\n');
        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string ToWireAuthorKind(MessageAuthorKind kind)
        => kind switch
        {
            MessageAuthorKind.Human => "human",
            MessageAuthorKind.Agent => "agent",
            MessageAuthorKind.System => "system",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
