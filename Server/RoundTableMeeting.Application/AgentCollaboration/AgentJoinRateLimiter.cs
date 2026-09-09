using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentJoinRateLimiter(AgentCollaborationOptions options, TimeProvider timeProvider)
    : IAgentJoinRateLimiter
{
    private readonly ConcurrentDictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private long lastCleanupTicks;

    public bool TryAcquire(
        string? sourceAddress,
        string? meetingCode,
        string? displayName,
        McpTransportSessionKey transportSession,
        out int retryAfterSeconds)
    {
        var now = timeProvider.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.JoinRateLimitWindowSeconds);
        var dimensions = new List<(string Key, int Limit)>(capacity: 4)
        {
            (Key("source", NormalizeSource(sourceAddress)), options.JoinRateLimitPerSource),
            (Key("meeting", NormalizeMeetingCode(meetingCode)), options.JoinRateLimitPerMeetingCode),
            (Key("name", NormalizeDisplayName(displayName)), options.JoinRateLimitPerName)
        };
        if (!transportSession.IsEmpty)
            dimensions.Add((Key("connection", transportSession.ToStorageBinding()), options.JoinRateLimitPerConnection));

        lock (sync)
        {
            var countersForRequest = dimensions
                .Select(dimension => (dimension, Counter: counters.TryGetValue(dimension.Key, out var counter) ? counter : null))
                .ToArray();
            var results = countersForRequest
                .Select(item => item.Counter is null
                    ? Result.Allowed
                    : GetResult(item.Counter, item.dimension.Limit, now, window))
                .ToArray();
            retryAfterSeconds = results.Max(result => result.RetryAfterSeconds);
            if (results.Any(result => !result.IsAllowed)) return false;

            foreach (var (dimension, _) in countersForRequest)
            {
                var counter = counters.GetOrAdd(dimension.Key, _ => new Counter(now));
                counter.Count++;
            }
            Cleanup(now, window);
            return true;
        }
    }

    private static Result GetResult(Counter counter, int limit, DateTimeOffset now, TimeSpan window)
    {
        if (now - counter.WindowStartedAt >= window)
        {
            counter.WindowStartedAt = now;
            counter.Count = 0;
        }
        return counter.Count >= limit
            ? new(false, Math.Max(1, (int)Math.Ceiling((window - (now - counter.WindowStartedAt)).TotalSeconds)))
            : Result.Allowed;
    }

    private void Cleanup(DateTimeOffset now, TimeSpan window)
    {
        if (now.UtcTicks - lastCleanupTicks < TimeSpan.FromMinutes(1).Ticks)
            return;
        lastCleanupTicks = now.UtcTicks;
        foreach (var item in counters)
            if (now - item.Value.WindowStartedAt >= window)
                counters.TryRemove(item.Key, out _);
    }

    private static string Key(string dimension, string value)
        => dimension + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static string NormalizeSource(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToUpperInvariant();

    private static string NormalizeMeetingCode(string? value)
        => Guid.TryParseExact(value?.Trim(), "D", out var code)
            ? code.ToString("D").ToLowerInvariant()
            : string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToUpperInvariant();

    private static string NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        try
        {
            return MeetingParticipant.NormalizeDisplayName(value).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        }
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
