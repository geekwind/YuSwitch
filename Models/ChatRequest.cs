using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuSwitch.Models;

/// <summary>
/// Unified chat completion request — the vendor-neutral internal model.
/// Carries the FULL OpenAI-compatible field set so no field is silently
/// dropped when routing to a provider that doesn't understand it (the
/// gateway down-converts instead).
/// </summary>
public class ChatRequest
{
    public string Model { get; set; } = "";

    public List<ChatMessage> Messages { get; set; } = new();

    public List<Tool>? Tools { get; set; }
    public JsonDocument? ToolChoice { get; set; }
    /// <summary>Whether to enable parallel function calling during tool use
    /// (OpenAI param). null = upstream default.</summary>
    public bool? ParallelToolCalls { get; set; }

    public ResponseFormat? ResponseFormat { get; set; }

    public ReasoningConfig? Reasoning { get; set; }

    public bool Stream { get; set; }
    public StreamOptions? StreamOptions { get; set; }

    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? PresencePenalty { get; set; }
    public List<string>? Stop { get; set; }
    public int? Seed { get; set; }
    public int? N { get; set; }
    public bool? LogProbs { get; set; }
    public int? TopLogProbs { get; set; }
    public Dictionary<int, int>? LogitBias { get; set; }
    public string? User { get; set; }

    /// <summary>Model name as requested by the client, before redirect/mapping.</summary>
    [JsonIgnore]
    public string ClientModel { get; set; } = "";

    /// <summary>Sticky session id, taken ONLY from the X-Session-Id header.
    /// When non-empty, the gateway routes the session to the same provider
    /// (affinity), preserving multi-turn inference context. Note: a client's
    /// `user` / `metadata.user_id` field is deliberately NOT used here, since
    /// most SDKs send a stable id that would pin all traffic to one service
    /// and defeat load balancing. Sticky is opt-in via the header only.</summary>
    [JsonIgnore]
    public string? SessionId { get; set; }

    /// <summary>Provider-specific headers to forward upstream.</summary>
    [JsonIgnore]
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();

    /// <summary>Gateway-side web search intent, populated by WebSearchService
    /// before routing. Never serialized upstream.</summary>
    [JsonIgnore]
    public WebSearchIntent? WebSearch { get; set; }

    /// <summary>Guard so web search enrichment runs at most once per request —
    /// failover candidates reuse this same ChatRequest object.</summary>
    [JsonIgnore]
    public bool SearchHandled { get; set; }
}

[JsonConverter(typeof(ChatMessageJsonConverter))]
public class ChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    /// <summary>Multimodal content parts when the wire "content" was an array
    /// (vision / audio). Managed exclusively by <see cref="ChatMessageJsonConverter"/>;
    /// when set, <see cref="Content"/> holds the concatenated text parts so
    /// text-only consumers (previews, Anthropic system prompt) keep working.</summary>
    [JsonIgnore]
    public List<ContentPart>? Parts { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// Handles the OpenAI union type for message "content": either a plain string
/// or an array of typed parts ({type:text|image_url|input_audio,...}). The
/// default (de)serializer would throw a JsonException on the array form —
/// which is exactly how vision requests used to crash the gateway. Class-level
/// so the content token can decide between Content (string) and Parts (array)
/// while keeping every sibling field mapped in one place.
/// </summary>
public class ChatMessageJsonConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var msg = new ChatMessage();

        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "role":
                    msg.Role = prop.Value.GetString() ?? "";
                    break;
                case "content":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        msg.Content = prop.Value.GetString();
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        msg.Parts = prop.Value.Deserialize<List<ContentPart>>(options);
                        // Degraded text view for text-only consumers (previews,
                        // system-prompt extraction). Providers use Parts.
                        msg.Content = string.Join("\n", (msg.Parts ?? new())
                            .Where(p => p.Type == "text" && !string.IsNullOrEmpty(p.Text))
                            .Select(p => p.Text));
                    }
                    // null / other kinds → Content stays null.
                    break;
                case "tool_calls":
                    msg.ToolCalls = prop.Value.Deserialize<List<ToolCall>>(options);
                    break;
                case "tool_call_id":
                    msg.ToolCallId = prop.Value.GetString();
                    break;
                case "name":
                    msg.Name = prop.Value.GetString();
                    break;
            }
        }
        return msg;
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);

        if (value.Parts is { Count: > 0 })
        {
            // Multimodal round-trip: write the parts array verbatim so
            // OpenAI-compatible upstreams receive the original structure.
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.Parts, options);
        }
        else if (value.Content is not null)
        {
            writer.WriteString("content", value.Content);
        }
        else if (value.ToolCalls is not { Count: > 0 })
        {
            // OpenAI requires content (possibly null) unless tool_calls present.
            writer.WriteNull("content");
        }

        if (value.ToolCalls is { Count: > 0 })
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        if (!string.IsNullOrEmpty(value.ToolCallId))
            writer.WriteString("tool_call_id", value.ToolCallId);
        if (!string.IsNullOrEmpty(value.Name))
            writer.WriteString("name", value.Name);

        writer.WriteEndObject();
    }
}

public class ContentPart
{
    public string Type { get; set; } = "text"; // text, image_url, input_audio
    public string? Text { get; set; }
    public ImageUrl? ImageUrl { get; set; }
    public InputAudio? InputAudio { get; set; }
}

public class ImageUrl
{
    public string Url { get; set; } = "";
    public string? Detail { get; set; } // low/high/auto
}

public class InputAudio
{
    public string Data { get; set; } = ""; // base64
    public string Format { get; set; } = ""; // wav/mp3/...
}

public class Tool
{
    public string Type { get; set; } = "function";
    public FunctionDecl? Function { get; set; }
    /// <summary>Additional fields (e.g. web_search's max_results) preserved
    /// verbatim so they round-trip to upstreams that understand them.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Internal gateway web-search intent, set by WebSearchService before
/// routing. Never serialized upstream (ChatRequest.WebSearch is [JsonIgnore]).</summary>
public class WebSearchIntent
{
    /// <summary>Effective mode after enrichment: "inject" or "simulate".</summary>
    public string Mode { get; set; } = "inject";
    /// <summary>Max results to fetch (client-requested value or config default).</summary>
    public int MaxResults { get; set; } = 5;
    /// <summary>Resolved Tavily API key (service override or the global setting).</summary>
    public string? ApiKey { get; set; }
}

public class FunctionDecl
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonDocument? Parameters { get; set; } // JSON Schema
    public bool Strict { get; set; }
}

public class ToolCall
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public FunctionCall Function { get; set; } = new();
}

public class FunctionCall
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = ""; // JSON string
}

public class ResponseFormat
{
    public string Type { get; set; } = "text"; // text, json_object, json_schema
    public JsonSchemaSpec? JsonSchema { get; set; }
}

public class JsonSchemaSpec
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonDocument? Schema { get; set; }
    public bool Strict { get; set; }
}

public class ReasoningConfig
{
    public ReasoningEffort Effort { get; set; } = ReasoningEffort.Medium;
    public bool Enabled { get; set; }
    public int? MaxThinkingTokens { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningEffort
{
    Minimal, Low, Medium, High, XHigh, Max
}

public class StreamOptions
{
    public bool IncludeUsage { get; set; }
}
