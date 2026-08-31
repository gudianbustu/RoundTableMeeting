namespace RoundTableMeeting.Domain.Exceptions;

/// <summary>
/// 表示持久化过程中检测到并发更新冲突
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>
    /// 初始化并发冲突异常
    /// </summary>
    public ConcurrencyConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
