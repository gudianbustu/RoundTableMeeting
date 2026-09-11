using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.UnitOfWork;
namespace RoundTableMeeting.Application.Tests;
internal sealed class TestAccountTransactionRunner(IUnitOfWork unitOfWork) : IAccountTransactionRunner
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await action(cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}
