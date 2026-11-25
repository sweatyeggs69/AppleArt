using System;
using System.Threading;
using System.Threading.RateLimiting;
using Serilog;

namespace AnimatedArtworks.Infrastructure;

public readonly record struct AppleLookupClient(string Key, bool Relayed);

public sealed class AppleLookupLimiter
{
    private sealed class RequestScope(AppleLookupClient client)
    {
        public AppleLookupClient Client { get; } = client;
        public bool Throttled { get; set; }
    }

    private static readonly AsyncLocal<RequestScope?> Current = new();

    private readonly PartitionedRateLimiter<AppleLookupClient> _limiter;

    // Every Apple catalog call takes one permit. Bulk clients (ClientIdentity.BulkPrefix) get their own larger bucket;
    // requests relayed by the Worker and direct callers draw from separate global buckets.
    public AppleLookupLimiter(int perClientPerMinute, int bulkPerClientPerMinute, int relayedPerMinute, int directPerMinute)
    {
        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<AppleLookupClient, string>(client =>
                RateLimitPartition.GetTokenBucketLimiter(client.Key, key =>
                    Bucket(key.StartsWith(ClientIdentity.BulkPrefix, StringComparison.Ordinal) ? bulkPerClientPerMinute : perClientPerMinute))),
            PartitionedRateLimiter.Create<AppleLookupClient, string>(client =>
                client.Relayed
                    ? RateLimitPartition.GetTokenBucketLimiter("relayed", _ => Bucket(relayedPerMinute))
                    : RateLimitPartition.GetTokenBucketLimiter("direct", _ => Bucket(directPerMinute))));
    }

    public void BeginRequest(AppleLookupClient client) => Current.Value = new RequestScope(client);

    public bool CurrentRequestThrottled => Current.Value?.Throttled == true;

    public bool TryAcquireForCurrentRequest()
    {
        RequestScope? scope = Current.Value;
        AppleLookupClient client = scope?.Client ?? new AppleLookupClient("unknown", Relayed: false);

        using RateLimitLease lease = _limiter.AttemptAcquire(client);
        if (lease.IsAcquired)
        {
            return true;
        }

        if (scope is { Throttled: false })
        {
            Log.Information("Apple lookup limit reached for client {Client}", client.Key);
        }

        if (scope != null)
        {
            scope.Throttled = true;
        }

        return false;
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
