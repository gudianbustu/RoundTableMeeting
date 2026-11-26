using RoundTableMeeting.Abstraction.Contracts.UserManagement;

namespace RoundTableMeeting.Abstraction.Services;

/// <summary>
/// 定义内部测试阶段的最小用户管理用例。
/// </summary>
public interface IUserManagementService
{
    Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<UserManagementResult> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<UserManagementResult> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default);
    Task<UserManagementResult> UpdateNicknameAsync(Guid userId, UpdateNicknameRequest request, CancellationToken cancellationToken = default);
    Task<UserManagementResult> UpdateAdminAsync(Guid userId, UpdateAdminRequest request, CancellationToken cancellationToken = default);
    Task<UserManagementResult> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken = default);
}
