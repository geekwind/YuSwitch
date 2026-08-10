using System.Text.Json;
using YuSwitch.Models;
using YuSwitch.Services;

namespace YuSwitch.Middleware;

/// <summary>
/// API key authentication middleware. Validates the inbound key against
/// configured ApiKeys (or a single global key). Falls through to Blazor/UI
/// routes (which use cookie auth separately). Replaces the legacy
/// validateAPIKey + ValidateAPIKeyAndModel.
/// </summary>
public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConfigService _config;
    private readonly ApiKeyLimiter _limiter;
    private readonly ILogger<ApiKeyAuthMiddleware> _log;

    // snake_case to match OpenAI error envelope: {"error":{"message":...,"type":...}}
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ApiKeyAuthMiddleware(RequestDelegate next, ConfigService config,
        ApiKeyLimiter limiter, ILogger<ApiKeyAuthMiddleware> log)
    {
        _next = next; _config = config; _limiter = limiter; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        // Only gate the gateway API paths; UI/static/admin handled elsewhere.
        if (!IsGatewayPath(path))
        {
            await _next(ctx);
            return;
        }

        // CORS preflight (OPTIONS + Access-Control-Request-Method) carries no
        // credentials, so it can never satisfy the API-key check. Let it fall
        // through to the CORS middleware, which answers it with the Allow-*
        // headers. The real request that follows is authenticated normally.
        if (HttpMethods.IsOptions(ctx.Request.Method) &&
            ctx.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await _next(ctx);
            return;
        }

        var apiKey = ExtractApiKey(ctx.Request);
        var (valid, key) = ValidateKey(apiKey);
        if (!valid)
        {
            await WriteError(ctx, path, 401, "authentication_error", "invalid api key");
            return;
        }

        // Per-key limits: expiry / IP allowlist / QPM / daily quota. Only when
        // a concrete key matched (open mode has nothing to enforce).
        if (key is not null)
        {
            var check = await _limiter.CheckAsync(key, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
            if (!check.Allowed)
            {
                _log.LogWarning("api key '{Key}' rejected: {Reason}", key.Name, check.Message);
                await WriteError(ctx, path, check.Status,
                    check.Status == 429 ? "rate_limit_error" : "authentication_error",
                    check.Message ?? "request rejected");
                return;
            }
        }

        // model-level permission checked in the endpoint once body is parsed.
        ctx.Items["ApiKey"] = apiKey;
        ctx.Items["ApiKeyName"] = key?.Name ?? "open";
        await _next(ctx);
    }

    private static async Task WriteError(HttpContext ctx, string path, int status, string type, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        // Return the error in the protocol matching the inbound path:
        // Anthropic uses {"type":"error","error":{...}}, OpenAI uses {"error":{...}}.
        var body = path.StartsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new { type = "error", error = new { type, message } }, JsonOpts)
            : JsonSerializer.Serialize(new ErrorResponse
            {
                Error = new ErrorDetail { Message = message, Type = "invalid_request_error", Code = type }
            }, JsonOpts);
        await ctx.Response.WriteAsync(body);
    }

    private static bool IsGatewayPath(string path) =>
        path.StartsWith("/v1/") || path.StartsWith("/v1beta/") ||
        path == "/chat/completions" || path == "/messages" || path == "/responses";

    private static string? ExtractApiKey(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return req.Headers.TryGetValue("x-api-key", out var k) ? k.ToString()
             : req.Headers.TryGetValue("x-goog-api-key", out var g) ? g.ToString()
             : null;
    }

    private (bool valid, Data.Entities.ApiKeyEntity? key) ValidateKey(string? apiKey)
    {
        var snap = _config.Snapshot;
        // No keys configured at all → open (matches legacy behavior).
        if (snap.ApiKeys.Count == 0) return (true, null);

        if (string.IsNullOrEmpty(apiKey)) return (false, null);
        var key = snap.ApiKeys.FirstOrDefault(k =>
            k.Enabled && AdminAuthMiddleware.FixedTimeEquals(k.KeyValue, apiKey));
        if (key is null) return (false, null);
        return (true, key);
    }
}
