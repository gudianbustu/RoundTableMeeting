using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Database.Configurations;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class AvatarRepository(RoundTableMeetingDbContext db) : IAvatarRepository, IScopedDependency<IAvatarRepository>
{
    private static AvatarEntry Entry(string id, string name) => new(id, name, $"/api/avatars/{id}/image", "uploaded");

    public async Task<AvatarEntry> SaveUploadAsync(string name, byte[] png, Guid ownerId, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = Entry(id, name);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(new AvatarAsset { Id = id, Name = name, Png = png, OwnerId = ownerId });
        await db.SaveChangesAsync(ct);
        await SetAccountAsync(ownerId, entry.ImageUrl, ct);
        await transaction.CommitAsync(ct);
        return entry;
    }

    public Task<AvatarImage?> ReadImageAsync(string id, Guid viewer, CancellationToken ct) =>
        db.Set<AvatarAsset>().Where(a => a.Id == id && (a.OwnerId == null || a.OwnerId == viewer ||
            db.Set<MeetingParticipant>().Any(owner => owner.UserId == a.OwnerId &&
                db.Set<MeetingParticipant>().Any(reader => reader.MeetingId == owner.MeetingId && reader.UserId == viewer))))
            .Select(a => new AvatarImage(a.Png, a.OwnerId)).SingleOrDefaultAsync(ct);

    public Task SetAccountAsync(Guid userId, string url, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AvatarSelections (OwnerId, IsAccount, Url) VALUES ({userId}, 1, {url})
            ON CONFLICT(OwnerId, IsAccount) DO UPDATE SET Url=excluded.Url
            """, ct);

    public Task<string?> GetAccountAsync(Guid userId, CancellationToken ct) =>
        db.Set<AvatarSelection>().Where(x => x.IsAccount && x.OwnerId == userId).Select(x => x.Url).SingleOrDefaultAsync(ct);

    private IQueryable<AvatarColorAssignment> Participants(Guid meetingId) =>
        from p in db.Set<MeetingParticipant>()
        join a in db.Set<AvatarSelection>() on new { OwnerId = p.UserId ?? p.ID, IsAccount = p.UserId.HasValue } equals new { a.OwnerId, a.IsAccount }
        join color in db.Set<AvatarColorAssignment>() on new { ParticipantId = p.ID, a.Url } equals new { color.ParticipantId, color.Url }
        where p.MeetingId == meetingId
        select color;

    public async Task<IReadOnlyDictionary<Guid, ParticipantAvatar>> GetMeetingAsync(Guid meetingId, CancellationToken ct) =>
        await Participants(meetingId).AsNoTracking().ToDictionaryAsync(x => x.ParticipantId, x => new ParticipantAvatar(x.Url, x.Slot), ct);

    public Task<ParticipantAvatar?> GetParticipantAsync(Guid meetingId, Guid participantId, CancellationToken ct) =>
        Participants(meetingId).Where(x => x.ParticipantId == participantId)
            .Select(x => new ParticipantAvatar(x.Url, x.Slot)).SingleOrDefaultAsync(ct);

    public async Task CreateInvitationAsync(Guid meetingId, string normalizedName, string digest, string url, CancellationToken ct)
    {
        db.Add(new AvatarInvitation { MeetingId = meetingId, NormalizedName = normalizedName, Digest = digest, Url = url });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> BindInvitationAsync(Guid meetingId, string normalizedName, string digest, Guid participantId, bool creating, string? previousInvitationBinding, CancellationToken ct)
    {
        var invite = await db.Set<AvatarInvitation>().AsTracking().SingleOrDefaultAsync(x => x.Digest == digest, ct);
        if (invite is null) return true;
        if (invite.MeetingId != meetingId) return false;
        if (invite.BoundParticipantId.HasValue) return invite.BoundParticipantId == participantId;
        if (creating)
        {
            if (invite.NormalizedName != normalizedName) return false;
            db.Add(new AvatarSelection { OwnerId = participantId, IsAccount = false, Url = invite.Url });
        }
        else if (previousInvitationBinding is null || !await db.Set<AgentConnectionSession>().AnyAsync(
            c => c.MeetingId == meetingId && c.ParticipantId == participantId && c.McpTransportBinding == previousInvitationBinding, ct)) return false;
        invite.BoundParticipantId = participantId;
        return true;
    }
}
