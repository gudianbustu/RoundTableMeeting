using RoundTableMeeting.Abstraction.Contracts.Authentication;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAccountSeeder
{
    Task SeedAsync(IReadOnlyCollection<SeedAccountInput> accounts, CancellationToken cancellationToken = default);
}
