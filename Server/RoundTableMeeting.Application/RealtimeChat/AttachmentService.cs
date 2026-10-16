using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
namespace RoundTableMeeting.Application.RealtimeChat;

public sealed class AttachmentService(ICurrentUserContext currentUser, IMeetingRepository meetings,
    IMeetingParticipantRepository participants, IMeetingAttachmentRepository attachments,
    IMessageVisibilityRepository visibility, IUnitOfWork unitOfWork, IMeetingTransactionRunner runner,
    IMeetingRealtimeGate gate, IMeetingFileTransferGate transfers, IAttachmentStore store,
    IMeetingLifecycleService lifecycle, VisibleMessageMapper mapper, TimeProvider time, MeetingAgentContext? agentContext = null)
    : IAttachmentService, IScopedDependency<IAttachmentService>
{
    private async Task<MeetingParticipant> Authorize(Guid meetingId, bool writing, CancellationToken ct)
    {
        var userId = currentUser.UserId.GetValueOrDefault(); if (userId == Guid.Empty && agentContext?.ParticipantFor(meetingId) is null) throw new AttachmentOperationException("unauthenticated", 401);
        if (await meetings.FindActiveByIdAsync(meetingId, ct) is null)
        {
            var state = await lifecycle.GetMemberStateAsync(meetingId, userId, ct);
            throw new AttachmentOperationException(state is "deleting" or "deleted" ? "meeting_" + state : "meeting_not_found", state is "deleting" or "deleted" ? 410 : 404);
        }
        var participant = (agentContext?.ParticipantFor(meetingId) is Guid agentId ? (await participants.ListAsync(meetingId, ct)).SingleOrDefault(p => p.ID == agentId && p.ParticipantType == ParticipantType.Agent) : await participants.FindByUserAsync(meetingId, userId, ct)) ?? throw new AttachmentOperationException("meeting_not_found", 404);
        if (writing && participant.MembershipState == MeetingMembershipState.Left) throw new AttachmentOperationException("membership_left", 409);
        return participant;
    }
    public async Task<AttachmentDto> UploadAsync(Guid meetingId, Guid uploadRequestId, string name, Stream content, CancellationToken ct = default)
    {
        if (uploadRequestId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(char.IsControl))
            throw new AttachmentOperationException("validation_failed", 400);
        // Both path separators are stripped regardless of the server operating system.
        name = name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(name)) throw new AttachmentOperationException("validation_failed", 400);
        IMeetingFileTransferLease transfer;
        using (var lease = await (agentContext?.AcquireSharedAsync(gate, meetingId, ct) ?? gate.AcquireSharedAsync(meetingId, ct)))
        {
            await Authorize(meetingId, true, ct);
            if (lease is null) throw new AttachmentOperationException("meeting_deleting", 410);
            transfer = transfers.Acquire(meetingId) ?? throw new AttachmentOperationException("meeting_deleting", 410);
        }
        using (transfer)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.CancellationToken))
        {
            StagedAttachmentFile? file = null; var retained = false;
            try
            {
                file = await store.WriteAsync(meetingId, content, linked.Token);
                using var lease = await (agentContext?.AcquireSharedAsync(gate, meetingId, linked.Token) ?? gate.AcquireSharedAsync(meetingId, linked.Token));
                if (lease is null) throw new AttachmentOperationException("meeting_deleting", 410);
                var item = await runner.RunAsync(async token =>
                {
                    var p = await Authorize(meetingId, true, token);
                    var existing = await attachments.FindUploadAsync(meetingId, p.ID, uploadRequestId, token);
                    if (existing is not null)
                    {
                        if (existing.OriginalName != name || existing.SizeBytes != file.SizeBytes || existing.ContentHash != file.ContentHash)
                            throw new AttachmentOperationException("upload_mismatch", 409);
                        return existing;
                    }
                    var created = MeetingAttachment.Stage(meetingId, p.ID, uploadRequestId, name, file.SizeBytes,
                        file.MediaType, file.StorageKey, file.ContentHash, time.GetUtcNow());
                    unitOfWork.RegisterAdd(created); return created;
                }, linked.Token);
                retained = item.StorageKey == file.StorageKey;
                return new(item.ID, item.OriginalName, item.SizeBytes, item.MediaType, item.State);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && transfer.CancellationToken.IsCancellationRequested)
            { throw new AttachmentOperationException("meeting_deleting", 410); }
            catch (IOException) { throw new AttachmentOperationException("attachment_storage_unavailable", 503); }
            catch (UnauthorizedAccessException) { throw new AttachmentOperationException("attachment_storage_unavailable", 503); }
            finally
            {
                if (file is not null && !retained)
                {
                    // Failed cleanup remains eligible for orphan recovery; preserve the original result.
                    try { store.Delete(meetingId, file.StorageKey); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
    public async Task<AttachmentPageDto> ListAsync(Guid meetingId, MessagePageRequest request, CancellationToken ct = default)
    {
        using var lease = await (agentContext?.AcquireSharedAsync(gate, meetingId, ct) ?? gate.AcquireSharedAsync(meetingId, ct));
        var p = await Authorize(meetingId, false, ct);
        if (lease is null) throw new AttachmentOperationException("meeting_deleting", 410);
        var limit = request.Limit ?? 100;
        if (limit is < 1 or > 200 || request.BeforeSequence < 0 || request.AfterSequence.HasValue || request.ThroughSequence.HasValue
            || request.Scope is not ("all" or "public" or "private") || request.Scope == "public" && request.PeerParticipantId.HasValue
            || request.PeerParticipantId == p.ID || request.PeerParticipantId == Guid.Empty)
            throw new AttachmentOperationException("validation_failed", 400);
        if (request.PeerParticipantId is Guid peer && !(await participants.ListAsync(meetingId, ct)).Any(x => x.ID == peer))
            throw new AttachmentOperationException("validation_failed", 400);
        var rows = await visibility.PageAsync(meetingId, p.ID, request.Scope, request.PeerParticipantId,
            request.BeforeSequence, null, p.LeftThroughSequence, limit + 1, ct, attachmentsOnly: true);
        var chosen = rows.TakeLast(limit).ToArray(); var result = new List<AttachmentListItemDto>();
        foreach (var row in chosen.Reverse())
        {
            var dto = await mapper.MapAsync(row, ct);
            result.AddRange(dto.Attachments.Select(a => new AttachmentListItemDto(a, dto.MessageId, dto.Sequence, dto.Author, dto.CreatedAt)));
        }
        return new(result, rows.Count > limit ? chosen[0].Sequence : null);
    }
    public async Task<AttachmentDownload> OpenAsync(Guid meetingId, Guid attachmentId, CancellationToken ct = default)
    {
        using var lease = await (agentContext?.AcquireSharedAsync(gate, meetingId, ct) ?? gate.AcquireSharedAsync(meetingId, ct));
        var p = await Authorize(meetingId, false, ct);
        if (lease is null) throw new AttachmentOperationException("meeting_deleting", 410);
        var file = await attachments.FindAsync(meetingId, attachmentId, ct);
        if (file?.MessageId is not Guid messageId || await visibility.FindAsync(meetingId, p.ID, messageId, p.LeftThroughSequence, ct) is null)
            throw new AttachmentOperationException("attachment_not_found", 404);
        var transfer = transfers.Acquire(meetingId) ?? throw new AttachmentOperationException("meeting_deleting", 410);
        try { return new(new LeaseReadStream(store.Open(meetingId, file.StorageKey), transfer), file.OriginalName, file.MediaType, file.SizeBytes); }
        catch (IOException) { transfer.Dispose(); throw new AttachmentOperationException("attachment_storage_unavailable", 503); }
        catch (UnauthorizedAccessException) { transfer.Dispose(); throw new AttachmentOperationException("attachment_storage_unavailable", 503); }
        catch { transfer.Dispose(); throw; }
    }
    private sealed class LeaseReadStream : Stream
    {
        private readonly Stream inner;
        private readonly IMeetingFileTransferLease lease;
        private readonly CancellationToken token;
        private readonly CancellationTokenRegistration registration;
        public LeaseReadStream(Stream inner, IMeetingFileTransferLease lease)
        {
            this.inner = inner; this.lease = lease; token = lease.CancellationToken;
            registration = token.Register(() => inner.Dispose());
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            linked.Token.ThrowIfCancellationRequested(); return await inner.ReadAsync(buffer, linked.Token);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { registration.Dispose(); inner.Dispose(); lease.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
