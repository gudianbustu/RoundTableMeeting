using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Application.AccountAccess;

public enum AccountPasswordVerificationResult
{
    Invalid,
    Success
}

public sealed class PasswordVerificationService
{
    private readonly IPasswordHasher<UserAccount> passwordHasher;
    private readonly string dummyHash;

    public PasswordVerificationService(IPasswordHasher<UserAccount> passwordHasher)
    {
        this.passwordHasher = passwordHasher;
        var dummy = new UserAccount();
        dummyHash = passwordHasher.HashPassword(dummy, "dummy-password-for-timing");
    }

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
