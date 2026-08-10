using System.ComponentModel.DataAnnotations;

namespace YuSwitch.Data.Entities;

/// <summary>
/// Per-request usage log for observability and accounting. Written
/// asynchronously to avoid blocking the response path.
/// </summary>
public class UsageLogEntity
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    [MaxLength(64)]
    public string ApiKeyName { get; set; } = "";

    [MaxLength(128)]
    public string Model { get; set; } = "";

    /// <summary>The actual model name sent upstream after alias→real resolution
    /// and any service-level model redirect/map. Lets the log show the full
    /// routing chain: client alias → real upstream model.</summary>
    [MaxLength(128)]
    public string UpstreamModel { get; set; } = "";

    [MaxLength(64)]
    public string ProviderType { get; set; } = "";

    [MaxLength(64)]
    public string ServiceName { get; set; } = "";

    // --- Token consumption ---
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Reasoning tokens (o1/deepseek-reasoner thinking budget).</summary>
    public int ReasoningTokens { get; set; }

    // --- Prompt cache (Anthropic ephemeral / OpenAI cache) ---
    /// <summary>Tokens written to the upstream cache this request (Anthropic cache_creation_input_tokens).</summary>
    public int CacheCreationTokens { get; set; }
    /// <summary>Tokens served from the upstream cache this request (Anthropic cache_read_input_tokens).</summary>
    public int CacheReadTokens { get; set; }

    /// <summary>Whether the response hit the upstream prompt cache (cache_read_tokens > 0).</summary>
    public bool CacheHit { get; set; }

    /// <summary>Latency in milliseconds (end-to-end for this request).</summary>
    public long LatencyMs { get; set; }

    /// <summary>Time to first token in milliseconds (streaming only; 0 for non-stream).</summary>
    public long TtftMs { get; set; }

    public bool Stream { get; set; }
    public bool Success { get; set; }

    [MaxLength(16)]
    public string StatusCode { get; set; } = "";

    /// <summary>Error message if failed (truncated).</summary>
    [MaxLength(1024)]
    public string Error { get; set; } = "";

    /// <summary>First N chars of the prompt for debugging (configurable length).</summary>
    [MaxLength(256)]
    public string PromptPreview { get; set; } = "";

    /// <summary>First N chars of the response for debugging.</summary>
    [MaxLength(256)]
    public string ResponsePreview { get; set; } = "";

    /// <summary>Session id if sticky-session routing was used.</summary>
    [MaxLength(128)]
    public string SessionId { get; set; } = "";
}
