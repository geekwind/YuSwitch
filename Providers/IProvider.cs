using YuSwitch.Models;

namespace YuSwitch.Providers;

/// <summary>
/// The unified upstream provider interface. Every vendor implements this;
/// the gateway dispatches requests through it without caring about
/// vendor-specific protocol differences.
/// </summary>
/// <remarks>
/// Design: the interface is intentionally thin. Optional capabilities
/// (embeddings, vision, tools, reasoning) are discovered via marker
/// interfaces (ISupportsTools etc.) or via the service/model entity flags,
/// so providers only implement what they support.
/// </remarks>
public interface IProvider
{
    /// <summary>Provider type name (e.g. "openai", "claude").</summary>
    string Type { get; }

    /// <summary>Non-streaming chat completion.</summary>
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming chat completion. Returns an async stream of chunks —
    /// IAsyncEnumerable is C#'s native async-stream primitive, ideal for
    /// SSE: the endpoint awaits foreach and writes each chunk as it arrives.
    /// </summary>
    IAsyncEnumerable<StreamChunk> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

/// <summary>Optional embedding capability.</summary>
public interface IEmbeddingProvider : IProvider
{
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

// --- Optional capability markers (probe via 'is') ---

/// <summary>Marker: provider supports function calling / tools.</summary>
public interface ISupportsTools { }

/// <summary>Marker: provider supports vision/multimodal input.</summary>
public interface ISupportsVision { }

/// <summary>Marker: provider supports reasoning models.</summary>
public interface ISupportsReasoning { }

/// <summary>Marker: provider supports response_format (json_schema etc.).</summary>
public interface ISupportsResponseFormat { }

/// <summary>Marker: provider can list available upstream models. Implemented
/// by providers whose upstream exposes a /models endpoint (OpenAI-compatible
/// or Anthropic). Used for one-click model discovery in the admin UI.</summary>
public interface IModelListable
{
    /// <summary>Fetch the list of models available on this upstream.</summary>
    Task<List<UpstreamModelInfo>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>One model entry from an upstream /models listing.</summary>
public record UpstreamModelInfo(string Id, string? DisplayName);

/// <summary>
/// Context passed to a provider factory when building an instance for a
/// specific service config (credentials, server url, per-request http client).
/// </summary>
public record ProviderContext(
    string ServerUrl,
    Dictionary<string, string> Credentials,
    IHttpClientFactory? HttpClientFactory);
