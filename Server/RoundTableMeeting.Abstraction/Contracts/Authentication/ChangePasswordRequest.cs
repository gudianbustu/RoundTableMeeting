using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public sealed class ChangePasswordRequest
{
    [Required, StringLength(256)]
    public string? CurrentPassword { get; init; }
    [Required, StringLength(256)]
    public string? NewPassword { get; init; }
}
