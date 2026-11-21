using System;
using System.Threading.RateLimiting;

namespace AnimatedArtworks.Infrastructure;

public sealed class AppleLookupLimiter
{
    private readonly PartitionedRateLimiter<string> _limiter;

    // Bulk clients (ClientIdentity.BulkPrefix) get their own larger bucket; the Worker's global bulk budget already spreads them out.
    public AppleLookupLimiter(int perClientPerMinute, int bulkPerClientPerMinute, int globalPerMinute)
    {
        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<string, string>(client =>
                RateLimitPartition.GetTokenBucketLimiter(client, key =>
                    Bucket(key.StartsWith(ClientIdentity.BulkPrefix, StringComparison.Ordinal) ? bulkPerClientPerMinute : perClientPerMinute))),
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
