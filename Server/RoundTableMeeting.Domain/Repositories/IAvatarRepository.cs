namespace RoundTableMeeting.Domain.Repositories;

public sealed record AvatarEntry(string Id, string Name, string ImageUrl, string Category);
public sealed record ParticipantAvatar(string Url, int ColorSlot);
public sealed record AvatarImage(byte[] Bytes, Guid? OwnerId);

public interface IAvatarRepository
{
    Task<IReadOnlyList<AvatarEntry>> ListUploadsAsync(CancellationToken ct);
    Task<AvatarEntry?> FindPublicUploadAsync(string id, CancellationToken ct);
    Task<AvatarEntry> SaveUploadAsync(string name, byte[] png, Guid? ownerId, CancellationToken ct);
    Task<AvatarImage?> ReadImageAsync(string id, Guid viewer, CancellationToken ct);
    Task SetAccountAsync(Guid userId, string url, CancellationToken ct);
    Task<string?> GetAccountAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, ParticipantAvatar>> GetMeetingAsync(Guid meetingId, CancellationToken ct);
    Task<ParticipantAvatar?> GetParticipantAsync(Guid meetingId, Guid participantId, CancellationToken ct);
    Task CreateInvitationAsync(Guid meetingId, string normalizedName, string digest, string url, CancellationToken ct);
    Task<bool> BindInvitationAsync(Guid meetingId, string normalizedName, string digest, Guid participantId, bool creating, string? previousInvitationBinding, CancellationToken ct);
}
