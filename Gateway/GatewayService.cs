using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YuSwitch.Data.Entities;
using YuSwitch.Models;
using YuSwitch.Providers;
using YuSwitch.Providers.OpenAI;
using YuSwitch.Services;

namespace YuSwitch.Gateway;

/// <summary>
/// Core gateway orchestrator: given a chat request, resolve candidate
/// services, build providers, and execute with failover (retry on the same
/// provider, then fall back to the next candidate service). Replaces the
/// legacy Go dispatchToServiceHandler + the "failure = 500" behavior.
/// </summary>
public class GatewayService
{
    private readonly IProviderRegistry _registry;
    private readonly ConfigService _config;
    private readonly UsageService _usage;
    private readonly AppSettingsService _settings;
    private readonly WebSearchService _webSearch;
    private readonly ILogger<GatewayService> _log;
    private readonly RealtimeNotificationService? _notifications;

    // In-memory dispatch trace (ring buffer of last 64 dispatches) for
    // testing/observability — exposes which service handled each request,
    // keyed by session id. Read via /admin/dispatch-trace.
    private static readonly Queue<DispatchTrace> _trace = new();
    private const int TraceLimit = 64;

    // Per-(model, priority-tier) round-robin counter for weighted round-robin
    // load balancing. Survives across requests within the process so traffic
    // strictly alternates across same-priority services (ABAB for equal
    // weights, AAAB for weight 3 vs 1). Single-instance: sufficient and exact;
    // under multi-instance deployment each instance rotates independently
    // (still distributes overall, just not globally strict). Retained as the
    // tie-breaker when adaptive scores are exactly equal (e.g. cold start).
    private static readonly ConcurrentDictionary<string, int> _rrCounters = new();

    // Last winning service per model|tier — the stickiness anchor. Reads/writes
    // are racy-by-design (worst case: one extra switch), no lock needed.
    private static readonly ConcurrentDictionary<string, int> _lastPicked = new();

    // Per-service runtime state (in-flight, EWMA latency, circuit breaker,
    // concurrency semaphore, QPS/QPM buckets) for adaptive load balancing.
    // static + single-instance → exact, no DI needed.
    private static readonly ServiceStateTable _states = new();

    public static IReadOnlyList<DispatchTrace> DispatchTraces
    {
        get { lock (_trace) { return _trace.ToList(); } }
    }

    /// <summary>Live per-service adaptive-LB snapshot for /admin/service-state.
    /// Iterates the CONFIG services (not just the lazily-created runtime table)
    /// so services with no traffic yet still show up, joined with the latest
    /// proactive health-probe result when probing is enabled.</summary>
    public IReadOnlyList<object> ServiceStates()
    {
        var lb = ReadLbConfig();
        var probes = HealthProbeService.Results;
        return _config.Snapshot.Services
            .Where(s => s.Enabled)
            .Select(svc =>
            {
                var snap = _states.Get(svc.Id).Snapshot(lb, svc.Weight);
                probes.TryGetValue(svc.Id, out var probe);
                return (object)new
                {
                    serviceId = svc.Id,
                    name = svc.Name,
                    weight = svc.Weight,
                    inFlight = snap.InFlight,
                    ewmaMs = snap.EwmaMs,
                    score = snap.Score,
                    breakerOpen = snap.BreakerOpen,
                    consecutiveFailures = snap.ConsecutiveFailures,
                    openCount = snap.OpenCount,
                    cooldownRemainingMs = snap.CooldownRemainingMs,
                    probeOk = probe?.Ok,
                    probeLatencyMs = probe?.LatencyMs,
                    probeError = probe?.Error,
                    probeAt = probe?.At,
                };
            }).ToList();
    }

    private static LbConfig ReadLbConfigStatic(AppSettingsService s) => new(
        Alpha: s.LbEwmaAlpha,
        ColdStartMs: s.LbColdStartMs,
        BreakerThreshold: s.BreakerFailureThreshold,
        BreakerCooldownBaseMs: s.BreakerCooldownBaseS * 1000,
        Soft429PenaltyMs: s.Breaker429PenaltyMs,
        Soft429WindowMs: s.Breaker429WindowS * 1000,
        InFlightPenaltyMs: s.InFlightPenaltyMs,
        EwmaDecayS: s.EwmaDecayS,
        StickyFactor: s.LbStickyFactor,
        RateLimitEnabled: s.RateLimitEnabled);

