using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

public sealed class LoginRequest
{
    [Required]
    [StringLength(128)]
    public string? Username { get; init; }

    [Required]
    [StringLength(256)]
    public string? Password { get; init; }
}
