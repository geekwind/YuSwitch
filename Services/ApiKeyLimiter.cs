using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using YuSwitch.Data;
using YuSwitch.Data.Entities;

namespace YuSwitch.Services;

/// <summary>
/// Enforces the per-key limit fields that ApiKeyEntity always carried but
/// nothing ever checked: QpmLimit, DailyQuota, IpAllowlist, ExpiresAt.
///
/// State is in-memory (single-instance exact, same trade-off as the adaptive
/// LB). The daily counter is seeded from UsageLogs on the first check of each
/// day, so a process restart cannot be used to reset a key's quota.
/// </summary>
public class ApiKeyLimiter
{
    private readonly IDbContextFactory<AppDbContext> _dbf;

    private sealed class KeyState
    {
        public readonly object Lock = new();
        // QPM token bucket (recreated when the configured limit changes).
        public TokenBucket? QpmBucket;
        public int QpmLimit = -1;
        // Daily request counter, seeded from the DB once per (key, day).
        public DateOnly Day;
        public int DailyCount;
        public bool Seeded;
    }

    private readonly ConcurrentDictionary<int, KeyState> _states = new();

    public ApiKeyLimiter(IDbContextFactory<AppDbContext> dbf) => _dbf = dbf;

    /// <summary>Outcome of a limit check; Allowed=false carries the HTTP status
    /// and message the middleware should surface.</summary>
    public readonly record struct LimitResult(bool Allowed, int Status, string? Message)
    {
        public static readonly LimitResult Ok = new(true, 0, null);
        public static LimitResult Deny(int status, string message) => new(false, status, message);
    }

    /// <summary>Run all per-key checks; consumes one QPM token and one daily
    /// quota slot when the request is allowed.</summary>
    public async Task<LimitResult> CheckAsync(ApiKeyEntity key, IPAddress? remoteIp, CancellationToken ct = default)
    {
        // Expiry
        if (key.ExpiresAt is { } exp && DateTime.Now >= exp)
            return LimitResult.Deny(401, "api key expired");

        // IP allowlist (empty = everyone)
        if (!string.IsNullOrWhiteSpace(key.IpAllowlist) && !IpAllowed(key.IpAllowlist, remoteIp))
            return LimitResult.Deny(403, "request ip not in the key's allowlist");

        var st = _states.GetOrAdd(key.Id, _ => new KeyState());

        // Daily quota — seed today's count from the usage log before first use
        // so a restart doesn't grant a fresh quota.
        if (key.DailyQuota > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var needSeed = false;
            lock (st.Lock)
            {
                if (st.Day != today) { st.Day = today; st.DailyCount = 0; st.Seeded = false; }
                needSeed = !st.Seeded;
            }
            if (needSeed)
            {
                var midnight = today.ToDateTime(TimeOnly.MinValue);
                int used;
                await using (var db = await _dbf.CreateDbContextAsync(ct))
                {
                    used = await db.UsageLogs.AsNoTracking()
                        .CountAsync(l => l.ApiKeyName == key.Name && l.Timestamp >= midnight, ct);
                }
                lock (st.Lock)
                {
                    if (st.Day == today && !st.Seeded)
                    {
                        st.DailyCount = Math.Max(st.DailyCount, used);
                        st.Seeded = true;
                    }
                }
            }
            lock (st.Lock)
            {
                if (st.DailyCount >= key.DailyQuota)
                    return LimitResult.Deny(429, $"daily quota exceeded ({key.DailyQuota} requests/day)");
            }
        }

        // QPM
        if (key.QpmLimit > 0)
        {
            TokenBucket bucket;
            lock (st.Lock)
            {
                if (st.QpmBucket is null || st.QpmLimit != key.QpmLimit)
                {
                    st.QpmBucket = new TokenBucket(key.QpmLimit, key.QpmLimit / 60.0);
                    st.QpmLimit = key.QpmLimit;
                }
                bucket = st.QpmBucket;
            }
            if (!bucket.TryTake())
                return LimitResult.Deny(429, $"rate limit exceeded ({key.QpmLimit} requests/min)");
        }

        // Passed everything — count this request against the daily quota.
        if (key.DailyQuota > 0)
            lock (st.Lock) st.DailyCount++;

        return LimitResult.Ok;
    }

    /// <summary>Comma-separated entries: plain IPs or IPv4 CIDR (a.b.c.d/n).</summary>
    private static bool IpAllowed(string allowlist, IPAddress? remote)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        foreach (var raw in allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = raw.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(raw, out var ip) && ip.Equals(remote))
                    return true;
                continue;
            }
            // CIDR (IPv4 only — IPv6 ranges are rare for this use case).
            if (!IPAddress.TryParse(raw[..slash], out var network)) continue;
            if (!int.TryParse(raw[(slash + 1)..], out var prefix) || prefix is < 0 or > 32) continue;
            if (network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

            var netBits = BitConverter.ToUInt32(network.GetAddressBytes().Reverse().ToArray());
            var ipBits = BitConverter.ToUInt32(remote.GetAddressBytes().Reverse().ToArray());
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((netBits & mask) == (ipBits & mask))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Refill-on-read token bucket (duplicated from Gateway internals so the
/// Services layer has no dependency on Gateway). Capacity = burst size.
/// </summary>
public sealed class TokenBucket
{
    private double _tokens;
    private long _lastTicks;
    private readonly double _capacity;
    private readonly double _ratePerSec;
    private readonly object _lock = new();

    public TokenBucket(double capacity, double ratePerSec)
    {
        _capacity = Math.Max(1.0, capacity);
        _ratePerSec = Math.Max(0.0001, ratePerSec);
        _tokens = _capacity;
        _lastTicks = Environment.TickCount64;
    }

    public bool TryTake()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            _tokens = Math.Min(_capacity, _tokens + (now - _lastTicks) / 1000.0 * _ratePerSec);
            _lastTicks = now;
            if (_tokens >= 1.0) { _tokens -= 1.0; return true; }
            return false;
        }
    }
}
