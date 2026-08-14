using System.Text.Json;
using YuSwitch.Models;

namespace YuSwitch.Gateway;

/// <summary>
/// Writes OpenAI-style SSE chunks to an HttpResponse. Single place owning
/// SSE framing (headers, "data: ...\n\n", flush, [DONE] terminator),
/// replacing per-handler hand-rolled writes.
/// </summary>
public static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task StartSseAsync(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync();
    }

    public static async Task WriteChunkAsync(HttpResponse response, StreamChunk chunk, CancellationToken ct)
    {
        // Raw passthrough: no model-alias rewrite happened, so write the upstream
        // `data:` payload bytes back verbatim (no re-serialization).
        if (chunk.RawPayload is { Length: > 0 } raw)
        {
            await response.WriteAsync("data: ", ct);
            await response.Body.WriteAsync(raw, ct);
            await response.WriteAsync("\n\n", ct);
        }
        else
        {
            var json = JsonSerializer.Serialize(chunk, JsonOpts);
            await response.WriteAsync($"data: {json}\n\n", ct);
        }
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteErrorAsync(HttpResponse response, StreamError err, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { error = new { type = err.Type, message = err.Message } }, JsonOpts);
        await response.WriteAsync($"data: {payload}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>Best-effort error emit once the stream has already started: skip
    /// entirely when the client is gone (writing with its dead ct just throws),
    /// and swallow write failures — an exception escaping an SSE catch handler
    /// bubbles to a JSON error result, and the framework then throws
    /// "StatusCode cannot be set because the response has already started".</summary>
    public static async Task WriteErrorSafeAsync(HttpResponse response, StreamError err, CancellationToken clientCt)
    {
        if (clientCt.IsCancellationRequested) return;
        try { await WriteErrorAsync(response, err, CancellationToken.None); }
        catch { /* client disconnected mid-write — nothing left to salvage */ }
    }

    /// <summary>Best-effort raw SSE data event (verbatim upstream error JSON)
    /// with the same guarantees as <see cref="WriteErrorSafeAsync"/>.</summary>
    public static async Task WriteRawSafeAsync(HttpResponse response, string json, CancellationToken clientCt)
    {
        if (clientCt.IsCancellationRequested) return;
        try
        {
            await response.WriteAsync($"data: {json}\n\n", CancellationToken.None);
            await response.Body.FlushAsync(CancellationToken.None);
        }
        catch { /* client disconnected mid-write */ }
    }

    public static async Task WriteDoneAsync(HttpResponse response, CancellationToken ct)
    {
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

public record StreamError(string Type, string Message);
