using System.Collections.Concurrent;
using YuSwitch.Data.Entities;
using YuSwitch.Models;
using YuSwitch.Providers;

namespace YuSwitch.Services;

/// <summary>
/// Proactive upstream health probing. Without it a dead channel is only
/// discovered when a real request hits it (request-driven breaker); with it
/// long-idle channels get flagged before user traffic pays the price.
///
/// Probe strategy: GET /models when the provider supports listing (free, no
/// token cost); otherwise a 1-token chat completion. Results are kept
/// in-memory and joined into /admin/service-state for the dashboard.
/// Enabled/interval are hot-read from settings each cycle.
/// </summary>
public class HealthProbeService : BackgroundService
{
    private readonly ConfigService _config;
    private readonly IProviderRegistry _registry;
    private readonly AppSettingsService _settings;
    private readonly RealtimeNotificationService? _notifications;
    private readonly ILogger<HealthProbeService> _log;

    public record ProbeResult(bool Ok, long LatencyMs, string? Error, DateTime At);

    private static readonly ConcurrentDictionary<int, ProbeResult> _results = new();

    /// <summary>Latest probe result per service id (empty until first cycle).</summary>
    public static IReadOnlyDictionary<int, ProbeResult> Results => _results;

    public HealthProbeService(ConfigService config, IProviderRegistry registry,
        AppSettingsService settings, ILogger<HealthProbeService> log,
        RealtimeNotificationService? notifications = null)
    {
        _config = config; _registry = registry; _settings = settings;
        _log = log; _notifications = notifications;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let startup finish before the first probe round.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var intervalS = _settings.HealthProbeIntervalS;
            if (_settings.HealthProbeEnabled)
            {
                try { await ProbeAllAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "health probe cycle failed"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalS), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeAllAsync(CancellationToken ct)
    {
        var services = _config.Snapshot.Services.Where(s => s.Enabled).ToList();
        // Drop results for services that no longer exist / are disabled.
        foreach (var stale in _results.Keys.Except(services.Select(s => s.Id)).ToList())
            _results.TryRemove(stale, out _);

        // Sequential on purpose: probing is background hygiene, not a load test.
        foreach (var svc in services)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeOneAsync(svc, ct);
            _results[svc.Id] = result;
            if (!result.Ok)
                _log.LogWarning("health probe FAILED for {Svc}: {Err}", svc.Name, result.Error);
        }
        _notifications?.BroadcastImmediate("service-state", new());
    }

    private async Task<ProbeResult> ProbeOneAsync(ServiceEntity svc, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var provider = _registry.Create(svc);
            if (provider is IModelListable listable)
            {
                // Free probe: upstream /models answers ⇒ endpoint + key valid.
                await listable.ListModelsAsync(cts.Token);
            }
            else
            {
                var mdl = _config.Snapshot.Models.FirstOrDefault(m => m.ServiceId == svc.Id && m.Enabled);
                if (mdl is null)
                    return new ProbeResult(true, 0, null, DateTime.Now); // nothing to probe with
                var req = new ChatRequest
                {
                    Model = mdl.ResolveUpstreamModel(),
                    ClientModel = mdl.ModelName,
                    MaxTokens = 1,
                    Messages = new() { new() { Role = "user", Content = "hi" } },
                };
                await provider.ChatAsync(req, cts.Token);
            }
            return new ProbeResult(true, sw.ElapsedMilliseconds, null, DateTime.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, sw.ElapsedMilliseconds, "probe timeout (15s)", DateTime.Now);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            return new ProbeResult(false, sw.ElapsedMilliseconds,
                msg[..Math.Min(msg.Length, 200)], DateTime.Now);
        }
    }
}