    private static void RecordTrace(string model, string? session, string serviceName, int priority, int weight, bool success)
    {
        lock (_trace)
        {
            _trace.Enqueue(new DispatchTrace(DateTime.Now, model, session ?? "", serviceName, priority, weight, success));
            while (_trace.Count > TraceLimit) _trace.Dequeue();
        }
    }

    public GatewayService(IProviderRegistry registry, ConfigService config,
        UsageService usage, AppSettingsService settings, WebSearchService webSearch,
        ILogger<GatewayService> log, RealtimeNotificationService? notifications = null)
    {
        _registry = registry; _config = config; _usage = usage; _settings = settings;
        _webSearch = webSearch; _log = log; _notifications = notifications;
    }

    private LbConfig ReadLbConfig() => ReadLbConfigStatic(_settings);

    public async Task<ChatResponse> ChatAsync(ChatRequest req, string apiKeyName, CancellationToken ct)
    {
        var candidates = ResolveCandidates(req.Model, req.SessionId);
        if (candidates.Count == 0)
            throw new ModelNotFoundException(req.Model);

        // Gateway-side web search: enrich exactly once, based on the first
        // (preferred) candidate's service config. Failover candidates reuse the
        // already-enriched request, so the search runs once per client request.
        if (!req.SearchHandled)
        {
            await _webSearch.EnrichAsync(req, candidates[0].Service, ct);
            req.SearchHandled = true;
        }

        // simulate (non-streaming) does a two-round function call; streaming
        // simulate already fell back to inject inside EnrichAsync.
        if (req.WebSearch?.Mode == "simulate")
            return await SimulateSearchAsync(req, apiKeyName, candidates, ct);

        return await ChatOnceAsync(req, apiKeyName, candidates, ct);
    }

