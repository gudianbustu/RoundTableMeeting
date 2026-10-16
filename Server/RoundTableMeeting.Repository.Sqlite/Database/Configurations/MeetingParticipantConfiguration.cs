using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingParticipantConfiguration : IEntityTypeConfiguration<MeetingParticipant>
{
    public void Configure(EntityTypeBuilder<MeetingParticipant> builder)
    {
        builder.ToTable("MeetingParticipants", table => table.HasCheckConstraint("CK_MeetingParticipants_TypeUser", "(ParticipantType = 'Human' AND UserId IS NOT NULL) OR (ParticipantType = 'Agent' AND UserId IS NULL)"));
        builder.HasKey(x => x.ID);
        builder.Property(x => x.ParticipantType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(64).IsRequired();
        builder.Property(x => x.NormalizedDisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.JoinedAtUtc).IsRequired();
        builder.Property(x => x.MembershipState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(MeetingMembershipState.Joined).IsRequired();
        builder.Property(x => x.MembershipRevision).HasDefaultValue(1L).IsRequired();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MeetingId, x.UserId }).IsUnique().HasFilter("UserId IS NOT NULL");
        builder.HasIndex(x => new { x.MeetingId, x.NormalizedDisplayName }).IsUnique();
    }
}
