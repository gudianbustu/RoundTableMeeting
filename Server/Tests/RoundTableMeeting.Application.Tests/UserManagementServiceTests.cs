using RoundTableMeeting.Abstraction.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Application.UserManagement;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;

namespace RoundTableMeeting.Application.Tests;

public sealed class UserManagementServiceTests
{
    [Fact]
    public async Task ListMapsPublicFieldsInRepositoryOrder()
    {
        var first = Account("bob", "Bob");
        var second = Account("alice", "Alice");

        var result = await Service(new FakeRepository(first, second), new FakeUnitOfWork()).ListUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal([first.ID, second.ID], result.Select(item => item.UserId));
        Assert.All(result, item => Assert.Equal(["UserId", "Username", "Nickname", "IsAdmin"], item.GetType().GetProperties().Select(property => property.Name)));
    }

    [Fact]
    public async Task CreateNormalizesAndHashesWithoutWritingPlaintext()
    {
        var unitOfWork = new FakeUnitOfWork();
        var result = await Service(new FakeRepository(), unitOfWork).CreateUserAsync(new CreateUserRequest { Username = " Alice ", Nickname = " Alice ", InitialPassword = " secret " }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.Success, result.Status);
        Assert.Equal("Alice", result.User?.Username);
        Assert.Equal("Alice", result.User?.Nickname);
        Assert.Single(unitOfWork.Added);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.NotEqual(" secret ", unitOfWork.Added[0].PasswordHash);
    }

