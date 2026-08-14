using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var (req, _) = BuildRequest(request, stream: false);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException(resp.StatusCode, Encoding.UTF8.GetString(bodyBytes));

        var chatResp = JsonSerializer.Deserialize<ChatResponse>(bodyBytes, JsonOpts)
            ?? throw new UpstreamException(resp.StatusCode, "empty response");
        if (!string.IsNullOrEmpty(request.ClientModel))
            chatResp.Model = request.ClientModel;
        // No model-alias rewrite → the raw upstream bytes already carry the
        // client-facing model name; let the endpoint forward them verbatim.
        if (request.ClientModel == request.Model)
            chatResp.RawPayload = bodyBytes;
        return chatResp;
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _extraHeaders = request.ExtraHeaders;
        request.Stream = true;
        // Ensure we get usage stats from upstream even if the client didn't
        // ask for them — the gateway needs usage for logging/accounting.
        var clientAskedUsage = request.StreamOptions?.IncludeUsage == true;
        if (request.StreamOptions is null)
            request.StreamOptions = new StreamOptions { IncludeUsage = true };
        else if (!request.StreamOptions.IncludeUsage)
            request.StreamOptions.IncludeUsage = true;

        var resp = await SendChatStreamAsync(request, injectUsage: true, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var statusCode = resp.StatusCode;
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            // Some OpenAI-compatible servers (older vLLM, strict relays) 400 on
            // the stream_options field the gateway injected — retry once without
            // it. Never strip a client's own stream_options; that 400 stays a
            // caller error.
            if (!clientAskedUsage && (int)statusCode == 400 &&
                (errBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase) ||
                 errBody.Contains("include_usage", StringComparison.OrdinalIgnoreCase)))
            {
                request.StreamOptions = null;
                resp = await SendChatStreamAsync(request, injectUsage: false, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var retryStatus = resp.StatusCode;
                    var retryBody = await resp.Content.ReadAsStringAsync(ct);
                    resp.Dispose();
                    throw new UpstreamException(retryStatus, retryBody);
                }
            }
            else
            {
                throw new UpstreamException(statusCode, errBody);
            }
        }
        using (resp)
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string chunkId = "";
            long created = 0;

            // ReadLineAsync-until-null only: StreamReader.EndOfStream does a
            // SYNC blocking read when its buffer is empty (i.e. between every two
            // SSE events), pinning a thread-pool thread per concurrent stream.
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;

                var data = line["data:".Length..].Trim();
                if (data == "[DONE]") yield break;

                if (request.ClientModel == request.Model)
                {
                    // No-alias fast path: one JSON pass extracts exactly what the
                    // gateway consumes (usage for accounting, choices' delta for the
                    // call preview) and SseWriter writes the raw upstream bytes
                    // verbatim — no typed re-deserialization, id/created passthrough
                    // untouched so clients see the upstream line as-is.
                    var chunk = new StreamChunk { RawPayload = Encoding.UTF8.GetBytes(data) };
                    using (var doc = JsonDocument.Parse(data))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                            chunk.Usage = u.Deserialize<Usage>(JsonOpts);
                        if (root.TryGetProperty("choices", out var cs) && cs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in cs.EnumerateArray())
                            {
                                if (c.ValueKind != JsonValueKind.Object
                                    || !c.TryGetProperty("delta", out var delta)
                                    || delta.ValueKind != JsonValueKind.Object)
                                    continue;
                                var sc = new StreamChoice();
                                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                                    sc.Delta.Content = content.GetString();
                                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                    sc.Delta.ReasoningContent = rc.GetString();
                                chunk.Choices ??= new List<StreamChoice>();
                                chunk.Choices.Add(sc);
                            }
                        }
                    }
                    yield return chunk;
                    continue;
                }

                // Alias path: a model rewrite happened, so re-deserialize the typed
                // chunk to rewrite the model name and carry id/created forward.
                var typed = JsonSerializer.Deserialize<StreamChunk>(data, JsonOpts);
                if (typed is null) continue;

                // Carry id/created from first chunk to subsequent ones.
                if (!string.IsNullOrEmpty(typed.Id)) chunkId = typed.Id;
                else typed.Id = chunkId;
                if (typed.Created != 0) created = typed.Created;
                else typed.Created = created;

                // Echo client-facing model name.
                if (!string.IsNullOrEmpty(request.ClientModel))
                    typed.Model = request.ClientModel;

                yield return typed;
            }
        }
    }

    /// <summary>Send one streaming chat request. injectUsage=false omits the
    /// gateway's stream_options.include_usage patch (400-retry fallback).</summary>
    private Task<HttpResponseMessage> SendChatStreamAsync(ChatRequest request, bool injectUsage, CancellationToken ct)
    {
        var (req, _) = BuildRequest(request, stream: true, injectUsage);
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private (HttpRequestMessage req, byte[] payload) BuildRequest(ChatRequest request, bool stream, bool injectUsage = true)
    {
        var payload = BuildPayload(request, stream, injectUsage);
        var url = $"{_baseUrl}/chat/completions";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetHeaders(req);
        req.Content = new ByteArrayContent(payload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (stream)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return (req, payload);
    }

    /// <summary>Builds the outbound request body. Prefers the raw inbound bytes
    /// (set by the endpoint) so upstreams receive the client's JSON untouched —
    /// forward verbatim when nothing changed, patch the model/stream_options in
    /// place when they did. Falls back to full typed re-serialization only when
    /// the gateway mutated the body (web search inject) or no raw bytes exist.</summary>
    private byte[] BuildPayload(ChatRequest request, bool stream, bool injectUsage = true)
    {
        if (request.OriginalPayload is null || request.WebSearch is not null)
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOpts));

        // No alias + non-streaming: routing/redirect changed nothing in the body —
        // send the client's bytes verbatim (zero JSON work on the hot path).
        if (!stream && request.ClientModel == request.Model)
            return request.OriginalPayload;

        // Streaming forces stream_options.include_usage (accounting) and an alias
        // rewrites the model name. Patch the ORIGINAL document so every other
        // field (including unknown ones) passes through untouched.
        var node = JsonNode.Parse(Encoding.UTF8.GetString(request.OriginalPayload));
        if (node is null)   // literal JSON "null" body
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOpts));
        node["model"] = request.Model;
        if (stream && injectUsage)
        {
            if (node["stream_options"] is JsonObject so)
                so["include_usage"] = true;
            else
                node["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            node.WriteTo(writer);
        return ms.ToArray();
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
