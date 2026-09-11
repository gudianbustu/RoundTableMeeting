namespace RoundTableMeeting.Abstraction.Services;

public interface IAccountTransactionRunner
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
