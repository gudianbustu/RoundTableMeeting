namespace RoundTableMeeting.IntegrationTests;

/// <summary>
/// 提供可手动推进的测试时间源
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>
    /// 当前模拟 UTC 时间
    /// </summary>
    private DateTimeOffset utcNow;

    public ManualTimeProvider()
        : this(DateTimeOffset.UtcNow)
    {
    }

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        this.utcNow = utcNow;
    }

    /// <summary>
    /// 返回当前模拟 UTC 时间
    /// </summary>
    public override DateTimeOffset GetUtcNow() => utcNow;

    /// <summary>
    /// 推进模拟时间
    /// </summary>
    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}
