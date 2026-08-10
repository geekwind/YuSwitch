using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuSwitch.Models;

/// <summary>Unified non-streaming chat completion response.</summary>
public class ChatResponse
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "chat.completion";
    public long Created { get; set; }
    public string Model { get; set; } = "";
    public string? SystemFingerprint { get; set; }
    public List<Choice> Choices { get; set; } = new();
    public Usage? Usage { get; set; }
}

public class Choice
{
    public int Index { get; set; }
    public ResponseMessage Message { get; set; } = new();
    public string FinishReason { get; set; } = ""; // stop, length, tool_calls, content_filter
}

public class ResponseMessage
{
    public string Role { get; set; } = "assistant";
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
}

/// <summary>One incremental streaming chunk (OpenAI chat.completion.chunk).</summary>
public class StreamChunk
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "chat.completion.chunk";
    public long Created { get; set; }
    public string Model { get; set; } = "";
    public List<StreamChoice>? Choices { get; set; }
    public Usage? Usage { get; set; }
}

public class StreamChoice
{
    public int Index { get; set; }
    public Delta Delta { get; set; } = new();
    public string? FinishReason { get; set; }
}

public class Delta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
}

public class Usage : IJsonOnDeserialized
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }

    // Overflow for unmapped usage keys: OpenAI nests these under
    // prompt_tokens_details / completion_tokens_details, and DeepSeek sends
    // top-level prompt_cache_hit_tokens. The snake_case naming policy does NOT
    // transform JsonExtensionData keys, so the raw snake_case names are stored
    // verbatim and flattened into the flat fields below. Future vendor fields
    // are also captured here (harmless pass-through on serialization).
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; set; }

    public void OnDeserialized()
    {
        if (Extra is null) return;

        // OpenAI: prompt_tokens_details.cached_tokens → cache read.
        if (Extra.TryGetValue("prompt_tokens_details", out var ptd)
            && ptd.TryGetProperty("cached_tokens", out var cached))
            CacheReadInputTokens = Math.Max(CacheReadInputTokens, cached.GetInt32());

        // OpenAI / DeepSeek: completion_tokens_details.reasoning_tokens → reasoning.
        if (Extra.TryGetValue("completion_tokens_details", out var ctd)
            && ctd.TryGetProperty("reasoning_tokens", out var rt))
            ReasoningTokens = Math.Max(ReasoningTokens, rt.GetInt32());

        // DeepSeek: top-level prompt_cache_hit_tokens → cache read.
        if (Extra.TryGetValue("prompt_cache_hit_tokens", out var pch))
            CacheReadInputTokens = Math.Max(CacheReadInputTokens, pch.GetInt32());

        // cache_creation_input_tokens is Anthropic-only; OpenAI/DeepSeek never
        // send it, so it stays 0 for them — expected.
    }
}

/// <summary>OpenAI-standard error envelope.</summary>
public class ErrorResponse
{
    public ErrorDetail Error { get; set; } = new();
}

public class ErrorDetail
{
    public string Message { get; set; } = "";
    public string Type { get; set; } = "";
    // OpenAI spec: code and param are null when not applicable (not "").
    public string? Code { get; set; }
    public string? Param { get; set; }
}
