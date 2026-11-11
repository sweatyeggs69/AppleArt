using System;
using System.Threading.RateLimiting;

namespace AnimatedArtworks.Infrastructure;

public sealed class AppleLookupLimiter
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public AppleLookupLimiter(int perClientPerMinute, int globalPerMinute)
    {
        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<string, string>(client =>
                RateLimitPartition.GetTokenBucketLimiter(client, _ => Bucket(perClientPerMinute))),
            PartitionedRateLimiter.Create<string, string>(_ =>
                RateLimitPartition.GetTokenBucketLimiter("global", _ => Bucket(globalPerMinute))));
    }

    public bool TryAcquire(string client)
    {
        using RateLimitLease lease = _limiter.AttemptAcquire(client);
        return lease.IsAcquired;
    }

    private static TokenBucketRateLimiterOptions Bucket(int perMinute) => new()
    {
        TokenLimit = Math.Max(1, perMinute),
        TokensPerPeriod = Math.Max(1, perMinute),
        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    };
}
