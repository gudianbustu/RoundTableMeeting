namespace RoundTableMeeting.Abstraction.Services;

public interface IMeetingTransactionRunner
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
