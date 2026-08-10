using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace YuSwitch.Data.Entities;

/// <summary>
/// An upstream provider service configuration (e.g. one OpenAI account,
/// one Claude account). Maps to the legacy Go "ServiceModel" concept.
/// One provider type can have multiple instances (multiple credentials).
/// </summary>
public class ServiceEntity
{
    public int Id { get; set; }

    /// <summary>Provider type: "openai", "claude", "gemini", "qianfan", ...</summary>
    [Required, MaxLength(64)]
    public string ProviderType { get; set; } = "openai";

    /// <summary>Human-friendly name shown in UI.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Upstream base URL (empty = provider default).</summary>
    [MaxLength(512)]
    public string ServerUrl { get; set; } = "";

    /// <summary>Weight for load balancing (higher = more traffic).</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Priority for failover (lower = tried first).</summary>
    public int Priority { get; set; } = 0;

    /// <summary>Credentials as JSON (api_key, appid, ...). Stored opaquely.</summary>
    public string CredentialsJson { get; set; } = "{}";

    /// <summary>Per-service limit config as JSON (qps/qpm/concurrency/timeout).</summary>
    public string LimitJson { get; set; } = "{}";

    /// <summary>Model redirect/alias map as JSON.</summary>
    public string ModelRedirectJson { get; set; } = "{}";

    /// <summary>Model name mapping as JSON.</summary>
    public string ModelMapJson { get; set; } = "{}";

    /// <summary>Per-service web search config as JSON (enabled/mode/maxResults/...).</summary>
    public string WebSearchJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ICollection<ModelEntity> Models { get; set; } = new List<ModelEntity>();

    /// <summary>Deserialize credentials into a dictionary.</summary>
    public Dictionary<string, string> GetCredentials() =>
        string.IsNullOrWhiteSpace(CredentialsJson)
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(CredentialsJson) ?? new();

    public LimitConfig GetLimit() =>
        string.IsNullOrWhiteSpace(LimitJson)
            ? new LimitConfig()
            : JsonSerializer.Deserialize<LimitConfig>(LimitJson) ?? new LimitConfig();

    public Dictionary<string, string> GetModelRedirects() =>
        string.IsNullOrWhiteSpace(ModelRedirectJson)
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(ModelRedirectJson) ?? new();

    public Dictionary<string, string> GetModelMap() =>
        string.IsNullOrWhiteSpace(ModelMapJson)
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(ModelMapJson) ?? new();

    /// <summary>Deserialize the web search config; null when the column is empty
    /// or unparsable (treat as "web search disabled").</summary>
    public WebSearchConfig? GetWebSearch() =>
        string.IsNullOrWhiteSpace(WebSearchJson)
            ? null
            : JsonSerializer.Deserialize<WebSearchConfig>(WebSearchJson);
}

public class WebSearchConfig
{
    /// <summary>Master switch — enable web search for this service.</summary>
    public bool Enabled { get; set; }

    /// <summary>"inject" = context injection (default, works with any upstream);
    /// "simulate" = register a web_search function tool and do a second round
    /// (requires a function-calling upstream). Non-streaming only; streaming
    /// simulate falls back to inject.</summary>
    public string Mode { get; set; } = "inject";

    /// <summary>How many search results to fetch per query.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>When true, every request through this service triggers a search
    /// automatically (client needs no web_search tool).</summary>
    public bool AlwaysSearch { get; set; }

    /// <summary>Optional per-service Tavily API key. Empty = use the global
    /// `web_search_tavily_key` setting.</summary>
    public string? ApiKey { get; set; }
}

public class LimitConfig
{
    public double Qps { get; set; }
    public double Qpm { get; set; }
    public double Concurrency { get; set; }
    public int TimeoutSeconds { get; set; }
}
