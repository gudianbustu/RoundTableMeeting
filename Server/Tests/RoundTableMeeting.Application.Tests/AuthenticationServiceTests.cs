using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AccountAccess;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using Microsoft.EntityFrameworkCore;

namespace RoundTableMeeting.Application.Tests;

public sealed class AuthenticationServiceTests
{
    [Fact]
    public async Task ValidCredentialsReturnUserWithoutPasswordHash()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash(new PasswordHasher<UserAccount>().HashPassword(account, "secret"));
        var repository = new FakeRepository { Account = account };
        var service = CreateService(repository, new FakeCurrentUserContext());

        var result = await service.AuthenticateAsync(new LoginRequest { Username = " ALICE ", Password = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.Success, result.Status);
        Assert.Equal(account.ID, result.User?.UserId);
        Assert.Equal("alice", result.User?.Username);
        Assert.Null(result.Errors);
    }

    [Fact]
    public async Task MissingAndWrongCredentialsUseTheSameResult()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash(new PasswordHasher<UserAccount>().HashPassword(account, "secret"));
        var wrong = await CreateService(new FakeRepository { Account = account }, new FakeCurrentUserContext()).AuthenticateAsync(new LoginRequest { Username = "alice", Password = "wrong" }, TestContext.Current.CancellationToken);
        var missing = await CreateService(new FakeRepository(), new FakeCurrentUserContext()).AuthenticateAsync(new LoginRequest { Username = "missing", Password = "wrong" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.InvalidCredentials, wrong.Status);
        Assert.Equal(wrong.Status, missing.Status);
        Assert.Null(wrong.User);
        Assert.Null(missing.User);
    }

    [Fact]
    public async Task MissingAccountStillInvokesDummyVerification()
    {
        var hasher = new RecordingPasswordHasher();
        var service = new AuthenticationService(new FakeRepository(), new FakeCurrentUserContext(), new PasswordVerificationService(hasher));

        var result = await service.AuthenticateAsync(new LoginRequest { Username = "missing", Password = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.InvalidCredentials, result.Status);
        Assert.Equal(1, hasher.VerifyCalls);
    }

    [Fact]
    public async Task SuccessRehashNeededIsAcceptedAndPasswordIsNotRewritten()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash("old-hash");
        var hasher = new RecordingPasswordHasher { Result = Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded };
        var service = new AuthenticationService(new FakeRepository { Account = account }, new FakeCurrentUserContext(), new PasswordVerificationService(hasher));

        var result = await service.AuthenticateAsync(new LoginRequest { Username = "alice", Password = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.Success, result.Status);
        Assert.Equal("old-hash", account.PasswordHash);
    }

    [Fact]
    public async Task PasswordIsVerifiedWithoutTrimming()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash("hash");
        var hasher = new RecordingPasswordHasher { Result = Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success };
        var service = new AuthenticationService(new FakeRepository { Account = account }, new FakeCurrentUserContext(), new PasswordVerificationService(hasher));

        await service.AuthenticateAsync(new LoginRequest { Username = "alice", Password = " secret " }, TestContext.Current.CancellationToken);

        Assert.Equal(" secret ", hasher.LastPassword);
    }

    [Fact]
    public async Task UsernameNormalizationFailureReturnsValidationFailed()
    {
        var tooLong = new string('a', 65);
        var result = await CreateService(new FakeRepository(), new FakeCurrentUserContext()).AuthenticateAsync(new LoginRequest { Username = tooLong, Password = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.ValidationFailed, result.Status);
        Assert.Contains("username", result.Errors?.Keys ?? []);
    }

    [Fact]
    public async Task PasswordWithOuterSpacesIsPassedUnchanged()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash("hash");
        var hasher = new RecordingPasswordHasher { Result = Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success };
        var service = new AuthenticationService(new FakeRepository { Account = account }, new FakeCurrentUserContext(), new PasswordVerificationService(hasher));

        await service.AuthenticateAsync(new LoginRequest { Username = "alice", Password = " secret " }, TestContext.Current.CancellationToken);

        Assert.Equal(" secret ", hasher.LastPassword);
    }

