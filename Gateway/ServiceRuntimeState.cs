using System.Collections.Concurrent;
using YuSwitch.Data.Entities;

namespace YuSwitch.Gateway;

/// <summary>
/// Per-service runtime state for adaptive load balancing: in-flight count,
/// EWMA latency, a circuit breaker, a concurrency semaphore, and QPS/QPM
/// token buckets. Single-instance deployment → this in-memory state is exact
/// (no distributed consistency problem). Instances are created lazily and live
/// for the process lifetime (see <see cref="ServiceStateTable"/>).
/// </summary>
public sealed class ServiceRuntimeState
{
    public int ServiceId { get; }

    public ServiceRuntimeState(int serviceId) => ServiceId = serviceId;

    // --- in-flight (adaptive scoring + concurrency) ---
    private int _inFlight;
    public int InFlight => Volatile.Read(ref _inFlight);

    // --- EWMA of TTFT (stream) / latency (non-stream), ms ---
    private double _ewmaMs;
    private bool _ewmaInit;
    private int _lastSampleMs;                      // Environment.TickCount of last observation
    private readonly object _ewmaLock = new();

    // --- circuit breaker (two states: Closed / Open-with-cooldown) ---
    // After Threshold consecutive hard failures the service is excluded for an
    // exponential cooldown (base, 3×base, 6×base, capped). The first selection
    // attempt after cooldown acts as the half-open probe; OnSuccess closes it,
    // ClassifyFailure re-opens with a longer cooldown.
    private int _consecFailures;
    private int _openUntilMs;                       // Environment.TickCount-based
    private int _openCount;                         // consecutive OPEN episodes → longer cooldown

    // --- 429 soft de-weight ---
    private int _last429Ms;                         // Environment.TickCount; 0 = none

    // --- rate limiting (lazily created, recreated on config change) ---
    private SemaphoreSlim? _concSem;
    private int _concCap = -1;
    private readonly object _semLock = new();
    private TokenBucket? _qpsBucket;
    private double _qpsVal = -1;
    private TokenBucket? _qpmBucket;
    private double _qpmVal = -1;
    private readonly object _bucketLock = new();

    private static int NowMs() => Environment.TickCount; // wraps ~49d; diffs are fine

    // ==== adaptive scoring ====

    /// <summary>Adaptive score. Lower = better. Design: an idle/unmeasured service sits AT
    /// the cold-start baseline, and a measured-fast service is PULLED UP to the baseline
    /// too — so all idle/healthy services share the same score and rotate round-robin
    /// (probing each). Only a measured-SLOW service keeps a latency above the baseline and
    /// thus yields traffic. The above-baseline part DECAYS back toward the baseline with
    /// time constant EwmaDecayS, so one slow sample can't pin a recovered-but-idle service
    /// out of rotation forever. An in-flight request adds an additive penalty to spread
    /// concurrent load, and a recent 429 adds a fixed penalty (de-weight, not exclude).</summary>
    public double Score(int weight, LbConfig cfg)
    {
        double ewma = DecayedEwma(cfg);
        double penalty = InFlight * cfg.InFlightPenaltyMs;
        var last429 = Volatile.Read(ref _last429Ms);
        if (last429 != 0 && NowMs() - last429 < cfg.Soft429WindowMs)
            penalty += cfg.Soft429PenaltyMs;
        return ewma / Math.Max(1, weight) + penalty;
    }

    /// <summary>Read-time view of the EWMA with lazy exponential decay toward the
    /// cold-start baseline: baseline + (ewma − baseline)·e^(−elapsed/τ). Nothing is
    /// written back — ObserveSample materialises the same decay before folding in a
    /// new sample, so both paths agree. Always ≥ baseline (idle clamp preserved).</summary>
    private double DecayedEwma(LbConfig cfg)
    {
        lock (_ewmaLock)
        {
            if (!_ewmaInit) return cfg.ColdStartMs;
            return Math.Max(cfg.ColdStartMs, DecayedEwmaLocked(cfg));
        }
    }

    // Must be called under _ewmaLock. Raw decayed value, no baseline clamp.
    private double DecayedEwmaLocked(LbConfig cfg)
    {
        if (cfg.EwmaDecayS <= 0) return _ewmaMs;
        var elapsedS = Math.Max(0, NowMs() - _lastSampleMs) / 1000.0;
        return cfg.ColdStartMs + (_ewmaMs - cfg.ColdStartMs) * Math.Exp(-elapsedS / cfg.EwmaDecayS);
    }

