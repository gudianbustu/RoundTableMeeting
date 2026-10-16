using System.Text;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using DomainAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.RealtimeChat;

public sealed class ResolvedMessageAppendCore(
    IMeetingRepository meetings,
	IMeetingParticipantRepository participants,
	IMessageRepository messages,
	IUnitOfWork unitOfWork,
	IMessageMentionParser parser,
	IMessageEventConsumerCatalog consumerCatalog,
	RealtimeChatOptions options,
	TimeProvider timeProvider) : IResolvedMessageAppendCore
{
	public Task<ResolvedMessageAppendResult> AppendAgentAsync(
		Guid meetingId,
		Guid participantId,
		string authorDisplayName,
		string content,
		Guid clientSubmissionId,
		CancellationToken cancellationToken = default)
	    => AppendAsync(meetingId, new(participantId, authorDisplayName, MessageAuthorKind.Agent),
            content, clientSubmissionId, cancellationToken: cancellationToken);

    public async Task<ResolvedMessageAppendResult> AppendAsync(Guid meetingId, ResolvedMessageAuthor author,
        string content, Guid clientSubmissionId, IReadOnlyList<MentionCandidateDto>? mentionCandidates = null,
        CancellationToken cancellationToken = default)
    {
		if (meetingId == Guid.Empty || clientSubmissionId == Guid.Empty
            || !Enum.IsDefined(author.Kind)
            || (author.Kind == MessageAuthorKind.System ? author.ParticipantId is not null : author.ParticipantId is null || author.ParticipantId == Guid.Empty))
			throw new ArgumentException("Message identity is invalid.");

        if (await meetings.FindActiveByIdAsync(meetingId, cancellationToken) is null)
            throw new RoundTableMeeting.Domain.Exceptions.MeetingMessageAccessException("meeting_not_found");
        var roster = await participants.ListAsync(meetingId, cancellationToken);
        if (author.Kind != MessageAuthorKind.System)
        {
            var participant = roster.SingleOrDefault(p => p.ID == author.ParticipantId);
            if (participant is null) throw new RoundTableMeeting.Domain.Exceptions.MeetingMessageAccessException("meeting_not_found");
            if (participant.MembershipState == RoundTableMeeting.Domain.Enums.MeetingMembershipState.Left)
                throw new RoundTableMeeting.Domain.Exceptions.MeetingMessageAccessException("membership_left");
        }

		var normalizedContent = MeetingMessage.NormalizeContent(content);
		MeetingMessage.ValidateContentLength(normalizedContent, options.MaxContentLength);
		var sequence = await messages.GetNextSequenceAsync(meetingId, cancellationToken);
		var message = author.Kind == MessageAuthorKind.System
            ? MeetingMessage.CreateSystem(meetingId, sequence, author.DisplayName, normalizedContent, clientSubmissionId, timeProvider.GetUtcNow())
            : MeetingMessage.CreateParticipant(
			meetingId,
			sequence,
			author.ParticipantId!.Value,
			author.Kind == MessageAuthorKind.Agent ? DomainAuthorKind.Agent : DomainAuthorKind.Human,
			author.DisplayName,
			normalizedContent,
			clientSubmissionId,
			timeProvider.GetUtcNow());
		unitOfWork.RegisterAdd(message);

        if (author.Kind != MessageAuthorKind.System)
        {
		var people = roster.Where(participant => participant.MembershipState == RoundTableMeeting.Domain.Enums.MeetingMembershipState.Joined)
			.Select(participant => new MentionParticipant(participant.ID, participant.DisplayName, participant.NormalizedDisplayName))
			.ToArray();
		foreach (var mention in parser.Parse(normalizedContent, people, mentionCandidates))
		{
			unitOfWork.RegisterAdd(MessageMention.Create(
				meetingId,
				message.ID,
				mention.ParticipantId,
				mention.DisplayName,
				mention.StartOffset,
				mention.Length));
		}
        }

		var appendedEvent = MessageAppendedEvent.Create(meetingId, message.ID, sequence, message.CreatedAtUtc);
		unitOfWork.RegisterAdd(appendedEvent);
		foreach (var consumerName in consumerCatalog.ConsumerNames)
			unitOfWork.RegisterAdd(MessageEventDelivery.Create(appendedEvent.ID, consumerName, message.CreatedAtUtc));
		return new(
			message.ID,
			message.MeetingId,
			message.Sequence,
			message.AuthorParticipantId,
			message.AuthorDisplayName,
			message.Content,
			message.CreatedAtUtc);
	}
}