    [Fact]
    public async Task CreateInvalidFieldsReturnFixedErrorsWithoutWrite()
    {
        var unitOfWork = new FakeUnitOfWork();
        var result = await Service(new FakeRepository(), unitOfWork).CreateUserAsync(new CreateUserRequest { Username = " ", Nickname = " ", InitialPassword = " " }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.ValidationFailed, result.Status);
        Assert.Equal(["initialPassword", "nickname", "username"], result.Errors!.Keys.OrderBy(key => key));
        Assert.Empty(unitOfWork.Added);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task CreateDuplicateReturnsUsernameConflict()
    {
        var existing = Account("alice", "Alice");
        var result = await Service(new FakeRepository(existing), new FakeUnitOfWork()).CreateUserAsync(new CreateUserRequest { Username = " ALICE ", Nickname = "Other", InitialPassword = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.UsernameConflict, result.Status);
    }

    [Fact]
    public async Task UpdateNicknameReturnsNotFoundOrUpdatedSummary()
    {
        var account = Account("alice", "Alice");
        var service = Service(new FakeRepository(account), new FakeUnitOfWork());

        var updated = await service.UpdateNicknameAsync(account.ID, new UpdateNicknameRequest { Nickname = " New " }, TestContext.Current.CancellationToken);
        var missing = await service.UpdateNicknameAsync(Guid.NewGuid(), new UpdateNicknameRequest { Nickname = "New" }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.Success, updated.Status);
        Assert.Equal("New", account.Nickname);
        Assert.Equal(UserManagementStatus.UserNotFound, missing.Status);
    }

    [Fact]
    public async Task UpdateNicknameValidationReturnsNicknameError()
    {
        var result = await Service(new FakeRepository(Account("alice", "Alice")), new FakeUnitOfWork()).UpdateNicknameAsync(Guid.NewGuid(), new UpdateNicknameRequest { Nickname = " " }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.ValidationFailed, result.Status);
        Assert.Contains("nickname", result.Errors!.Keys);
    }

    [Fact]
    public async Task ResetPasswordUsesOnlyFixedValue()
    {
        var account = Account("alice", "Alice", "old-hash");
        var result = await Service(new FakeRepository(account), new FakeUnitOfWork()).ResetPasswordAsync(account.ID, TestContext.Current.CancellationToken);
        var verifier = new PasswordHasher<UserAccount>();

        Assert.Equal(UserManagementStatus.Success, result.Status);
        Assert.Equal(PasswordVerificationResult.Success, verifier.VerifyHashedPassword(account, account.PasswordHash, "123456"));
        Assert.Equal(PasswordVerificationResult.Failed, verifier.VerifyHashedPassword(account, account.PasswordHash, "old-hash"));
        Assert.Null(result.User);
    }

    [Fact]
    public async Task ResetPasswordMissingAccountDoesNotHash()
    {
        var hasher = new RecordingHasher();
        var result = await Service(new FakeRepository(), new FakeUnitOfWork(), hasher).ResetPasswordAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.UserNotFound, result.Status);
        Assert.Equal(0, hasher.HashCalls);
    }

    [Fact]
    public async Task ConcurrentUsernameConflictIsTranslatedToResult()
    {
        var unitOfWork = new FakeUnitOfWork { CommitException = new RoundTableMeeting.Domain.Exceptions.UsernameConflictException("conflict") };
        var result = await Service(new FakeRepository(), unitOfWork).CreateUserAsync(new CreateUserRequest { Username = "alice", Nickname = "Alice", InitialPassword = "secret" }, TestContext.Current.CancellationToken);

        Assert.Equal(UserManagementStatus.UsernameConflict, result.Status);
    }

    private static UserManagementService Service(FakeRepository repository, FakeUnitOfWork unitOfWork, IPasswordHasher<UserAccount>? hasher = null)
        {
        repository.Actor.SetAdministrator(true);
        return new(repository, unitOfWork, hasher ?? new PasswordHasher<UserAccount>(), new ActorContext(repository.Actor.ID), new TestAccountTransactionRunner(unitOfWork));
    }

    private static UserAccount Account(string username, string nickname, string hash = "hash")
    {
        var account = UserAccount.Create(username, nickname);
        account.InitializePasswordHash(hash);
        return account;
    }

    private sealed class FakeRepository : SqliteRepository<UserAccount, Guid>, IUserAccountRepository
    {
        public UserAccount Actor { get; } = Account("operator", "Operator");
        private readonly List<UserAccount> accounts;
        public FakeRepository(params UserAccount[] accounts) : base(CreateContext()) => this.accounts = [.. accounts];
        public Task<IReadOnlyList<UserAccount>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UserAccount>>(accounts);
        public Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default) => Task.FromResult(accounts.SingleOrDefault(account => account.NormalizedUsername == normalizedUsername));
        public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == Actor.ID ? Actor : accounts.SingleOrDefault(account => account.ID == id));
        public async Task<int> CountAdministratorsAsync(CancellationToken cancellationToken = default) => (await ListAllAsync(cancellationToken)).Count(a => a.IsAdmin);
        public async Task UpdateNicknameAsync(Guid id, string nickname, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ChangeNickname(nickname);
        public async Task UpdatePasswordHashAsync(Guid id, string hash, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.ReplacePasswordHash(hash);
        public async Task UpdateAdministratorAsync(Guid id, bool value, CancellationToken cancellationToken = default) => (await FindByIdAsync(id, cancellationToken))!.SetAdministrator(value);
        private static RoundTableMeetingDbContext CreateContext() => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>().UseSqlite("Data Source=:memory:").Options);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public List<UserAccount> Added { get; } = [];
        public int CommitCount { get; private set; }
        public Exception? CommitException { get; init; }
        public void RegisterAdd(Materal.TTA.Common.IEntity<Guid> entity) => Added.Add((UserAccount)entity);
        public void RegisterEdit(Materal.TTA.Common.IEntity<Guid> entity) { }
        public void RegisterDelete(Materal.TTA.Common.IEntity<Guid> entity) { }
        public Task<int> CommitAsync(CancellationToken cancellationToken = default) { CommitCount++; return CommitException is null ? Task.FromResult(1) : Task.FromException<int>(CommitException); }
    }

    private sealed record ActorContext(Guid? UserId) : ICurrentUserContext { public bool IsAuthenticated => true; }

    private sealed class RecordingHasher : IPasswordHasher<UserAccount>
    {
        public int HashCalls { get; private set; }
        public string HashPassword(UserAccount user, string password) { HashCalls++; return "hash"; }
        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword) => PasswordVerificationResult.Failed;
    }
}
