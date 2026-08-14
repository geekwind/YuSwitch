using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using YuSwitch.Gateway;
using YuSwitch.Models;
using YuSwitch.Providers.OpenAI;
using YuSwitch.Services;

namespace YuSwitch.Endpoints;

/// <summary>
/// OpenAI-compatible inbound endpoints: /v1/chat/completions,
/// /v1/models, /v1/embeddings. This is the primary client-facing surface.
/// </summary>
public static class OpenAiEndpoints
{
    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1");

        g.MapPost("/chat/completions", HandleChatCompletion);

        g.MapGet("/models", HandleListModels);
        g.MapGet("/models/{model}", HandleGetModel);
        g.MapPost("/embeddings", HandleEmbeddings);

        return app;
    }

    private static async Task<IResult> HandleChatCompletion(
        HttpContext ctx, GatewayService gw, ConfigService config, CancellationToken ct)
    {
        // Read the raw body once: keep the bytes so the provider can forward
        // them verbatim (or patch the model field) instead of re-serializing
        // the typed ChatRequest — the fast path that avoids unknown-field loss
        // and extra JSON passes on the hot path.
        var rawBody = await ReadBodyAsync(ctx.Request.Body, ct);
        ChatRequest req;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            req = doc.RootElement.Deserialize<ChatRequest>(JsonOpts) ?? new ChatRequest();
        }
        catch (JsonException ex)
        {
            // Malformed request body must come back as the OpenAI error envelope
            // (400), not as an unhandled 500 from the exception middleware.
            return Error(400, $"invalid request body: {ex.Message}", "invalid_request_error");
        }
        req.OriginalPayload = rawBody;
        req.ClientModel = req.Model;
        // Sticky session engages ONLY on an explicit X-Session-Id header — a
        // client's `user` field no longer pins affinity (it defeated load
        // balancing because most SDKs send a stable user id).
        req.SessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault();
        // Forward client headers that upstreams need (anthropic-version, beta, etc.)
        req.ExtraHeaders = ExtractForwardableHeaders(ctx.Request.Headers);
        var apiKeyName = ctx.Items["ApiKeyName"] as string ?? "";

        if (!CheckModelPermission(ctx, config, req.Model))
            return Error(403, "model not allowed for this api key", "invalid_request_error");

        if (req.Stream)
            return await StreamResponse(ctx, gw, req, apiKeyName, ct);
        else
            return await NonStreamResponse(gw, req, apiKeyName, ct);
    }

    /// <summary>Headers to forward to upstream (whitelist, like sub2api).</summary>
    private static Dictionary<string, string> ExtractForwardableHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] forward = { "anthropic-version", "anthropic-beta", "user-agent",
            "x-stainless-arch", "x-stainless-os", "x-stainless-package-version",
            "x-stainless-runtime", "x-stainless-runtime-version", "x-stainless-lang",
            "x-client-request-id", "accept-language", "openai-beta", "originator" };
        foreach (var key in forward)
        {
            var v = headers[key].ToString();
            if (!string.IsNullOrEmpty(v)) result[key] = v;
        }
        return result;
    }

    private static async Task<IResult> NonStreamResponse(
        GatewayService gw, ChatRequest req, string apiKeyName, CancellationToken ct)
    {
        try
        {
            var resp = await gw.ChatAsync(req, apiKeyName, ct);
            // No model-alias rewrite happened → forward the upstream JSON bytes
            // verbatim (zero re-serialization, unknown fields preserved).
            if (resp.RawPayload is not null)
                return Results.Bytes(resp.RawPayload, "application/json");
            return Results.Json(resp, JsonOpts);
        }
        catch (ModelNotFoundException ex)
        {
            return Error(404, ex.Message, "invalid_request_error");
        }
        catch (ServiceCapacityException ex)
        {
            return Error(503, ex.Message, "server_error");
        }
        catch (UpstreamException ex)
        {
            return UpstreamErrorPassthrough(ex);
        }
    }

    private static async Task<IResult> StreamResponse(
        HttpContext ctx, GatewayService gw, ChatRequest req, string apiKeyName, CancellationToken ct)
    {
        // Materialize the first chunk BEFORE starting SSE, so pre-stream failures
        // (unknown model, all services at capacity, upstream 4xx/5xx) get their
        // real HTTP status instead of a 200 + SSE error event.
        await using var iter = gw.StreamAsync(req, apiKeyName, ct).GetAsyncEnumerator(ct);
        StreamChunk? first;
        try
        {
            first = await iter.MoveNextAsync() ? iter.Current : null;
        }
        catch (ModelNotFoundException ex)
        {
            return Error(404, ex.Message, "invalid_request_error");
        }
        catch (ServiceCapacityException ex)
        {
            return Error(503, ex.Message, "server_error");
        }
        catch (UpstreamException ex)
        {
            return UpstreamErrorPassthrough(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            return Error(500, ex.Message, "server_error");
        }

        await SseWriter.StartSseAsync(ctx.Response);
        try
        {
            if (first is not null)
                await SseWriter.WriteChunkAsync(ctx.Response, first, ct);
            while (await iter.MoveNextAsync())
                await SseWriter.WriteChunkAsync(ctx.Response, iter.Current, ct);
            await SseWriter.WriteDoneAsync(ctx.Response, ct);
            return Results.Empty;
        }
        catch (UpstreamException ex)
        {
            // Upstream failed mid-stream: emit the real provider error envelope
            // verbatim as an SSE data event, then end the stream (no [DONE]).
            if (JsonBody(ex.UpstreamBody, out var json))
                await SseWriter.WriteRawSafeAsync(ctx.Response, json, ct);
            else
                await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("upstream_error", ex.Message), ct);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — nobody is listening; write nothing.
            return Results.Empty;
        }
        catch (Exception ex)
        {
            await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("upstream_error", ex.Message), ct);
            return Results.Empty;
        }
    }

    private static IResult HandleListModels(ConfigService config)
    {
        var models = config.GetEnabledModelNames().Select(m => new
        {
            id = m,
            @object = "model",
            created = 0,
            owned_by = "easy-gateway",
        });
        return Results.Json(new { @object = "list", data = models });
    }

    private static IResult HandleGetModel(string model, ConfigService config)
    {
        var names = config.GetEnabledModelNames();
        if (!names.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase)))
            return Error(404, $"model '{model}' not found", "invalid_request_error");
        return Results.Json(new { id = model, @object = "model", created = 0, owned_by = "easy-gateway" });
    }

    private static async Task<IResult> HandleEmbeddings(
        HttpContext ctx, [FromBody] JsonElement body, GatewayService gw, ConfigService config, CancellationToken ct)
    {
        // Manual parse: OpenAI "input" is a union (string | string[] | token
        // arrays); typed binding would reject the plain-string form.
        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "" : "";
        if (model.Length == 0)
            return Error(400, "model is required", "invalid_request_error");

        var input = new List<string>();
        if (body.TryGetProperty("input", out var inp))
        {
            if (inp.ValueKind == JsonValueKind.String)
                input.Add(inp.GetString() ?? "");
            else if (inp.ValueKind == JsonValueKind.Array)
                foreach (var e in inp.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String)
                        input.Add(e.GetString() ?? "");
                    else
                        return Error(400, "token-array input is not supported; send strings", "invalid_request_error");
        }
        if (input.Count == 0)
            return Error(400, "input is required", "invalid_request_error");

        if (!CheckModelPermission(ctx, config, model))
            return Error(403, "model not allowed for this api key", "invalid_request_error");

        var apiKeyName = ctx.Items["ApiKeyName"] as string ?? "";
        var req = new EmbeddingRequest { Model = model, Input = input };
        try
        {
            var resp = await gw.EmbedAsync(req, model, apiKeyName, ct);
            return Results.Json(resp, JsonOpts);
        }
        catch (ModelNotFoundException ex)
        {
            return Error(404, ex.Message + " (embeddings requires a model flagged 支持Embeddings)", "invalid_request_error");
        }
        catch (ServiceCapacityException ex)
        {
            return Error(503, ex.Message, "server_error");
        }
        catch (UpstreamException ex)
        {
            return UpstreamErrorPassthrough(ex);
        }
    }

    private static bool CheckModelPermission(HttpContext ctx, ConfigService config, string model)
    {
        var snap = config.Snapshot;
        if (snap.ApiKeys.Count == 0) return true; // open mode
        var keyValue = ctx.Items["ApiKey"] as string;
        var key = snap.ApiKeys.FirstOrDefault(k => k.KeyValue == keyValue);
        return key?.AllowsModel(model) ?? false;
    }

    private static IResult Error(int status, string message, string type) =>
        Results.Json(new ErrorResponse { Error = new ErrorDetail { Message = message, Type = type } },
            JsonSerializerOptions, statusCode: status);

    /// <summary>Pass through an upstream failure verbatim: reuse the upstream
    /// HTTP status and, when the upstream body is JSON, return it untouched so
    /// clients see the real provider error envelope (e.g. OpenAI
    /// {error:{message,type,code}}). Falls back to the gateway envelope.</summary>
    private static IResult UpstreamErrorPassthrough(UpstreamException ex)
    {
        if (JsonBody(ex.UpstreamBody, out var json))
            return new StatusTextResult(json, "application/json", (int)ex.StatusCode);
        return Error((int)ex.StatusCode, ex.Message, "upstream_error");
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static bool JsonBody(string body, out string json)
    {
        json = body;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try { using var doc = JsonDocument.Parse(body); return true; }
        catch (JsonException) { return false; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions JsonSerializerOptions = JsonOpts;
}
