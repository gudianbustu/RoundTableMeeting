using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAccountSettingsService
{
    Task<AccountSettingsResult> UpdateMyNicknameAsync(UpdateNicknameRequest request, CancellationToken cancellationToken = default);
    Task<AccountSettingsResult> ChangeMyPasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
