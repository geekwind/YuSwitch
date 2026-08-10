using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using YuSwitch.Data.Entities;
using YuSwitch.Models;

namespace YuSwitch.Providers.OpenAI;

/// <summary>
/// Provider for OpenAI-compatible upstreams. One implementation serves
/// OpenAI, DeepSeek, Zhipu, Groq, Azure (compatible), Lingyi, and any
/// vendor that speaks the /v1/chat/completions protocol — configured via
/// ServerUrl + api_key. This is the migration pilot for the unified
/// IProvider abstraction.
/// </summary>
public class OpenAIProvider : IProvider, ISupportsTools, ISupportsVision,
    ISupportsReasoning, ISupportsResponseFormat, IEmbeddingProvider, IModelListable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _providerType;
    private Dictionary<string, string>? _extraHeaders;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OpenAIProvider(HttpClient http, ServiceEntity service)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(service.ServerUrl);
        _apiKey = service.GetCredentials().GetValueOrDefault("api_key", "");
        _providerType = service.ProviderType;
    }

    public string Type => _providerType;

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        _extraHeaders = request.ExtraHeaders;
        var (req, payload) = BuildRequest(request, stream: false);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException(resp.StatusCode, body);

        var chatResp = JsonSerializer.Deserialize<ChatResponse>(body, JsonOpts)
            ?? throw new UpstreamException(resp.StatusCode, "empty response");
        if (!string.IsNullOrEmpty(request.ClientModel))
            chatResp.Model = request.ClientModel;
        return chatResp;
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _extraHeaders = request.ExtraHeaders;
        request.Stream = true;
        // Ensure we get usage stats from upstream even if the client didn't
        // ask for them — the gateway needs usage for logging/accounting.
        if (request.StreamOptions is null)
            request.StreamOptions = new StreamOptions { IncludeUsage = true };
        else if (!request.StreamOptions.IncludeUsage)
            request.StreamOptions.IncludeUsage = true;

        var (req, _) = BuildRequest(request, stream: true);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new UpstreamException(resp.StatusCode, errBody);
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string chunkId = "";
        long created = 0;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            var chunk = JsonSerializer.Deserialize<StreamChunk>(data, JsonOpts);
            if (chunk is null) continue;

            // Carry id/created from first chunk to subsequent ones.
            if (!string.IsNullOrEmpty(chunk.Id)) chunkId = chunk.Id;
            else chunk.Id = chunkId;
            if (chunk.Created != 0) created = chunk.Created;
            else chunk.Created = created;

            // Echo client-facing model name.
            if (!string.IsNullOrEmpty(request.ClientModel))
                chunk.Model = request.ClientModel;

            yield return chunk;
        }
    }

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(request, JsonOpts);
        var url = $"{_baseUrl}/embeddings";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetHeaders(req);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException(resp.StatusCode, body);
        return JsonSerializer.Deserialize<EmbeddingResponse>(body, JsonOpts)
            ?? throw new UpstreamException(resp.StatusCode, "empty response");
    }

    /// <summary>Fetch models from GET {baseUrl}/models (OpenAI-compatible).</summary>
    public async Task<List<UpstreamModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        SetHeaders(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException(resp.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        var result = new List<UpstreamModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                    result.Add(new UpstreamModelInfo(id!, null));
            }
        }
        return result;
    }

    // --- helpers ---

    private (HttpRequestMessage req, string payload) BuildRequest(ChatRequest request, bool stream)
    {
        var payload = JsonSerializer.Serialize(request, JsonOpts);
        var url = $"{_baseUrl}/chat/completions";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetHeaders(req);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (stream)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return (req, payload);
    }

    private void SetHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        // Forward client headers (anthropic-version, anthropic-beta, user-agent, etc.)
        // passed via ChatRequest.ExtraHeaders from the endpoint layer.
        if (_extraHeaders is not null)
        {
            foreach (var kv in _extraHeaders)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }

    private static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "https://api.openai.com/v1";
        url = url.TrimEnd('/');
        // Accept both "/v1" base and "/v1/chat/completions" full URL.
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/chat/completions".Length];
        return url;
    }
}

/// <summary>Upstream error carrying the original status code + body.</summary>
public class UpstreamException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }
    public string UpstreamBody { get; }

    public UpstreamException(System.Net.HttpStatusCode status, string body)
        : base($"upstream {(int)status}: {Truncate(body, 500)}")
    {
        StatusCode = status;
        UpstreamBody = body;
    }

    /// <summary>True for 5xx (retryable / failover candidate).</summary>
    public bool IsServerError => (int)StatusCode >= 500 && (int)StatusCode < 600;

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";
}
