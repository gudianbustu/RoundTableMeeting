using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

internal sealed class AvatarAsset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public byte[] Png { get; set; } = [];
}

internal sealed class AvatarSelection
{
    public Guid OwnerId { get; set; }
    public bool IsAccount { get; set; }
    public string Url { get; set; } = "";
}

internal sealed class AvatarColorAssignment
{
    public Guid ParticipantId { get; set; }
    public Guid MeetingId { get; set; }
    public string Url { get; set; } = "";
    public int Slot { get; set; }
}

internal sealed class AvatarInvitation
{
    public string Digest { get; set; } = "";
    public Guid MeetingId { get; set; }
    public string NormalizedName { get; set; } = "";
    public string Url { get; set; } = "";
    public Guid? BoundParticipantId { get; set; }
}

internal sealed class AvatarConfiguration : IEntityTypeConfiguration<AvatarAsset>, IEntityTypeConfiguration<AvatarSelection>, IEntityTypeConfiguration<AvatarInvitation>, IEntityTypeConfiguration<AvatarColorAssignment>
{
    public void Configure(EntityTypeBuilder<AvatarAsset> b)
    {
        b.ToTable("AvatarAssets").HasKey(x => x.Id);
        b.HasIndex(x => x.OwnerId);
    }

    public void Configure(EntityTypeBuilder<AvatarSelection> b)
    {
        b.ToTable("AvatarSelections").HasKey(x => new { x.OwnerId, x.IsAccount });
    }

    public void Configure(EntityTypeBuilder<AvatarColorAssignment> b)
    {
        b.ToTable("AvatarColorAssignments").HasKey(x => new { x.ParticipantId, x.Url });
        b.HasIndex(x => new { x.MeetingId, x.Url, x.Slot }).IsUnique();
        b.HasOne<Domain.Entities.MeetingParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<AvatarInvitation> b)
    {
        b.ToTable("AvatarInvitations").HasKey(x => x.Digest);
        b.HasOne<Domain.Entities.Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
    }
}
