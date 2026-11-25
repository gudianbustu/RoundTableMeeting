using System.Text;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed class Meeting : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? CreationInformationHash { get; private set; }
    public Guid CreatorUserId { get; private set; }
    public Guid MeetingCode { get; private set; }
    public MeetingLifecycleState LifecycleState { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Meeting() { }
    private Meeting(string name, string? description, Guid creatorUserId, Guid meetingCode, DateTimeOffset createdAtUtc)
    {
        Name = NormalizeName(name);
        Description = NormalizeDescription(description);
        CreatorUserId = creatorUserId == Guid.Empty ? throw new ArgumentException("Creator is required.", nameof(creatorUserId)) : creatorUserId;
        MeetingCode = meetingCode == Guid.Empty ? throw new ArgumentException("Meeting code is required.", nameof(meetingCode)) : meetingCode;
        CreatedAtUtc = createdAtUtc;
        LifecycleState = MeetingLifecycleState.Active;
    }
    public static Meeting Create(string name, string? description, Guid creatorUserId, DateTimeOffset createdAtUtc)
        => new(name, description, creatorUserId, Guid.NewGuid(), createdAtUtc);
    private static string InformationHash(string name, string? description)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { name, description });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public bool MatchesCreationInformation(string name, string? description) => CreationInformationHash is null
        ? Name == name && Description == description
        : CreationInformationHash == InformationHash(name, description);

    public void UpdateInformation(string name, string? description)
    {
        var normalizedName = NormalizeName(name);
        var normalizedDescription = NormalizeDescription(description);
        CreationInformationHash ??= InformationHash(Name, Description);
        Name = normalizedName;
        Description = normalizedDescription;
    }

    public void BeginDeleting()
    {
        if (LifecycleState != MeetingLifecycleState.Active) throw new InvalidOperationException("Meeting is not active.");
        LifecycleState = MeetingLifecycleState.Deleting;
    }
    public static string NormalizeName(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length is < 1 or > 128) throw new ArgumentException("Meeting name length is invalid.", nameof(value));
        return normalized;
    }
    public static string? NormalizeDescription(string? value)
    {
        if (value is null) return null;
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0) return null;
        if (normalized.Length > 2000) throw new ArgumentException("Meeting description length is invalid.", nameof(value));
        return normalized;
    }
}
