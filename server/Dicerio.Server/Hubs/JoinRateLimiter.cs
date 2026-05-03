using System.Collections.Concurrent;

namespace Dicerio.Server.Hubs;

public sealed class JoinRateLimiterOptions
{
    public int MaxAttemptsPerWindow { get; set; } = 12;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Lightweight token-bucket-ish limiter to slow down room-code guessing.
/// Keyed by an arbitrary string (typically connection id or remote IP).
/// Single-process / in-memory; Redis later if needed.
/// </summary>
public sealed class JoinRateLimiter
{
    private readonly JoinRateLimiterOptions _options;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public JoinRateLimiter(JoinRateLimiterOptions options) => _options = options;

    public bool TryAcquire(string key)
    {
        var now = DateTime.UtcNow;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (bucket)
        {
            if (now - bucket.WindowStartUtc > _options.Window)
            {
                bucket.WindowStartUtc = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= _options.MaxAttemptsPerWindow)
            {
                return false;
            }

            bucket.Count++;
            return true;
        }
    }

    private sealed class Bucket
    {
        public DateTime WindowStartUtc { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
    }
}
