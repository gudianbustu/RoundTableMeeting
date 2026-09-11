using System.Security.Cryptography;
using System.Text;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentResumeRateLimiter(AgentCollaborationOptions options, TimeProvider timeProvider) : IAgentResumeRateLimiter
{
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private readonly object sync = new();

    public bool TryAcquire(string? sourceAddress, Guid connectionSessionId,
        out IAgentResumeAttempt? attempt, out int retryAfterSeconds)
    {
        var sourceKey = Key("source", string.IsNullOrWhiteSpace(sourceAddress) ? "unknown" : sourceAddress.Trim().ToUpperInvariant());
        var connectionKey = Key("connection", connectionSessionId.ToString("N"));
        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            foreach (var pair in counters.Where(pair => now >= pair.Value.ExpiresAt).ToArray()) counters.Remove(pair.Key);
            counters.TryGetValue(sourceKey, out var source);
            counters.TryGetValue(connectionKey, out var connection);
            attempt = null;
            retryAfterSeconds = Math.Max(RetryAfter(source, options.ResumeRateLimitPerSource, options.ResumeFailureLimitPerSource, now),
                RetryAfter(connection, options.ResumeRateLimitPerConnection, options.ResumeFailureLimitPerConnection, now));
            if (retryAfterSeconds > 0) return false;
            var required = (source is null ? 1 : 0) + (connection is null ? 1 : 0);
            if ((long)counters.Count + required > options.RecoveryRateLimitMaximumBuckets)
            {
                retryAfterSeconds = counters.Count == 0 ? options.ResumeRateLimitWindowSeconds
                    : SecondsUntil(counters.Values.Min(counter => counter.ExpiresAt), now);
                return false;
            }
            var expires = now.AddSeconds(options.ResumeRateLimitWindowSeconds);
            if (source is null) counters.Add(sourceKey, source = new(expires));
            if (connection is null) counters.Add(connectionKey, connection = new(expires));
            source.Attempts++;
            connection.Attempts++;
            attempt = new Attempt(this, source, connection);
            return true;
        }
    }

    private static string Key(string dimension, string value) =>
        dimension + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int RetryAfter(Counter? counter, int attemptLimit, int failureLimit, DateTimeOffset now) =>
        counter is not null && (counter.Attempts >= attemptLimit || counter.Failures >= failureLimit)
            ? SecondsUntil(counter.ExpiresAt, now) : 0;

    private static int SecondsUntil(DateTimeOffset expires, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Ceiling((expires - now).TotalSeconds), 1, int.MaxValue);

    private sealed class Counter(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Attempts { get; set; }
        public int Failures { get; set; }
    }

    private sealed class Attempt(AgentResumeRateLimiter owner, Counter source, Counter connection) : IAgentResumeAttempt
    {
        private bool failed;
        public void RecordFailure()
        {
            lock (owner.sync)
            {
                if (failed) return;
                failed = true;
                // The receipt refers to its admission window; a late callback cannot poison a newer window.
                source.Failures++;
                connection.Failures++;
            }
        }
    }
}
