using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using YuSwitch.Data.Entities;
using YuSwitch.Models;

namespace YuSwitch.Services;

/// <summary>
/// Gateway-side web search. When an upstream platform cannot search itself
/// (no native web_search support), the gateway performs the search via Tavily
/// and feeds the results to the upstream model — either as an injected system
/// message ("inject") or as a simulated function-call round trip ("simulate").
/// Never a hard dependency: a missing/expired key or a failed search degrades
/// to "continue without results" rather than failing the request.
/// </summary>
public class WebSearchService
{
    private const string TavilyUrl = "https://api.tavily.com/search";

    private readonly IHttpClientFactory _http;
    private readonly AppSettingsService _settings;
    private readonly ILogger<WebSearchService> _log;

    public WebSearchService(IHttpClientFactory http, AppSettingsService settings, ILogger<WebSearchService> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Central entry point, invoked once per request by GatewayService (guarded
    /// by <see cref="ChatRequest.SearchHandled"/>). Decides, from the resolved
    /// service config, whether/how to enrich:
    ///  - not requested / disabled → strip any web_search tool (never leaks upstream);
    ///  - "inject" → search now, prepend results as a system message;
    ///  - "simulate" (non-streaming) → replace web_search tools with a function
    ///    tool so the upstream model decides whether to search; GatewayService
    ///    performs the second round.
    /// simulate + streaming falls back to inject (holding back the first round
    /// to discover the tool call would break the SSE contract).
    /// </summary>
    public async Task EnrichAsync(ChatRequest req, ServiceEntity svc, CancellationToken ct)
    {
        var cfg = svc.GetWebSearch();
        var requested = HasWebSearchTool(req) || cfg?.AlwaysSearch == true;
        if (!requested || cfg is null || !cfg.Enabled)
        {
            StripWebSearchTools(req);
            return;
        }

        var maxResults = ClampMaxResults(ClientRequestedMaxResults(req) ?? cfg.MaxResults);
        var apiKey = ResolveKey(cfg);
        req.WebSearch = new WebSearchIntent { Mode = cfg.Mode, MaxResults = maxResults, ApiKey = apiKey };

        if (cfg.Mode == "simulate" && !req.Stream)
        {
            ReplaceWebSearchWithFunction(req, maxResults);
            return;
        }

        // inject (or simulate forced back to inject by streaming)
        StripWebSearchTools(req);
        var query = ExtractQuery(req);
        if (string.IsNullOrWhiteSpace(query))
            return;

        string context;
        try
        {
            context = await SearchAsync(query, maxResults, apiKey, ct);
        }
        catch (Exception ex)
        {
            // Search is additive enrichment — never fail the request on it.
            _log.LogWarning(ex, "web search failed for service {Svc}; continuing without results", svc.Name);
            return;
        }
        req.Messages.Insert(0, new ChatMessage { Role = "system", Content = context });
    }

    /// <summary>Execute a Tavily search and format the results into a compact
    /// context block the model can answer from.</summary>
    public async Task<string> SearchAsync(string query, int maxResults, string apiKey, CancellationToken ct)
    {
        using var client = _http.CreateClient("tavily");
        using var resp = await client.PostAsJsonAsync(TavilyUrl, new Dictionary<string, object?>
        {
            ["api_key"] = apiKey,
            ["query"] = query,
            ["max_results"] = maxResults,
            ["include_answer"] = true,
            ["search_depth"] = "basic",
        }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tavily {(int)resp.StatusCode}: {Trunc(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        sb.Append($"[Web Search Results for: {query}]");

        if (root.TryGetProperty("answer", out var answer) &&
            answer.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(answer.GetString()))
            sb.Append("\nSummary: ").Append(answer.GetString());

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var i = 1;
            foreach (var r in results.EnumerateArray())
            {
                var title = GetString(r, "title");
                var url = GetString(r, "url");
                var content = GetString(r, "content");
                sb.Append('\n').Append(i++).Append(". ");
                if (!string.IsNullOrEmpty(title)) sb.Append(title);
                if (!string.IsNullOrEmpty(url)) sb.Append(" — ").Append(url);
                if (!string.IsNullOrEmpty(content)) sb.Append("\n  ").Append(content);
            }
        }
        return sb.ToString();
    }

    private bool HasWebSearchTool(ChatRequest req) =>
        req.Tools is { Count: > 0 } && req.Tools.Any(t => t.Type == "web_search");

    private static void StripWebSearchTools(ChatRequest req)
    {
        if (req.Tools is not { Count: > 0 }) return;
        var kept = req.Tools.Where(t => t.Type != "web_search").ToList();
        req.Tools = kept.Count > 0 ? kept : null;
    }

    /// <summary>Replace each web_search tool with an equivalent function tool so
    /// a function-calling upstream can decide to invoke it. The model supplies
    /// the query via tool arguments; GatewayService performs the search.</summary>
    private static void ReplaceWebSearchWithFunction(ChatRequest req, int maxResults)
    {
        if (req.Tools is not { Count: > 0 }) return;
        for (var i = 0; i < req.Tools.Count; i++)
        {
            if (req.Tools[i].Type != "web_search") continue;
            req.Tools[i] = new Tool
            {
                Type = "function",
                Function = new FunctionDecl
                {
                    Name = "web_search",
                    Description = "Search the web for up-to-date information. Returns page titles, URLs and excerpts. Use this when the answer may require current or factual information.",
                    Parameters = JsonDocument.Parse(
                        $$$"""{"type":"object","properties":{"query":{"type":"string","description":"The search query."},"max_results":{"type":"integer","default":{{{maxResults}}},"description":"Number of results to return."}},"required":["query"]}"""),
                },
            };
        }
    }

    /// <summary>Prefer the client's web_search tool max_results, else null.</summary>
    private static int? ClientRequestedMaxResults(ChatRequest req)
    {
        if (req.Tools is { Count: > 0 })
            foreach (var t in req.Tools)
                if (t.Type == "web_search" && t.ExtensionData is not null &&
                    t.ExtensionData.TryGetValue("max_results", out var mr) &&
                    mr.ValueKind == JsonValueKind.Number && mr.TryGetInt32(out var v))
                    return v;
        return null;
    }

    private string ResolveKey(WebSearchConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.ApiKey) ? cfg.ApiKey!.Trim()
            : _settings.WebSearchTavilyKey.Trim();

    /// <summary>Last user text message — the natural search query.</summary>
    public static string ExtractQuery(ChatRequest req)
    {
        for (int i = req.Messages.Count - 1; i >= 0; i--)
            if (req.Messages[i].Role == "user")
                return req.Messages[i].Content ?? "";
        return "";
    }

    private static int ClampMaxResults(int v) => v is >= 1 and <= 10 ? v : 5;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
