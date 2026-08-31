namespace RoundTableMeeting.Domain.Exceptions;

/// <summary>
/// 表示规范化用户名已经被其他账号使用。
/// </summary>
public sealed class UsernameConflictException : Exception
{
    public UsernameConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
