using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingJoinRateLimiter(IOptions<MeetingJoinRateLimitOptions> options, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, Counter> counters = new();
    private long lastCleanupTicks;

    public bool TryAcquire(Guid userId, string? source, out int retryAfterSeconds)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var window = TimeSpan.FromSeconds(settings.WindowSeconds);
        var user = Acquire($"user:{userId:D}", settings.PerUserLimit, now, window);
        var address = string.IsNullOrWhiteSpace(source) ? Result.Allowed : Acquire($"source:{source}", settings.PerSourceLimit, now, window);
        retryAfterSeconds = Math.Max(user.RetryAfterSeconds, address.RetryAfterSeconds);
        return user.IsAllowed && address.IsAllowed;
    }

    private Result Acquire(string key, int limit, DateTimeOffset now, TimeSpan window)
    {
        var counter = counters.GetOrAdd(key, _ => new Counter(now));
        lock (counter)
        {
            if (now - counter.WindowStartedAt >= window)
            {
                counter.WindowStartedAt = now;
                counter.Count = 0;
            }
            if (counter.Count >= limit)
                return new(false, Math.Max(1, (int)Math.Ceiling((window - (now - counter.WindowStartedAt)).TotalSeconds)));
            counter.Count++;
            var cleanupAt = Interlocked.Read(ref lastCleanupTicks);
            if (now.UtcTicks - cleanupAt >= TimeSpan.FromMinutes(1).Ticks
                && Interlocked.CompareExchange(ref lastCleanupTicks, now.UtcTicks, cleanupAt) == cleanupAt)
            {
                RemoveExpired(now, window);
            }
            return Result.Allowed;
        }
    }

    private void RemoveExpired(DateTimeOffset now, TimeSpan window)
    {
        foreach (var item in counters)
            if (now - item.Value.WindowStartedAt >= window) counters.TryRemove(item.Key, out _);
    }

    private sealed class Counter(DateTimeOffset windowStartedAt)
    {
        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;
        public int Count { get; set; }
    }

    private readonly record struct Result(bool IsAllowed, int RetryAfterSeconds)
    {
        public static Result Allowed => new(true, 0);
    }
}
