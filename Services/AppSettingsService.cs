using Microsoft.EntityFrameworkCore;
using YuSwitch.Data;
using YuSwitch.Data.Entities;

namespace YuSwitch.Services;

/// <summary>
/// Application-level settings (software name, subtitle, ...). Backed by the
/// key/value <see cref="SettingEntity"/> table and cached in memory so the UI
/// shell (sidebar brand, top bar) can read them without a DB round-trip.
/// </summary>
public class AppSettingsService
{
    public const string KeyAppName = "app_name";
    public const string KeySubtitle = "app_subtitle";
    public const string KeyLogoType = "logo_type";   // auto | emoji | image
    public const string KeyLogoValue = "logo_value"; // emoji char, or data-URI for image

    // Listen address/port — read once at startup before Kestrel binds, so a
    // change only takes effect after a full app restart (see /admin/system/restart).
    public const string KeyListenHost = "listen_host";
    public const string KeyListenPort = "listen_port";
    public const string DefaultListenHost = "localhost";
    public const int DefaultListenPort = 5078;

    // Adaptive load-balancing tunables (hot-reloaded; read each dispatch).
    public const string KeyLbEwmaAlpha = "lb_ewma_alpha";                 // EWMA smoothing 0..1
    public const string KeyLbColdStartMs = "lb_cold_start_ms";            // neutral cold-start latency
    public const string KeyBreakerFailureThreshold = "breaker_failure_threshold"; // consecutive hard fails → OPEN
    public const string KeyBreakerCooldownBaseS = "breaker_cooldown_base_s";      // first cooldown (×1,×3,×6)
    public const string KeyBreaker429PenaltyMs = "breaker_429_penalty_ms"; // additive score penalty in 429 window
    public const string KeyBreaker429WindowS = "breaker_429_window_s";    // how long a 429 de-weights
    public const string KeyInFlightPenaltyMs = "lb_inflight_penalty_ms";  // additive score penalty per in-flight req
    public const string KeyEwmaDecayS = "lb_ewma_decay_s";                // EWMA time-decay τ to baseline
    public const string KeyLbStickyFactor = "lb_sticky_factor";           // hysteresis: stay on last service unless score > best × factor
    public const string KeyRateLimitEnabled = "rl_enabled";               // master switch for LimitConfig

    // Default upstream request timeout when a service has no TimeoutSeconds set.
    // The named HttpClient has no timeout of its own, so this is the only bound.
    public const string KeyRequestTimeoutDefaultS = "request_timeout_default_s";

    // Admin token protecting /admin/*. Empty = loopback-only zero-friction mode
    // (loopback requests pass, remote requests are rejected outright).
    public const string KeyAdminToken = "admin_token";

    // Proactive upstream health probing (background service, hot-read).
    public const string KeyHealthProbeEnabled = "health_probe_enabled";
    public const string KeyHealthProbeIntervalS = "health_probe_interval_s";

    // Usage-log retention — how many days of UsageLog rows to keep; the
    // UsageService background cleanup deletes anything older.
    public const string KeyUsageLogRetentionDays = "usage_log_retention_days";

    // Gateway-side web search. Global Tavily API key used when a service's
    // WebSearchConfig.ApiKey is empty.
    public const string KeyWebSearchTavilyKey = "web_search_tavily_key";

    // Self-update source. Empty = official GitHub Releases (geekwind/YuSwitch).
    // Overridable for mirrors or local testing; must point at a JSON document
    // shaped like the GitHub releases/latest payload.
    public const string KeyUpdateBaseUrl = "update_base_url";

    public const string DefaultAppName = "禹枢";
    public const string DefaultSubtitle = "AI 网关";

    private readonly IDbContextFactory<AppDbContext> _dbf;
    private volatile Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AppSettingsService(IDbContextFactory<AppDbContext> dbf) => _dbf = dbf;

    /// <summary>Raised after settings are saved, so open circuits refresh the shell.</summary>
    public event Action? Changed;

    public string AppName => Get(KeyAppName, DefaultAppName);
    public string Subtitle => Get(KeySubtitle, DefaultSubtitle);

    /// <summary>Logo rendering mode: auto (monogram), emoji, or image (data URI).</summary>
    public string LogoType => Get(KeyLogoType, "auto");
    public string LogoValue => Get(KeyLogoValue, "");