    private async Task<ChatResponse> ChatOnceAsync(ChatRequest req, string apiKeyName,
        List<(ServiceEntity Service, ModelEntity Model)> candidates, CancellationToken ct)
    {
        // Key ResolveCandidates was called with — ApplyRedirect mutates req.Model
        // per attempt, so capture it now for the sticky-anchor update on success.
        var stickyModel = req.Model;
        Exception? lastErr = null;
        foreach (var (svc, model) in candidates)
        {
            var st = _states.Get(svc.Id);
            var lim = svc.GetLimit();
            // Acquire the in-flight/concurrency/rate slot for this attempt. If the
            // service just hit its cap between selection and now, skip to the next
            // candidate rather than queue. Exit() releases it in the finally below.
            if (!st.TryEnter(lim)) continue;

            var provider = _registry.Create(svc);
            ApplyRedirect(req, svc, model);
            _log.LogInformation("ChatAsync dispatch: model={Model} session={Session} -> service={Svc} upstream={Up} (pri={Pri} w={W})",
                req.ClientModel, req.SessionId ?? "-", svc.Name, req.Model, svc.Priority, svc.Weight);
            var sw = StopwatchStart();
            // The upstream HttpClient has NO timeout of its own, so the gateway
            // always bounds the call: per-service TimeoutSeconds, else the
            // global default (Settings).
            var effTimeoutS = lim.TimeoutSeconds > 0 ? lim.TimeoutSeconds : _settings.RequestTimeoutDefaultS;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(effTimeoutS));
            var effCt = timeoutCts.Token;
            try
            {
                var resp = await provider.ChatAsync(req, effCt);
                var respMsg = resp.Choices.FirstOrDefault()?.Message;
                // Thinking models may burn the whole token budget on reasoning
                // and return no content — log the reasoning so the call log
                // explains "success but empty response".
                var preview = !string.IsNullOrEmpty(respMsg?.Content)
                    ? respMsg!.Content
                    : !string.IsNullOrEmpty(respMsg?.ReasoningContent) ? "[思考] " + respMsg!.ReasoningContent : null;
                // Adaptive feedback: record latency into the EWMA and close the breaker.
                st.ObserveSample(ElapsedMs(sw), ReadLbConfig());
                st.OnSuccess();
                _lastPicked[$"{stickyModel}|{svc.Priority}"] = svc.Id;   // sticky anchor
                _usage.Record(BuildLog(svc, model, req, apiKeyName, resp.Usage, sw, true, 200, null,
                    responsePreview: preview));
                // Notify service state change (breaker closed)
                _notifications?.BroadcastImmediate("service-state", new() { Service = svc.Name });
                RecordTrace(req.ClientModel.Length > 0 ? req.ClientModel : req.Model, req.SessionId, svc.Name, svc.Priority, svc.Weight, true);
                return resp;
            }
            catch (Exception ex)
            {
                // The CLIENT disconnected/cancelled (outer ct) — the upstream may be
                // perfectly healthy. Don't count it toward the breaker and don't
                // failover with an already-dead token; just surface the cancellation.
                // (effCt-only cancellation = our per-service timeout → still Hard.)
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    throw;
                lastErr = ex;
                // Adaptive feedback: hard failures trip the breaker; 429 de-weights.
                var kind = Classify(ex);
                ApplyToBreaker(st, kind, ReadLbConfig());
                _log.LogWarning(ex, "provider {Type} failed for {Model} ({Kind})", svc.ProviderType, req.Model, kind);
                _usage.Record(BuildLog(svc, model, req, apiKeyName, null, sw, false, ex is UpstreamException ue ? (int)ue.StatusCode : 500, ex.Message));
                RecordTrace(req.ClientModel.Length > 0 ? req.ClientModel : req.Model, req.SessionId, svc.Name, svc.Priority, svc.Weight, false);
                // Notify service state change (breaker may have opened)
                _notifications?.BroadcastImmediate("service-state", new() { Service = svc.Name });
                // A caller error (400/422/... — NOT 401/403) is the CLIENT's
                // problem, not this upstream's health — retrying it against every
                // other upstream would just burn quota and latency. 401/403 are
                // AuthError and fall through to failover (a different account may
                // be authorized). Surface only CallerError immediately.
                if (kind == FailureKind.CallerError)
                    throw;
                // otherwise continue to next candidate (failover)
            }
            finally
            {
                st.Exit();   // always release the in-flight/concurrency slot
            }
        }
        throw lastErr ?? new ModelNotFoundException(req.Model);
    }

    /// <summary>Two-round web search simulation. Round 1 lets the upstream model
    /// decide to call the web_search function tool; when it does, the gateway
    /// runs the search itself and feeds the results back in round 2. Any other
    /// tool call (or no tool call) passes through untouched — the client
    /// executes its own tools. Each round records its own usage log row.</summary>
    private async Task<ChatResponse> SimulateSearchAsync(ChatRequest req, string apiKeyName,
        List<(ServiceEntity Service, ModelEntity Model)> candidates, CancellationToken ct)
    {
        var round1 = await ChatOnceAsync(req, apiKeyName, candidates, ct);
        var msg = round1.Choices.FirstOrDefault()?.Message;
        if (msg?.ToolCalls is { Count: > 0 } calls &&
            calls.All(c => c.Type == "function" && c.Function.Name == "web_search"))
        {
            var query = ParseQueryFromArgs(calls[0].Function.Arguments);
            var intent = req.WebSearch;
            if (!string.IsNullOrWhiteSpace(query) && intent is not null)
            {
                string results;
                try
                {
                    results = await _webSearch.SearchAsync(query, intent.MaxResults, intent.ApiKey ?? "", ct);
                }
                catch (Exception ex)
                {
                    // Hand the failure to the model so it can explain to the
                    // client instead of dying mid-conversation.
                    results = $"[web search failed: {ex.Message}]";
                }
                // Echo the assistant tool_call, then the tool result, so the
                // upstream sees a consistent function-calling conversation.
                req.Messages.Add(new ChatMessage { Role = "assistant", Content = null, ToolCalls = calls });
                req.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = calls[0].Id, Content = results });
                // ApplyRedirect rewrote req.Model to the upstream name in round 1;
                // restore the client alias so round 2 resolves cleanly.
                req.Model = req.ClientModel;
                return await ChatOnceAsync(req, apiKeyName, candidates, ct);
            }
        }
        return round1;
    }

    private static string ParseQueryFromArgs(string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            return doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest req, string apiKeyName, [EnumeratorCancellation] CancellationToken ct)
    {
        var candidates = ResolveCandidates(req.Model, req.SessionId);
        if (candidates.Count == 0)
            throw new ModelNotFoundException(req.Model);

        // Gateway-side web search, once per request (simulate falls back to
        // inject inside EnrichAsync, so streaming never does two rounds).
        if (!req.SearchHandled)
        {
            await _webSearch.EnrichAsync(req, candidates[0].Service, ct);
            req.SearchHandled = true;
        }

        // Key ResolveCandidates was called with — ApplyRedirect mutates req.Model
        // per attempt, so capture it now for the sticky-anchor update on success.
        var stickyModel = req.Model;
        var sw = StopwatchStart();
        Exception? lastErr = null;
        var lb = ReadLbConfig();

        // Phase 1: pre-stream failover. Try each candidate until one yields
        // its first chunk. We can only fall back BEFORE bytes are on the wire.
        // Once a first chunk is obtained, switch to straight enumeration (no
        // catch — errors after this point surface as stream-error events in
        // the endpoint layer).
        IAsyncEnumerator<StreamChunk>? iter = null;
        StreamChunk? firstChunk = null;
        ServiceEntity? activeSvc = null;
        ModelEntity? activeModel = null;
        ServiceRuntimeState? activeState = null;

        foreach (var (svc, model) in candidates)
        {
            var st = _states.Get(svc.Id);
            var lim = svc.GetLimit();
            // Acquire the in-flight/concurrency/rate slot for the winning provider.
            // Skip (don't queue) a service that hit its cap between selection and now.
            if (!st.TryEnter(lim)) continue;

            var provider = _registry.Create(svc);
            ApplyRedirect(req, svc, model);
            // The upstream HttpClient has no timeout of its own; this bounds the
            // wait for the FIRST chunk (per-service TimeoutSeconds, else the
            // global default). Once streaming, the client's own ct governs.
            var effTimeoutS = lim.TimeoutSeconds > 0 ? lim.TimeoutSeconds : _settings.RequestTimeoutDefaultS;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(effTimeoutS));
            var effCt = timeoutCts.Token;
            iter = provider.StreamAsync(req, effCt).GetAsyncEnumerator(effCt);
            try
            {
                // WaitAsync(effCt) lets the linked timeout cancel the first-chunk
                // wait even if the provider doesn't poll ct promptly.
                firstChunk = await iter.MoveNextAsync().AsTask().WaitAsync(effCt) ? iter.Current : null;
                activeSvc = svc;
                activeModel = model;
                activeState = st;
                // Adaptive feedback: record TTFT into the EWMA and close the breaker.
                var ttft0 = ElapsedMs(sw);
                st.ObserveSample(ttft0, lb);
                st.OnSuccess();
                _lastPicked[$"{stickyModel}|{svc.Priority}"] = svc.Id;   // sticky anchor
                // Notify service state change (breaker closed)
                _notifications?.BroadcastImmediate("service-state", new() { Service = svc.Name });
                break; // got a first chunk (or clean empty), use this provider
            }
            catch (Exception ex)
            {
                // The CLIENT disconnected/cancelled (outer ct) — not an upstream
                // failure. Release the slot, dispose the attempt, and surface the
                // cancellation without breaker credit or failover (every remaining
                // candidate would instantly "fail" on the same dead token).
                // (effCt-only cancellation = our first-chunk timeout → still Hard.)
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    st.Exit();
                    await iter.DisposeAsync();
                    iter = null;
                    throw;
                }
                lastErr = ex;
                var kind = Classify(ex);
                ApplyToBreaker(st, kind, lb);
                st.Exit();                                  // release the slot for the failed attempt
                _log.LogWarning(ex, "stream provider {Type} failed pre-stream for {Model} ({Kind})", svc.ProviderType, req.Model, kind);
                // Notify service state change (breaker may have opened)
                _notifications?.BroadcastImmediate("service-state", new() { Service = svc.Name });
                _usage.Record(BuildLog(svc, model, req, apiKeyName, null, sw, false,
                    ex is UpstreamException ue ? (int)ue.StatusCode : 500, ex.Message));
                RecordTrace(req.ClientModel.Length > 0 ? req.ClientModel : req.Model, req.SessionId, svc.Name, svc.Priority, svc.Weight, false);
                await iter.DisposeAsync();
                iter = null;
                // A caller error (400/422/... — NOT 401/403) is the client's
                // problem — don't replay it against the remaining upstreams.
                // 401/403 are AuthError and fall through to failover.
                if (kind == FailureKind.CallerError)
                    throw;
                // loop to next candidate
            }
        }

        if (iter is null || activeSvc is null || activeState is null)
            throw lastErr ?? new ModelNotFoundException(req.Model);

        // Phase 2: stream through. The in-flight slot for the winning provider was
        // acquired in Phase 1; it MUST be released when the stream ends for ANY
        // reason — normal end, consumer break/disconnect, or exception. yield is
        // illegal inside try-with-catch (CS1626) but legal inside try-with-finally,
        // and await is allowed in the finally of an async iterator. So all yields
        // sit in one try whose finally releases the slot and disposes the enumerator.
        Usage? usage = null;
        long ttft = ElapsedMs(sw);   // first chunk already obtained → TTFT known
        var respPreview = new System.Text.StringBuilder(256);
        var reasoningPreview = new System.Text.StringBuilder(256);
        try
        {
            if (firstChunk is not null)
            {
                if (firstChunk.Usage is { } u1) usage = u1;
                AccumulatePreview(respPreview, reasoningPreview, firstChunk);
                yield return firstChunk;
            }
            while (await iter.MoveNextAsync())
            {
                var chunk = iter.Current;
                if (chunk.Usage is { } u2) usage = u2;
                AccumulatePreview(respPreview, reasoningPreview, chunk);
                yield return chunk;
            }
        }
        finally
        {
            activeState.Exit();          // guaranteed release on every exit path
            await iter.DisposeAsync();   // guaranteed dispose (was clean-end-only before)
        }
        // Thinking models may produce only reasoning (content empty) — fall
        // back to the reasoning text so the call log explains the empty body.
        var finalPreview = respPreview.Length > 0
            ? respPreview.ToString()
            : reasoningPreview.Length > 0 ? "[思考] " + reasoningPreview : "";
        _usage.Record(BuildLog(activeSvc, activeModel!, req, apiKeyName, usage, sw, true, 200, null,
            ttftMs: ttft, responsePreview: finalPreview));
    }

    private static void AccumulatePreview(System.Text.StringBuilder content,
        System.Text.StringBuilder reasoning, StreamChunk chunk)
    {
        if (chunk.Choices is null) return;
        foreach (var c in chunk.Choices)
        {
            if (content.Length < 256 && !string.IsNullOrEmpty(c.Delta?.Content))
                content.Append(c.Delta.Content);
            if (reasoning.Length < 256 && !string.IsNullOrEmpty(c.Delta?.ReasoningContent))
                reasoning.Append(c.Delta.ReasoningContent);
        }
        if (content.Length > 256) content.Length = 256;
        if (reasoning.Length > 256) reasoning.Length = 256;
    }

    /// <summary>
    /// Resolve candidate services for a model, ordered by:
    ///  - Priority ascending (lower = higher precedence; tried first as the
    ///    primary tier, higher-priority tiers are failover backups).
    ///  - Within the same priority tier, the PRIMARY candidate is chosen by
    ///    ADAPTIVE SCORING: filter out services whose breaker is cooling or whose
    ///    concurrency/rate capacity is exhausted, then score the rest by
    ///    EWMA(latency/TTFT) × (in-flight+1) ÷ Weight and pick the minimum. A
    ///    busy/slow service scores worse and yields traffic to its peers — this
    ///    is what makes concurrent requests spread by live load, not just config.
    ///    STICKY HYSTERESIS: the last service that succeeded for this model|tier
    ///    keeps winning while its score ≤ best × LbStickyFactor, so healthy peers
    ///    don't flap per request (flapping discards upstream prompt caches).
    ///    If a SessionId is given (explicit X-Session-Id header only — clients'
    ///    user/user_id no longer trigger this), the primary is instead pinned by
    ///    a stable hash (sticky session affinity) and bypasses scoring.
    ///    Remaining candidates in the tier keep weight-desc order as failover.
    /// </summary>
    private List<(ServiceEntity Service, ModelEntity Model)> ResolveCandidates(string model, string? sessionId)
    {
        if (model.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            var all = _config.GetEnabledModelNames();
            return _config.FindServicesForModel(all.Count > 0 ? all[0] : "random");
        }

        var ordered = _config.FindServicesForModel(model);
        if (ordered.Count <= 1)
            return ordered;

        var lb = ReadLbConfig();

        // Group by priority tier; primary chosen per tier, rest as fallback.
        var result = new List<(ServiceEntity, ModelEntity)>();
        foreach (var tier in ordered.GroupBy(c => c.Item1.Priority).OrderBy(g => g.Key))
        {
            var tierList = tier.ToList();

            // Sticky session: deterministic hash pins the primary on the FULL tier
            // (scoring/rate-limit are advisory; an explicit session must not be
            // routed away by a momentarily-busy preferred service). Failover still
            // applies if the pinned service errors during the actual call.
            if (!string.IsNullOrEmpty(sessionId))
            {
                var pinnedIdx = StableHash(model, sessionId!) % tierList.Count;
                var pinned = tierList[pinnedIdx];
                tierList.RemoveAt(pinnedIdx);
                result.Add(pinned);
                result.AddRange(tierList.OrderByDescending(c => c.Item1.Weight));
                continue;
            }

            if (tierList.Count == 1)
            {
                result.AddRange(tierList);
                continue;
            }

            // --- FILTER: drop breaker-open (cooling) and capacity-exhausted services ---
            // Concurrency is filtered by comparing the live in-flight count against
            // the configured cap (non-destructive — the slot is only acquired when a
            // service is actually tried, in ChatAsync/StreamAsync). Rate buckets are
            // peeked non-destructively for the same reason.
            var eligible = new List<(ServiceEntity, ModelEntity)>(tierList.Count);
            foreach (var c in tierList)
            {
                var st = _states.Get(c.Item1.Id);
                if (!st.IsAvailable(lb)) continue;                     // breaker cooling
                if (lb.RateLimitEnabled)
                {
                    var lim = c.Item1.GetLimit();
                    if (lim.Concurrency > 0 && st.InFlight >= (int)lim.Concurrency) continue; // at cap
                    if (lim.Qps > 0 && !st.PeekQps(lim)) continue;     // QPS bucket empty
                    if (lim.Qpm > 0 && !st.PeekQpm(lim)) continue;     // QPM bucket empty
                }
                eligible.Add(c);
            }
            // If filtering emptied the tier, fall back to the full tier so we still
            // have failover candidates rather than a spurious "model not found".
            var usable = eligible.Count > 0 ? eligible : tierList;

            // --- SCORE: EWMA × (in-flight+1) ÷ weight, pick minimum; RR breaks ties ---
            var scores = new double[usable.Count];
            double best = double.MaxValue;
            var bestIdxs = new List<int>(usable.Count);
            for (int i = 0; i < usable.Count; i++)
            {
                scores[i] = _states.Get(usable[i].Item1.Id).Score(usable[i].Item1.Weight, lb);
                if (scores[i] < best - 1e-9) { best = scores[i]; bestIdxs.Clear(); bestIdxs.Add(i); }
                else if (Math.Abs(scores[i] - best) <= 1e-9) bestIdxs.Add(i);
            }
            int primaryIdx = -1;
            // --- STICKY: prefer the last service that SUCCEEDED for this model|tier
            // as long as its score is within StickyFactor× of the best. Switching
            // upstreams discards the provider-side prompt cache (slower TTFT, more
            // cost for agent clients that resend the whole conversation), so we
            // only move when the anchor is genuinely worse — slow, loaded, cooling
            // (filtered out of `usable` above) or at capacity. 1.0 disables.
            if (lb.StickyFactor > 1.0 && _lastPicked.TryGetValue($"{model}|{tier.Key}", out var lastId))
            {
                var anchorIdx = usable.FindIndex(c => c.Item1.Id == lastId);
                if (anchorIdx >= 0 && scores[anchorIdx] <= best * lb.StickyFactor)
                    primaryIdx = anchorIdx;
            }
            if (primaryIdx < 0)
            {
                if (bestIdxs.Count == 1) primaryIdx = bestIdxs[0];
                else
                {
                    // Exact tie (e.g. all cold at coldStart/weight): advance RR among the
                    // tied slots so a burst of identical scores rotates instead of pinning.
                    var n = _rrCounters.AddOrUpdate($"{model}|{tier.Key}", _ => 1, (_, old) => old + 1);
                    primaryIdx = bestIdxs[(n - 1) % bestIdxs.Count];
                }
            }

            var primary = usable[primaryIdx];
            usable.RemoveAt(primaryIdx);
            result.Add(primary);
            // fallbacks: weight desc
            result.AddRange(usable.OrderByDescending(c => c.Item1.Weight));
        }
        return result;
    }

    /// <summary>Route an embeddings request. Same candidate resolution and
    /// failover discipline as ChatAsync, filtered to models flagged
    /// SupportsEmbeddings and providers implementing IEmbeddingProvider.</summary>
    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest req, string clientModel, string apiKeyName, CancellationToken ct)
    {
        var candidates = ResolveCandidates(clientModel, sessionId: null)
            .Where(c => c.Model.SupportsEmbeddings)
            .ToList();
        if (candidates.Count == 0)
            throw new ModelNotFoundException(clientModel);

        Exception? lastErr = null;
        foreach (var (svc, model) in candidates)
        {
            var st = _states.Get(svc.Id);
            var lim = svc.GetLimit();
            if (!st.TryEnter(lim)) continue;

            var provider = _registry.Create(svc);
            if (provider is not IEmbeddingProvider embedder)
            {
                st.Exit();
                continue;
            }

            // Same alias→upstream + redirect chain as chat.
            req.Model = model.ResolveUpstreamModel();
            var redirects = svc.GetModelRedirects();
            var maps = svc.GetModelMap();
            if (redirects.TryGetValue(req.Model, out var r)) req.Model = r;
            if (maps.TryGetValue(req.Model, out var m2)) req.Model = m2;

            var sw = StopwatchStart();
            var effTimeoutS = lim.TimeoutSeconds > 0 ? lim.TimeoutSeconds : _settings.RequestTimeoutDefaultS;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(effTimeoutS));
            try
            {
                var resp = await embedder.EmbedAsync(req, timeoutCts.Token);
                st.ObserveSample(ElapsedMs(sw), ReadLbConfig());
                st.OnSuccess();
                resp.Model = clientModel;
                _usage.Record(new UsageLogEntity
                {
                    Model = clientModel,
                    UpstreamModel = req.Model,
                    ProviderType = svc.ProviderType,
                    ServiceName = svc.Name,
                    ApiKeyName = apiKeyName,
                    PromptTokens = resp.Usage?.PromptTokens ?? 0,
                    TotalTokens = resp.Usage?.TotalTokens ?? resp.Usage?.PromptTokens ?? 0,
                    LatencyMs = ElapsedMs(sw),
                    Success = true,
                    StatusCode = "200",
                    PromptPreview = Trunc($"[embeddings ×{req.Input.Count}] {req.Input.FirstOrDefault() ?? ""}", 256),
                    ResponsePreview = $"[{resp.Data.Count} vectors]",
                });
                _notifications?.BroadcastImmediate("service-state", new() { Service = svc.Name });
                return resp;
            }
            catch (Exception ex)
            {
                // Client cancelled (outer ct) — no breaker credit, no failover.
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    throw;
                lastErr = ex;
                var kind = Classify(ex);
                ApplyToBreaker(st, kind, ReadLbConfig());
                _log.LogWarning(ex, "embeddings provider {Type} failed for {Model} ({Kind})", svc.ProviderType, req.Model, kind);
                _usage.Record(new UsageLogEntity
                {
                    Model = clientModel,
                    UpstreamModel = req.Model,
                    ProviderType = svc.ProviderType,
                    ServiceName = svc.Name,
                    ApiKeyName = apiKeyName,
                    LatencyMs = ElapsedMs(sw),
                    Success = false,
                    StatusCode = (ex is UpstreamException ue ? (int)ue.StatusCode : 500).ToString(),
                    Error = ex.Message[..Math.Min(ex.Message.Length, 1000)],
                    PromptPreview = Trunc($"[embeddings ×{req.Input.Count}]", 256),
                });
                if (kind == FailureKind.CallerError)
                    throw;
            }
            finally
            {
                st.Exit();
            }
        }
        throw lastErr ?? new ModelNotFoundException(clientModel);
    }

    /// <summary>How a provider failure should be treated by the breaker AND the
    /// failover loop — one classification, two consumers, so they can't drift.</summary>
    private enum FailureKind
    {
        /// <summary>5xx / network error / timeout: counts toward opening the breaker, failover continues.</summary>
        Hard,
        /// <summary>429: soft de-weight (score penalty), failover continues.</summary>
        Soft429,
        /// <summary>401/403: THIS service's credentials/account aren't allowed for
        /// the resource (e.g. team_model_access_denied, invalid key). Not the
        /// client's fault, and not "this upstream is down" — so failover continues
        /// (a different account may have access) but it does NOT count toward the
        /// breaker (the account is fine for other models; breaking it would steer
        /// traffic away for no reason).</summary>
        AuthError,
        /// <summary>Other 4xx (e.g. 400 bad request): the caller's fault — no breaker
        /// signal, and NO failover (replaying a bad request burns every upstream).</summary>
        CallerError,
    }

    private static FailureKind Classify(Exception ex) => ex switch
    {
        UpstreamException ue when (int)ue.StatusCode == 429 => FailureKind.Soft429,
        UpstreamException ue when (int)ue.StatusCode is 401 or 403 => FailureKind.AuthError,
        UpstreamException ue when ue.IsServerError => FailureKind.Hard,
        UpstreamException => FailureKind.CallerError,
        _ => FailureKind.Hard,                       // network error / timeout / cancel
    };

    private static void ApplyToBreaker(ServiceRuntimeState st, FailureKind kind, LbConfig lb)
    {
        switch (kind)
        {
            case FailureKind.Soft429: st.OnSoft429(); break;
            case FailureKind.Hard: st.OnHardFailure(lb); break;
            // AuthError / CallerError: no health signal — neither means the upstream is sick.
        }
    }

    /// <summary>Stable, portable hash (no Math.Random / time) for session affinity.</summary>
    private static int StableHash(string model, string sessionId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{model}|{sessionId}");
        var h = SHA256.HashData(bytes);
        // First 4 bytes as big-endian int32
        return Math.Abs((h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3]);
    }

    private static void ApplyRedirect(ChatRequest req, ServiceEntity svc, ModelEntity model)
    {
        // Per-service model name mapping: the client requested a unified alias
        // (e.g. "model-alias"); this service may serve it under a different real
        // upstream name (e.g. "model-name-A"). Resolve to the upstream name.
        // Then apply the service-level model_redirect/model_map (further alias
        // remapping) if configured.
        req.Model = model.ResolveUpstreamModel();

        var redirects = svc.GetModelRedirects();
        var maps = svc.GetModelMap();
        if (redirects.TryGetValue(req.Model, out var redirected))
            req.Model = redirected;
        if (maps.TryGetValue(req.Model, out var mapped))
            req.Model = mapped;
    }

    private static long StopwatchStart() => Stopwatch.GetTimestamp();
    private static long ElapsedMs(long start) =>
        (long)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

    private static UsageLogEntity BuildLog(ServiceEntity svc, ModelEntity model,
        ChatRequest req, string apiKeyName, Usage? usage, long start,
        bool success, int status, string? error, long ttftMs = 0,
        string? responsePreview = null) => new()
    {
        Model = req.ClientModel.Length > 0 ? req.ClientModel : req.Model,
        UpstreamModel = req.Model,
        ProviderType = svc.ProviderType,
        ServiceName = svc.Name,
        ApiKeyName = apiKeyName,
        PromptTokens = usage?.PromptTokens ?? 0,
        CompletionTokens = usage?.CompletionTokens ?? 0,
        TotalTokens = usage?.TotalTokens ?? 0,
        ReasoningTokens = usage?.ReasoningTokens ?? 0,
        CacheCreationTokens = usage?.CacheCreationInputTokens ?? 0,
        CacheReadTokens = usage?.CacheReadInputTokens ?? 0,
        CacheHit = (usage?.CacheReadInputTokens ?? 0) > 0,
        LatencyMs = ElapsedMs(start),
        TtftMs = ttftMs,
        Stream = req.Stream,
        Success = success,
        StatusCode = status.ToString(),
        Error = error is null ? "" : error[..Math.Min(error.Length, 1000)],
        SessionId = req.SessionId ?? "",
        PromptPreview = Trunc(PromptText(req), 256),
        ResponsePreview = Trunc(responsePreview ?? "", 256),
    };

    private static string PromptText(ChatRequest req)
    {
        // Concatenate last user message text for a compact preview.
        for (int i = req.Messages.Count - 1; i >= 0; i--)
            if (req.Messages[i].Role == "user")
                return req.Messages[i].Content ?? "";
        return req.Messages.FirstOrDefault()?.Content ?? "";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}

public class ModelNotFoundException : Exception
{
    public ModelNotFoundException(string model) : base($"model '{model}' not found or not enabled") { }
}

/// <summary>One dispatch decision (for /admin/dispatch-trace observability).</summary>
public record DispatchTrace(
    DateTime Timestamp,
    string Model,
    string SessionId,
    string ServiceName,
    int Priority,
    int Weight,
    bool Success);