    [Fact]
    public async Task FirstSeedAddsTwoAccountsAndCommitsOnce()
    {
        var repository = new MultiAccountRepository();
        var unitOfWork = new FakeUnitOfWork();
        var seeder = new AccountSeeder(repository, unitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(unitOfWork));

        await seeder.SeedAsync([new SeedAccountInput("alice", "Alice", "secret-a"), new SeedAccountInput("bob", "Bob", "secret-b")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, unitOfWork.AddedEntities.Count);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task InvalidInputDoesNotQueryRepository()
    {
        var repository = new FakeRepository();
        var result = await CreateService(repository, new FakeCurrentUserContext()).AuthenticateAsync(new LoginRequest { Username = " ", Password = "" }, TestContext.Current.CancellationToken);

        Assert.Equal(AuthenticateStatus.ValidationFailed, result.Status);
        Assert.Equal(["password", "username"], result.Errors?.Keys.OrderBy(key => key));
        Assert.Equal(0, repository.LookupCount);
    }

    [Fact]
    public async Task AnonymousCurrentUserIsUnauthenticated()
    {
        var result = await CreateService(new FakeRepository(), new FakeCurrentUserContext()).GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CurrentUserStatus.Unauthenticated, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task SeedIsIdempotentAndDoesNotOverwriteExistingAccount()
    {
        var existing = UserAccount.Create("alice", "Original");
        existing.InitializePasswordHash("original-hash");
        var repository = new FakeRepository { Account = existing };
        var unitOfWork = new FakeUnitOfWork();
        var seeder = new AccountSeeder(repository, unitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(unitOfWork));

        await seeder.SeedAsync([new SeedAccountInput("alice", "Changed", "new-secret")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Original", existing.Nickname);
        Assert.Equal("original-hash", existing.PasswordHash);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task CurrentUserSuccessReturnsOnlyApprovedProfileFields()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash("hash");
        var context = new FakeCurrentUserContext { IsAuthenticated = true, UserId = account.ID };
        var result = await CreateService(new FakeRepository { Account = account }, context).GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CurrentUserStatus.Success, result.Status);
        Assert.Equal(account.ID, result.User?.UserId);
        Assert.Equal("alice", result.User?.Username);
        Assert.Equal("Alice", result.User?.Nickname);
        Assert.Null(result.User?.GetType().GetProperty("PasswordHash"));
    }

    [Fact]
    public async Task MissingCurrentUserAccountReturnsUnauthenticated()
    {
        var result = await CreateService(new FakeRepository(), new FakeCurrentUserContext { IsAuthenticated = true, UserId = Guid.NewGuid() }).GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CurrentUserStatus.Unauthenticated, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task DuplicateSeedUsernamesFailBeforeAnyRegistration()
    {
        var unitOfWork = new FakeUnitOfWork();
        var seeder = new AccountSeeder(new MultiAccountRepository(), unitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(unitOfWork));

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync([new SeedAccountInput("alice", "Alice", "a"), new SeedAccountInput(" ALICE ", "Other", "b")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(unitOfWork.AddedEntities);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task OneExistingAndOneMissingSeedOnlyAddsMissingAccount()
    {
        var existing = UserAccount.Create("alice", "Original");
        existing.InitializePasswordHash("original");
        var repository = new MultiAccountRepository(existing);
        var unitOfWork = new FakeUnitOfWork();
        var seeder = new AccountSeeder(repository, unitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(unitOfWork));

        await seeder.SeedAsync([new SeedAccountInput("alice", "Changed", "new-a"), new SeedAccountInput("bob", "Bob", "new-b")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(unitOfWork.AddedEntities);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Equal("Original", existing.Nickname);
    }

    [Fact]
    public async Task SeedCancellationAndCommitFailurePropagate()
    {
        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountSeeder(new MultiAccountRepository(), new FakeUnitOfWork(), new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(new FakeUnitOfWork())).SeedAsync([new SeedAccountInput("alice", "Alice", "secret")], cancelled));

        var failedUnitOfWork = new FakeUnitOfWork { CommitException = new InvalidOperationException("commit failed") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AccountSeeder(new MultiAccountRepository(), failedUnitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(failedUnitOfWork)).SeedAsync([new SeedAccountInput("alice", "Alice", "secret")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, failedUnitOfWork.CommitCount);
    }

    [Fact]
    public async Task SeedRepeatedWithTwoExistingAccountsPreservesProfile()
    {
        var alice = UserAccount.Create("alice", "Alice");
        alice.InitializePasswordHash("hash-a");
        var bob = UserAccount.Create("bob", "Bob");
        bob.InitializePasswordHash("hash-b");
        var repository = new MultiAccountRepository(alice, bob);
        var unitOfWork = new FakeUnitOfWork();

        await new AccountSeeder(repository, unitOfWork, new PasswordHasher<UserAccount>(), new TestAccountTransactionRunner(unitOfWork)).SeedAsync([new SeedAccountInput("alice", "Changed", "new-a"), new SeedAccountInput("bob", "Changed", "new-b")], initialAdminUsername: "alice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(unitOfWork.AddedEntities);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Equal("hash-a", alice.PasswordHash);
        Assert.Equal("hash-b", bob.PasswordHash);
    }

    private static AuthenticationService CreateService(FakeRepository repository, FakeCurrentUserContext currentUser)
        => new(repository, currentUser, new PasswordVerificationService(new PasswordHasher<UserAccount>()));

    private sealed class FakeRepository : SqliteRepository<UserAccount, Guid>, IUserAccountRepository
    {
        public FakeRepository() : base(CreateContext()) { }
        public UserAccount? Account { get; init; }
        public int LookupCount { get; private set; }
        public Task<IReadOnlyList<UserAccount>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UserAccount>>(Account is null ? [] : [Account]);
        public Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default) { LookupCount++; return Task.FromResult(Account?.NormalizedUsername == normalizedUsername ? Account : null); }
        public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Account?.ID == id ? Account : null);
        public async Task<int> CountAdministratorsAsync(CancellationToken cancellationToken = default) => (await ListAllAsync(cancellationToken)).Count(a => a.IsAdmin);
        public async Task UpdateNicknameAsync(Guid id, string nickname, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ChangeNickname(nickname);
        public async Task UpdatePasswordHashAsync(Guid id, string hash, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ReplacePasswordHash(hash);
        public async Task UpdateAdministratorAsync(Guid id, bool value, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.SetAdministrator(value);
        private static RoundTableMeetingDbContext CreateContext()
            => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>().UseSqlite("Data Source=:memory:").Options);
    }

    private sealed class MultiAccountRepository : SqliteRepository<UserAccount, Guid>, IUserAccountRepository
    {
        public MultiAccountRepository(params UserAccount[] accounts) : base(CreateContext()) => AddedAccounts.AddRange(accounts);
        public List<UserAccount> AddedAccounts { get; } = [];
        public Task<IReadOnlyList<UserAccount>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UserAccount>>(AddedAccounts);
        public Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default)
            => Task.FromResult(AddedAccounts.SingleOrDefault(account => account.NormalizedUsername == normalizedUsername));
        public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(AddedAccounts.SingleOrDefault(account => account.ID == id));
        public async Task<int> CountAdministratorsAsync(CancellationToken cancellationToken = default) => (await ListAllAsync(cancellationToken)).Count(a => a.IsAdmin);
        public async Task UpdateNicknameAsync(Guid id, string nickname, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ChangeNickname(nickname);
        public async Task UpdatePasswordHashAsync(Guid id, string hash, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ReplacePasswordHash(hash);
        public async Task UpdateAdministratorAsync(Guid id, bool value, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.SetAdministrator(value);
        private static RoundTableMeetingDbContext CreateContext()
            => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>().UseSqlite("Data Source=:memory:").Options);
    }

    private sealed class RecordingPasswordHasher : IPasswordHasher<UserAccount>
    {
        public int VerifyCalls { get; private set; }
        public string? LastPassword { get; private set; }
        public Microsoft.AspNetCore.Identity.PasswordVerificationResult Result { get; init; } = Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
        public string HashPassword(UserAccount user, string password) => "recorded-hash";
        public Microsoft.AspNetCore.Identity.PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        {
            VerifyCalls++;
            LastPassword = providedPassword;
            return Result;
        }
    }

    private sealed class FakeCurrentUserContext : ICurrentUserContext
    {
        public bool IsAuthenticated { get; init; }
        public Guid? UserId { get; init; }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int CommitCount { get; private set; }
        public Exception? CommitException { get; init; }
        public List<Materal.TTA.Common.IEntity<Guid>> AddedEntities { get; } = [];
        public void RegisterAdd(Materal.TTA.Common.IEntity<Guid> entity) => AddedEntities.Add(entity);
        public void RegisterEdit(Materal.TTA.Common.IEntity<Guid> entity) { }
        public void RegisterDelete(Materal.TTA.Common.IEntity<Guid> entity) { }
        public Task<int> CommitAsync(CancellationToken cancellationToken = default) { CommitCount++; if (CommitException is not null) return Task.FromException<int>(CommitException); return Task.FromResult(1); }
    }
}
