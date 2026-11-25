using System.Text;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingMessage : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public long Sequence { get; private set; }
    public Guid? AuthorParticipantId { get; private set; }
    public MessageAuthorKind AuthorKind { get; private set; }
    public string AuthorDisplayName { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public Guid ClientSubmissionId { get; private set; }
    public string Visibility { get; private set; } = "public";
    public Guid? RecipientParticipantId { get; private set; }
    public Guid? ReplyToMessageId { get; private set; }
    public string PayloadFingerprint { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public MeetingMessage() { }

    private MeetingMessage(Guid meetingId, long sequence, Guid? authorParticipantId, MessageAuthorKind authorKind,
        string authorDisplayName, string content, Guid clientSubmissionId, DateTimeOffset createdAtUtc)
    {
        if (meetingId == Guid.Empty) throw new ArgumentException("Meeting is required.", nameof(meetingId));
        if (sequence is < 1 or > 9_007_199_254_740_991) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("Submission is required.", nameof(clientSubmissionId));
        if (authorKind is MessageAuthorKind.Human or MessageAuthorKind.Agent && (!authorParticipantId.HasValue || authorParticipantId == Guid.Empty)) throw new ArgumentException("Participant author is required.", nameof(authorParticipantId));
        if (authorKind == MessageAuthorKind.System && authorParticipantId.HasValue) throw new ArgumentException("System messages cannot have a participant author.", nameof(authorParticipantId));
        MeetingId = meetingId; Sequence = sequence; AuthorParticipantId = authorParticipantId; AuthorKind = authorKind;
        AuthorDisplayName = NormalizeDisplayName(authorDisplayName);
        Content = NormalizeContent(content);
        ClientSubmissionId = clientSubmissionId; CreatedAtUtc = createdAtUtc;
    }

    public static MeetingMessage CreateParticipant(Guid meetingId, long sequence, Guid participantId, MessageAuthorKind authorKind, string authorDisplayName, string content, Guid clientSubmissionId, DateTimeOffset createdAtUtc)
        => new(meetingId, sequence, participantId, authorKind is MessageAuthorKind.Human or MessageAuthorKind.Agent ? authorKind : throw new ArgumentException("Invalid participant author kind.", nameof(authorKind)), authorDisplayName, content, clientSubmissionId, createdAtUtc);

    public static MeetingMessage CreateSystem(Guid meetingId, long sequence, string authorDisplayName, string content, Guid clientSubmissionId, DateTimeOffset createdAtUtc)
        => new(meetingId, sequence, null, MessageAuthorKind.System, authorDisplayName, content, clientSubmissionId, createdAtUtc);

    public static string NormalizeContent(string? value)
    {
        return value ?? string.Empty;
    }

    public Guid? OriginalTextAttachmentId { get; private set; }

    public void SetOriginalText(Guid attachmentId) => OriginalTextAttachmentId = attachmentId;

    public void SetScope(string visibility, Guid? recipient, Guid? replyTo, string fingerprint)
    {
        if (visibility is not ("public" or "private") || visibility == "public" && recipient is not null
            || visibility == "private" && (AuthorKind == MessageAuthorKind.System || recipient is null || recipient == Guid.Empty || recipient == AuthorParticipantId))
            throw new ArgumentException("Invalid message scope.");
        Visibility = visibility; RecipientParticipantId = recipient; ReplyToMessageId = replyTo; PayloadFingerprint = fingerprint;
    }

    public static void ValidateContentLength(string content, int maxLength)
    {
        if (maxLength < 1 || content.Length > maxLength) throw new ArgumentException("Message content length is invalid.", nameof(content));
    }

    private static string NormalizeDisplayName(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length == 0 || normalized.Length > 128) throw new ArgumentException("Author display name is invalid.", nameof(value));
        return normalized;
    }
}