    /// <summary>Record a latency/TTFT observation into the EWMA (first sample seeds it).
    /// The stored EWMA is first decayed to "now" so a long-idle service doesn't blend a
    /// fresh sample against a stale high value the scorer no longer believes in.</summary>
    public void ObserveSample(double ms, LbConfig cfg)
    {
        lock (_ewmaLock)
        {
            if (!_ewmaInit) { _ewmaMs = ms; _ewmaInit = true; }
            else _ewmaMs = cfg.Alpha * ms + (1 - cfg.Alpha) * DecayedEwmaLocked(cfg);
            Volatile.Write(ref _lastSampleMs, NowMs());
        }
    }

    // ==== circuit breaker ====

    /// <summary>True if the service may receive traffic now (breaker not cooling down).</summary>
    public bool IsAvailable(LbConfig cfg)
    {
        var until = Volatile.Read(ref _openUntilMs);
        return until == 0 || NowMs() >= until;
    }

    public bool BreakerOpen => Volatile.Read(ref _openUntilMs) != 0 && NowMs() < Volatile.Read(ref _openUntilMs);

    /// <summary>A successful call closes the breaker and resets the failure/cooldown ladders.</summary>
    public void OnSuccess()
    {
        Interlocked.Exchange(ref _consecFailures, 0);
        Interlocked.Exchange(ref _openCount, 0);
        Interlocked.Exchange(ref _openUntilMs, 0);
    }

    /// <summary>A hard failure (exception/timeout/5xx) increments the streak and,
    /// past the threshold, opens the breaker for an exponential cooldown.</summary>
    public void OnHardFailure(LbConfig cfg)
    {
        if (Interlocked.Increment(ref _consecFailures) >= cfg.BreakerThreshold)
        {
            var n = Interlocked.Increment(ref _openCount);
            var mult = n switch { <= 1 => 1, 2 => 3, _ => 6 };      // 1×, 3×, 6× base
            var cd = Math.Min(cfg.BreakerCooldownBaseMs * mult, 300_000);
            Interlocked.Exchange(ref _openUntilMs, NowMs() + cd);
        }
    }

    /// <summary>A 429 (rate-limited) is a soft signal: de-weight for a window, never exclude.</summary>
    public void OnSoft429() => Interlocked.Exchange(ref _last429Ms, NowMs());

    // ==== rate limiting + in-flight lifecycle ====

    /// <summary>Try to enter: take a concurrency slot (if capped), a QPS token and a
    /// QPM token (if configured), then count in-flight. All-or-nothing — any
    /// refusal releases what was taken. Caller MUST pair a true result with Exit().</summary>
    public bool TryEnter(LimitConfig lim)
    {
        // Concurrency cap (≤0 = unlimited).
        if (lim.Concurrency > 0)
        {
            var sem = GetOrCreateSem((int)lim.Concurrency);
            if (!sem.Wait(0)) return false;                          // at cap → excluded, not queued
        }
        if (lim.Qps > 0 && !GetQpsBucket(lim.Qps).TryTake()) { ReleaseConcurrency(); return false; }
        if (lim.Qpm > 0 && !GetQpmBucket(lim.Qpm).TryTake()) { ReleaseConcurrency(); return false; }
        Interlocked.Increment(ref _inFlight);
        return true;
    }

    /// <summary>Release the in-flight slot + concurrency semaphore. Must be called
    /// exactly once per successful TryEnter, on every exit path (finally).</summary>
    public void Exit()
    {
        Interlocked.Decrement(ref _inFlight);
        ReleaseConcurrency();
    }

    private void ReleaseConcurrency() => _concSem?.Release();        // null-safe (uncapped → no-op)

    /// <summary>Non-destructive peek: is a QPS token available? Used to filter
    /// candidates; the real consume happens on the chosen attempt's TryEnter.</summary>
    public bool PeekQps(LimitConfig lim) => GetQpsBucket(lim.Qps).CanTake();

    /// <summary>Non-destructive peek: is a QPM token available?</summary>
    public bool PeekQpm(LimitConfig lim) => GetQpmBucket(lim.Qpm).CanTake();

