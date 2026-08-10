using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using YuSwitch.Data;
using YuSwitch.Data.Entities;

namespace YuSwitch.Services;

/// <summary>
/// Writes usage logs asynchronously (fire-and-forget into a channel, drained
/// by a background worker) so the response path isn't blocked.
/// </summary>
public class UsageService : IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbf;
    private readonly ILogger<UsageService> _log;
    private readonly AppSettingsService _settings;
    private readonly Channel<UsageLogEntity> _ch;
    private readonly RealtimeNotificationService? _notifications;
    private readonly System.Threading.Timer _retentionTimer;
    private int _cleanupRunning; // Interlocked guard against overlapping cleanups

    public UsageService(IDbContextFactory<AppDbContext> dbf, ILogger<UsageService> log,
        AppSettingsService settings, RealtimeNotificationService? notifications = null)
    {
        _dbf = dbf; _log = log; _settings = settings;
        _notifications = notifications;
        // Bounded + DropOldest: if SQLite stalls (locked DB, dead disk) the
        // queue sacrifices the oldest log lines instead of growing without
        // limit — usage logs are diagnostics, not billing-grade records.
        _ch = Channel.CreateBounded<UsageLogEntity>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _ = Task.Run(DrainAsync);

        // Retention cleanup: first sweep shortly after startup, then hourly.
        // The retention window is re-read from AppSettingsService on each run,
        // so a saved change takes effect at the next tick without a restart.
        _retentionTimer = new System.Threading.Timer(_ => CleanupTick(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _retentionTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void CleanupTick()
    {
        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0)
            return; // a previous sweep is still running — skip this tick
        try
        {
            await CleanupOldLogsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "usage-log retention cleanup failed");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);
        }
    }

    /// <summary>Deletes usage logs older than the configured retention window.
    /// Public so it can also be invoked on-demand (e.g. admin action).</summary>
    public async Task CleanupOldLogsAsync(CancellationToken ct = default)
    {
        var days = _settings.UsageLogRetentionDays;
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var cutoff = DateTime.Now.AddDays(-days);
        var deleted = await db.UsageLogs
            .Where(l => l.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            _log.LogInformation("retention cleanup deleted {N} usage-log rows older than {Days}d", deleted, days);
    }

    public void Record(UsageLogEntity entry)
    {
        if (_ch.Writer.TryWrite(entry))
        {
            // Notify with context for smart filtering
            var context = new NotificationContext
            {
                Model = entry.Model,
                Service = entry.ServiceName,
                Provider = entry.ProviderType
            };
            _notifications?.Broadcast("new-log", context);
        }
    }

    private async Task DrainAsync()
    {
        var consecutiveFailures = 0;
        await foreach (var e in _ch.Reader.ReadAllAsync())
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync();
                db.UsageLogs.Add(e);
                await db.SaveChangesAsync();
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                // Back off on persistent failure so a locked/broken DB isn't
                // hammered in a tight loop (log lines keep queueing; the
                // bounded channel drops the oldest once full).
                consecutiveFailures++;
                _log.LogError(ex, "failed to persist usage log (streak {N})", consecutiveFailures);
                if (consecutiveFailures >= 3)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, consecutiveFailures)));
            }
        }
    }

    /// <summary>Recent usage stats for the dashboard, optionally filtered.</summary>
    public async Task<UsageStats> GetStatsAsync(int hours = 24, string? model = null,
        string? service = null, string? provider = null, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var since = DateTime.Now.AddHours(-hours);
        var logs = await Filtered(db, since, model, service, provider).ToListAsync(ct);

        return new UsageStats(
            Total: logs.Count,
            Success: logs.Count(l => l.Success),
            Failed: logs.Count(l => !l.Success),
            PromptTokens: logs.Sum(l => l.PromptTokens),
            CompletionTokens: logs.Sum(l => l.CompletionTokens),
            TotalTokens: logs.Sum(l => l.TotalTokens),
            ReasoningTokens: logs.Sum(l => l.ReasoningTokens),
            CacheCreationTokens: logs.Sum(l => l.CacheCreationTokens),
            CacheReadTokens: logs.Sum(l => l.CacheReadTokens),
            CacheHitCount: logs.Count(l => l.CacheHit),
            AvgLatencyMs: logs.Count == 0 ? 0 : (long)Math.Round(logs.Average(l => (double)l.LatencyMs)),
            AvgTtftMs: logs.Where(l => l.TtftMs > 0).Select(l => (long?)l.TtftMs).Average() is { } ttft ? (long)Math.Round((double)ttft) : 0,
            ByModel: logs.GroupBy(l => l.Model)
                         .ToDictionary(g => g.Key, g => g.Count()),
            ByProvider: logs.GroupBy(l => l.ProviderType)
                           .ToDictionary(g => g.Key, g => g.Count()),
            ByService: logs.GroupBy(l => l.ServiceName)
                          .ToDictionary(g => g.Key, g => g.Count()))
        {
            ByApiKey = logs.GroupBy(l => l.ApiKeyName)
                           .ToDictionary(g => g.Key, g => g.Count()),
        };
    }
    /// <summary>Per-hour request/token buckets for the dashboard trend chart.
    /// Returns exactly <paramref name="hours"/> buckets ending at the current hour,
    /// including empty ones so the chart has a continuous time axis.</summary>
    public async Task<List<HourlyBucket>> GetHourlyAsync(int hours = 24, string? model = null,
        string? service = null, string? provider = null, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var start = end.AddHours(-(hours - 1));
        var logs = await Filtered(db, start, model, service, provider).ToListAsync(ct);

        var byHour = logs.GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month,
                l.Timestamp.Day, l.Timestamp.Hour, 0, 0))
            .ToDictionary(g => g.Key, g => g.ToList());

        var buckets = new List<HourlyBucket>(hours);
        for (var h = start; h <= end; h = h.AddHours(1))
        {
            byHour.TryGetValue(h, out var hl);
            buckets.Add(new HourlyBucket(
                Hour: h.ToString("HH:00"),
                Success: hl?.Count(l => l.Success) ?? 0,
                Failed: hl?.Count(l => !l.Success) ?? 0,
                TotalTokens: hl?.Sum(l => l.TotalTokens) ?? 0));
        }
        return buckets;
    }

    /// <summary>Distinct model/service/provider names present in the logs, for
    /// populating filter dropdowns.</summary>
    public async Task<FilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var logs = db.UsageLogs.AsNoTracking();
        return new FilterOptions(
            await logs.Select(l => l.Model).Distinct().OrderBy(x => x).ToListAsync(ct),
            await logs.Select(l => l.ServiceName).Distinct().OrderBy(x => x).ToListAsync(ct),
            await logs.Select(l => l.ProviderType).Distinct().OrderBy(x => x).ToListAsync(ct));
    }

    private static IQueryable<UsageLogEntity> Filtered(AppDbContext db, DateTime since,
        string? model, string? service, string? provider)
    {
        var q = db.UsageLogs.AsNoTracking().Where(l => l.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(model)) q = q.Where(l => l.Model == model);
        if (!string.IsNullOrWhiteSpace(service)) q = q.Where(l => l.ServiceName == service);
        if (!string.IsNullOrWhiteSpace(provider)) q = q.Where(l => l.ProviderType == provider);
        return q;
    }
}

public record FilterOptions(List<string> Models, List<string> Services, List<string> Providers);

public record HourlyBucket(string Hour, int Success, int Failed, int TotalTokens);

public record UsageStats(
    int Total, int Success, int Failed,
    int PromptTokens, int CompletionTokens, int TotalTokens,
    int ReasoningTokens, int CacheCreationTokens, int CacheReadTokens, int CacheHitCount,
    long AvgLatencyMs, long AvgTtftMs,
    Dictionary<string, int> ByModel, Dictionary<string, int> ByProvider, Dictionary<string, int> ByService)
{
    /// <summary>Request counts per API key (grouped by ApiKeyName). Non-positional
    /// so the dashboard fallback constructor <c>new UsageStats(0, ...)</c> keeps
    /// compiling; set via object initializer in GetStatsAsync.</summary>
    public Dictionary<string, int> ByApiKey { get; init; } = new();
}