    /// <summary>Bind host for Kestrel (localhost / 127.0.0.1 / 0.0.0.0 / + / IP). Falls back to localhost.</summary>
    public string ListenHost
    {
        get
        {
            var h = Get(KeyListenHost, DefaultListenHost)?.Trim();
            return string.IsNullOrEmpty(h) ? DefaultListenHost : h;
        }
    }

    /// <summary>Bind port (1..65535). Falls back to 5078 on any parse/range failure.</summary>
    public int ListenPort
    {
        get
        {
            var raw = Get(KeyListenPort, DefaultListenPort.ToString());
            return int.TryParse(raw, out var p) && p is > 0 and <= 65535 ? p : DefaultListenPort;
        }
    }

    // --- Adaptive LB tunables (typed getters, same Get+TryParse+fallback pattern) ---
    public double LbEwmaAlpha => GetD(KeyLbEwmaAlpha, 0.3, 0.01, 1.0);
    public double LbColdStartMs => GetD(KeyLbColdStartMs, 500, 1, 600_000);
    public int BreakerFailureThreshold => GetI(KeyBreakerFailureThreshold, 3, 1, 100);
    public int BreakerCooldownBaseS => GetI(KeyBreakerCooldownBaseS, 10, 1, 3600);
    public double Breaker429PenaltyMs => GetD(KeyBreaker429PenaltyMs, 400.0, 0, 1_000_000);
    public int Breaker429WindowS => GetI(KeyBreaker429WindowS, 30, 1, 3600);
    public double InFlightPenaltyMs => GetD(KeyInFlightPenaltyMs, 500.0, 0, 1_000_000);
    public double EwmaDecayS => GetD(KeyEwmaDecayS, 4.0, 0.1, 3600);
    /// <summary>Sticky hysteresis: keep routing to the last-used service while its
    /// score ≤ best × factor. Avoids per-request channel flapping that discards
    /// upstream prompt caches. 1.0 = off (pure best-score + RR). Default 2.0.</summary>
    public double LbStickyFactor => GetD(KeyLbStickyFactor, 2.0, 1.0, 100.0);
    public bool RateLimitEnabled => GetB(KeyRateLimitEnabled, true);

    /// <summary>Fallback upstream timeout (seconds) when a service's LimitConfig has none.</summary>
    public int RequestTimeoutDefaultS => GetI(KeyRequestTimeoutDefaultS, 120, 5, 3600);

    /// <summary>Admin token guarding /admin/*. Empty = not set (loopback-only mode).</summary>
    public string AdminToken => Get(KeyAdminToken, "");

    /// <summary>Proactive health probing of enabled services (default off — costs
    /// upstream requests when the provider has no free /models endpoint).</summary>
    public bool HealthProbeEnabled => GetB(KeyHealthProbeEnabled, false);
    public int HealthProbeIntervalS => GetI(KeyHealthProbeIntervalS, 300, 30, 86400);

    /// <summary>How many days of usage logs to retain (1..365). Rows older than
    /// this are deleted by the UsageService hourly cleanup.</summary>
    public int UsageLogRetentionDays => GetI(KeyUsageLogRetentionDays, 30, 1, 365);

    /// <summary>Global Tavily API key for gateway-side web search. Empty = the
    /// gateway still strips web_search tools but performs no search.</summary>
    public string WebSearchTavilyKey => Get(KeyWebSearchTavilyKey, "");

    /// <summary>Self-update source URL. Empty = official GitHub Releases.</summary>
    public string UpdateBaseUrl => Get(KeyUpdateBaseUrl, "");

    private double GetD(string key, double fallback, double min, double max) =>
        double.TryParse(Get(key, ""), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v : fallback;

    private int GetI(string key, int fallback, int min, int max) =>
        int.TryParse(Get(key, ""), out var v) && v >= min && v <= max ? v : fallback;

    private bool GetB(string key, bool fallback)
    {
        var raw = Get(key, "");
        return string.IsNullOrWhiteSpace(raw) ? fallback
            : raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    public string Get(string key, string fallback = "") =>
        _cache.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var all = await db.Settings.AsNoTracking().ToListAsync(ct);
        _cache = all.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
            db.Settings.Add(new SettingEntity { Key = key, Value = value ?? "" });
        else
            existing.Value = value ?? "";
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        Changed?.Invoke();
    }
}
