using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Application.AccountAccess;

/// <summary>
/// 表示密码哈希校验结果
/// </summary>
public enum AccountPasswordVerificationResult
{
    Invalid,
    Success
}

/// <summary>
/// 校验账号密码，并为未知账号保持一致的执行路径
/// </summary>
public sealed class PasswordVerificationService
{
    /// <summary>
    /// 用于真实和伪密码校验的哈希器
    /// </summary>
    private readonly IPasswordHasher<UserAccount> passwordHasher;
    /// <summary>
    /// 用户名不存在时使用的伪哈希
    /// </summary>
    private readonly string dummyHash;

    /// <summary>
    /// 初始化密码校验器并准备伪哈希
    /// </summary>
    public PasswordVerificationService(IPasswordHasher<UserAccount> passwordHasher)
    {
        this.passwordHasher = passwordHasher;
        var dummy = new UserAccount();
        dummyHash = passwordHasher.HashPassword(dummy, "dummy-password-for-timing");
    }

    /// <summary>
    /// 校验密码，账号不存在时使用伪哈希
    /// </summary>
    public AccountPasswordVerificationResult Verify(UserAccount? account, string password)
    {
        if (account is null)
        {
            var dummy = new UserAccount();
            passwordHasher.VerifyHashedPassword(dummy, dummyHash, password);
            return AccountPasswordVerificationResult.Invalid;
        }

        return passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password) is Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success or Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded
            ? AccountPasswordVerificationResult.Success
            : AccountPasswordVerificationResult.Invalid;
    }
}
