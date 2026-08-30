namespace RoundTableMeeting.IntegrationTests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;

    public ManualTimeProvider()
        : this(DateTimeOffset.UtcNow)
    {
    }

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        this.utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}
