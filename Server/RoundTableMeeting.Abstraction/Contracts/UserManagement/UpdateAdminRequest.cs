using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RoundTableMeeting.Abstraction.Contracts.UserManagement;

public sealed class UpdateAdminRequest
{
    [Required, JsonRequired]
    public bool IsAdmin { get; init; }
}
