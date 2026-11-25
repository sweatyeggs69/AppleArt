using System;

namespace AnimatedArtworks.Infrastructure;

public class SystemStatusService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private int _consecutiveRateLimits;
    private DateTime _backoffUntil = DateTime.MinValue;

    public bool IsRateLimited => _consecutiveRateLimits > 0;

    public void ReportRateLimit()
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _backoffUntil)
            {
                return;
            }

            _consecutiveRateLimits++;
            double minutes = Math.Min(MaxBackoff.TotalMinutes, InitialBackoff.TotalMinutes * Math.Pow(2, Math.Min(_consecutiveRateLimits - 1, 10)));
            _backoffUntil = now + TimeSpan.FromMinutes(minutes);
        }
    }

    public void ReportSuccess()
    {
        lock (_lock)
        {
            _consecutiveRateLimits = 0;
            _backoffUntil = DateTime.MinValue;
        }
    }

    public bool IsInBackoff() => DateTime.UtcNow < _backoffUntil;
}
