using System.ComponentModel.DataAnnotations;
namespace RoundTableMeeting.Application.RealtimeChat;
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachment";
    [Range(1, long.MaxValue)] public long MaxFileBytes { get; set; } = 100_000_000;
    public string RootPath { get; set; } = "App_Data/attachments";
}