    private SemaphoreSlim GetOrCreateSem(int cap)
    {
        if (_concSem is { } s && _concCap == cap) return s;
        lock (_semLock)
        {
            if (_concSem is { } s2 && _concCap == cap) return s2;
            var n = new SemaphoreSlim(cap, cap);
            _concSem = n; _concCap = cap;
            return n;
        }
    }

    private TokenBucket GetQpsBucket(double qps)
    {
        if (_qpsBucket is { } b && _qpsVal == qps) return b;
        lock (_bucketLock)
        {
            if (_qpsBucket is { } b2 && _qpsVal == qps) return b2;
            var n = new TokenBucket(qps, qps);                       // rate qps/s, burst qps
            _qpsBucket = n; _qpsVal = qps;
            return n;
        }
    }

    private TokenBucket GetQpmBucket(double qpm)
    {
        if (_qpmBucket is { } b && _qpmVal == qpm) return b;
        lock (_bucketLock)
        {
            if (_qpmBucket is { } b2 && _qpmVal == qpm) return b2;
            var n = new TokenBucket(qpm, qpm / 60.0);                // rate qpm/min, burst qpm
            _qpmBucket = n; _qpmVal = qpm;
            return n;
        }
    }

    // ==== observability ====

    public ServiceStateSnapshot Snapshot(LbConfig cfg, int weight)
    {
        double ewma = DecayedEwma(cfg);
        var until = Volatile.Read(ref _openUntilMs);
        return new ServiceStateSnapshot(
            ServiceId, InFlight, (long)Math.Round(ewma), Math.Round(Score(weight, cfg), 1),
            BreakerOpen, Volatile.Read(ref _consecFailures), Volatile.Read(ref _openCount),
            until != 0 && NowMs() < until ? Math.Max(0, until - NowMs()) : 0);
    }
}

/// <summary>Live per-service snapshot for /admin/service-state.</summary>
public record ServiceStateSnapshot(
    int ServiceId, int InFlight, long EwmaMs, double Score,
    bool BreakerOpen, int ConsecutiveFailures, int OpenCount, int CooldownRemainingMs);

/// <summary>
/// Refill-on-read token bucket. Capacity = burst size, rate = tokens/sec.
/// Thread-safe via a private lock (contention is negligible at gateway scale).
/// </summary>
internal sealed class TokenBucket
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
        _tokens = _capacity;                                         // start full
        _lastTicks = Environment.TickCount64;
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        var elapsedSec = (now - _lastTicks) / 1000.0;
        _lastTicks = now;
        _tokens = Math.Min(_capacity, _tokens + elapsedSec * _ratePerSec);
    }

    /// <summary>Consume one token if available.</summary>
    public bool TryTake()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1.0) { _tokens -= 1.0; return true; }
            return false;
        }
    }

    /// <summary>Peek whether a token is available without consuming (used to filter
    /// candidates; the real consume happens on the chosen attempt's TryEnter).</summary>
    public bool CanTake()
    {
        lock (_lock) { Refill(); return _tokens >= 1.0; }
    }
}

/// <summary>Process-wide table of per-service runtime state, keyed by service Id.
/// Construction is side-effect-free so a GetOrAdd valueFactory race is harmless.</summary>
public sealed class ServiceStateTable
{
    private readonly ConcurrentDictionary<int, ServiceRuntimeState> _map = new();
    public ServiceRuntimeState Get(int serviceId) => _map.GetOrAdd(serviceId, id => new ServiceRuntimeState(id));
    public IEnumerable<ServiceRuntimeState> All() => _map.Values;
}

/// <summary>Tunable knobs for adaptive LB / breaker / rate limiting (hot-reloaded
/// from AppSettingsService each dispatch — reads are cheap dictionary lookups).</summary>
public readonly record struct LbConfig(
    double Alpha,
    double ColdStartMs,
    int BreakerThreshold,
    int BreakerCooldownBaseMs,
    double Soft429PenaltyMs,      // additive score penalty while in the recent-429 window
    int Soft429WindowMs,
    double InFlightPenaltyMs,     // additive score penalty per in-flight request
    double EwmaDecayS,            // EWMA time-decay τ back toward the cold-start baseline
    bool RateLimitEnabled);
