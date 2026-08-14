using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using YuSwitch.Gateway;
using YuSwitch.Models;
using YuSwitch.Providers.OpenAI;
using YuSwitch.Services;

namespace YuSwitch.Endpoints;

/// <summary>
/// Anthropic Messages API inbound: /v1/messages, /v1/messages/count_tokens.
/// Accepts the Anthropic request shape, converts to the unified ChatRequest,
/// routes through the gateway, and converts the response back to the
/// Anthropic Messages format. This lets Claude Code / Anthropic SDK clients
/// use the gateway directly.
/// </summary>
public static class AnthropicEndpoints
{
    public static IEndpointRouteBuilder MapAnthropicEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1");
        g.MapPost("/messages", HandleMessages);
        g.MapPost("/messages/count_tokens", HandleCountTokens);
        return app;
    }

    private static async Task<IResult> HandleMessages(
        HttpContext ctx, [FromBody] JsonElement body,
        GatewayService gw, ConfigService config, CancellationToken ct)
    {
        AnthropicRequest anthropicReq;
        try { anthropicReq = body.Deserialize<AnthropicRequest>(JsonOpts) ?? new(); }
        catch (JsonException ex)
        {
            return AnthropicError(400, $"invalid request body: {ex.Message}", "invalid_request_error");
        }
        if (string.IsNullOrEmpty(anthropicReq.Model))
            return AnthropicError(400, "model is required", "invalid_request_error");

        // model-level key permission
        if (!CheckModelPermission(ctx, config, anthropicReq.Model))
            return AnthropicError(403, "model not allowed for this api key", "invalid_request_error");

        var chatReq = AnthropicToChat(anthropicReq);
        // Sticky session engages ONLY on an explicit X-Session-Id header —
        // metadata.user_id no longer pins affinity (it defeated load balancing
        // because Claude Code / the Anthropic SDK always sends a stable user_id).
        chatReq.SessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault();
        chatReq.ExtraHeaders = ExtractForwardableHeaders(ctx.Request.Headers);
        var apiKeyName = ctx.Items["ApiKeyName"] as string ?? "";

        try
        {
            if (anthropicReq.Stream == true)
                return await StreamAnthropic(ctx, gw, chatReq, anthropicReq, apiKeyName, ct);
            else
                return await NonStreamAnthropic(gw, chatReq, anthropicReq, apiKeyName, ct);
        }
        catch (ModelNotFoundException ex) when (!ctx.Response.HasStarted)
        {
            return AnthropicError(404, ex.Message, "not_found_error");
        }
        catch (ServiceCapacityException ex) when (!ctx.Response.HasStarted)
        {
            return AnthropicError(503, ex.Message, "overloaded_error");
        }
        catch (UpstreamException ex) when (!ctx.Response.HasStarted)
        {
            return UpstreamErrorPassthrough(ex);
        }
        catch (Exception ex)
        {
            // Once the SSE stream has started, a JSON result would blow up with
            // "StatusCode cannot be set because the response has already started"
            // — emit a best-effort SSE error event instead.
            if (ctx.Response.HasStarted)
            {
                await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("api_error", ex.Message), ct);
                return Results.Empty;
            }
            return AnthropicError(500, ex.Message, "api_error");
        }
    }

    private static async Task<IResult> NonStreamAnthropic(
        GatewayService gw, ChatRequest chatReq, AnthropicRequest anthropicReq,
        string apiKeyName, CancellationToken ct)
    {
        try
        {
            var resp = await gw.ChatAsync(chatReq, apiKeyName, ct);
            return Results.Json(ChatToAnthropic(resp, anthropicReq), JsonOpts);
        }
        catch (ModelNotFoundException ex)
        {
            return AnthropicError(404, ex.Message, "not_found_error");
        }
        catch (ServiceCapacityException ex)
        {
            return AnthropicError(503, ex.Message, "overloaded_error");
        }
        catch (UpstreamException ex)
        {
            return UpstreamErrorPassthrough(ex);
        }
    }

    private static async Task<IResult> StreamAnthropic(
        HttpContext ctx, GatewayService gw, ChatRequest chatReq,
        AnthropicRequest anthropicReq, string apiKeyName, CancellationToken ct)
    {
        // Materialize the first chunk BEFORE starting SSE, so pre-stream failures
        // (unknown model, all services at capacity, upstream 4xx/5xx) get their
        // real HTTP status instead of a 200 + SSE error event.
        await using var iter = gw.StreamAsync(chatReq, apiKeyName, ct).GetAsyncEnumerator(ct);
        bool hasFirst;
        try
        {
            hasFirst = await iter.MoveNextAsync();
        }
        catch (ModelNotFoundException ex)
        {
            return AnthropicError(404, ex.Message, "not_found_error");
        }
        catch (ServiceCapacityException ex)
        {
            return AnthropicError(503, ex.Message, "overloaded_error");
        }
        catch (UpstreamException ex)
        {
            return UpstreamErrorPassthrough(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }

        // Anthropic content blocks require unique, monotonically increasing
        // indices — SDKs accumulate content by index. One counter assigns
        // thinking/text/tool_use blocks as they open.
        int nextBlockIndex = 0;
        int thinkingIdx = -1;   // open thinking block index, -1 = none
        int textIdx = -1;       // open text block index
        var toolBlockIdx = new Dictionary<int, int>();   // OpenAI tool_calls index → block index
        string? finishReason = null;
        int inputTokens = 0;
        int outputTokens = 0;

        // The first chunk from a Claude upstream already carries input_tokens —
        // put it in message_start like the real API does.
        if (hasFirst && iter.Current.Usage is { } u0 && u0.PromptTokens > 0)
            inputTokens = u0.PromptTokens;

        await SseWriter.StartSseAsync(ctx.Response);
        var msgId = "msg_" + Guid.NewGuid().ToString("N");
        var startPayload = new
        {
            type = "message_start",
            message = new
            {
                id = msgId,
                type = "message",
                role = "assistant",
                model = anthropicReq.Model,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = inputTokens, output_tokens = 0 },
            },
        };
        await WriteEventAsync(ctx.Response, "message_start", startPayload, ct);

        async Task CloseThinkingAsync()
        {
            if (thinkingIdx < 0) return;
            await WriteEventAsync(ctx.Response, "content_block_stop",
                new { type = "content_block_stop", index = thinkingIdx }, ct);
            thinkingIdx = -1;
        }

        async Task CloseTextAsync()
        {
            if (textIdx < 0) return;
            await WriteEventAsync(ctx.Response, "content_block_stop",
                new { type = "content_block_stop", index = textIdx }, ct);
            textIdx = -1;
        }

        async Task ProcessChunkAsync(StreamChunk chunk)
        {
            if (chunk.Usage is { } u)
            {
                if (u.PromptTokens > 0) inputTokens = u.PromptTokens;
                outputTokens = u.CompletionTokens > 0 ? u.CompletionTokens : outputTokens;
            }

            if (chunk.Choices is null || chunk.Choices.Count == 0) return;
            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (!string.IsNullOrEmpty(choice.FinishReason))
                finishReason = choice.FinishReason;

            // Reasoning (Claude thinking_delta or DeepSeek-style
            // reasoning_content) → Anthropic "thinking" content block.
            if (!string.IsNullOrEmpty(delta.ReasoningContent))
            {
                if (thinkingIdx < 0)
                {
                    thinkingIdx = nextBlockIndex++;
                    await WriteEventAsync(ctx.Response, "content_block_start",
                        new { type = "content_block_start", index = thinkingIdx,
                              content_block = new { type = "thinking", thinking = "" } }, ct);
                }
                await WriteEventAsync(ctx.Response, "content_block_delta",
                    new { type = "content_block_delta", index = thinkingIdx,
                          delta = new { type = "thinking_delta", thinking = delta.ReasoningContent } }, ct);
            }

            // Claude-upstream thinking signature → signature_delta, so the client
            // can echo a verifiable thinking block in later history.
            if (!string.IsNullOrEmpty(delta.ThinkingSignature))
            {
                if (thinkingIdx < 0)
                {
                    thinkingIdx = nextBlockIndex++;
                    await WriteEventAsync(ctx.Response, "content_block_start",
                        new { type = "content_block_start", index = thinkingIdx,
                              content_block = new { type = "thinking", thinking = "" } }, ct);
                }
                await WriteEventAsync(ctx.Response, "content_block_delta",
                    new { type = "content_block_delta", index = thinkingIdx,
                          delta = new { type = "signature_delta", signature = delta.ThinkingSignature } }, ct);
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                if (textIdx < 0)
                {
                    await CloseThinkingAsync();
                    textIdx = nextBlockIndex++;
                    await WriteEventAsync(ctx.Response, "content_block_start",
                        new { type = "content_block_start", index = textIdx,
                              content_block = new { type = "text", text = "" } }, ct);
                }
                await WriteEventAsync(ctx.Response, "content_block_delta",
                    new { type = "content_block_delta", index = textIdx,
                          delta = new { type = "text_delta", text = delta.Content } }, ct);
            }

            // Tool call deltas → Anthropic tool_use events.
            // OpenAI streams tool_calls as deltas with index/id/function.name/arguments.
            // Anthropic expects: content_block_start(tool_use) → input_json_delta → content_block_stop.
            if (delta.ToolCalls is { Count: > 0 })
            {
                await CloseThinkingAsync();
                await CloseTextAsync();
                foreach (var tc in delta.ToolCalls)
                {
                    // First delta for a tool call: send content_block_start.
                    if (!toolBlockIdx.ContainsKey(tc.Index) &&
                        (!string.IsNullOrEmpty(tc.Id) || !string.IsNullOrEmpty(tc.Function.Name)))
                    {
                        var bIdx = nextBlockIndex++;
                        toolBlockIdx[tc.Index] = bIdx;
                        await WriteEventAsync(ctx.Response, "content_block_start",
                            new
                            {
                                type = "content_block_start",
                                index = bIdx,
                                content_block = new
                                {
                                    type = "tool_use",
                                    id = tc.Id,
                                    name = tc.Function.Name,
                                    input = new { },
                                },
                            }, ct);
                    }

                    // Arguments delta → input_json_delta.
                    if (!string.IsNullOrEmpty(tc.Function.Arguments) &&
                        toolBlockIdx.TryGetValue(tc.Index, out var argIdx))
                    {
                        await WriteEventAsync(ctx.Response, "content_block_delta",
                            new
                            {
                                type = "content_block_delta",
                                index = argIdx,
                                delta = new { type = "input_json_delta", partial_json = tc.Function.Arguments },
                            }, ct);
                    }
                }
            }
        }

        try
        {
            if (hasFirst)
                await ProcessChunkAsync(iter.Current);
            while (await iter.MoveNextAsync())
                await ProcessChunkAsync(iter.Current);

            await CloseThinkingAsync();
            await CloseTextAsync();
            // Close any tool_use blocks that were started (in block order).
            foreach (var kv in toolBlockIdx.OrderBy(kv => kv.Value))
            {
                await WriteEventAsync(ctx.Response, "content_block_stop",
                    new { type = "content_block_stop", index = kv.Value }, ct);
            }

            // message_delta with usage and stop_reason — Claude Code relies on
            // this for token accounting and stream termination.
            var stopReason = finishReason switch
            {
                "stop" => "end_turn",
                "length" => "max_tokens",
                "tool_calls" => "tool_use",
                _ => "end_turn",
            };
            await WriteEventAsync(ctx.Response, "message_delta",
                new
                {
                    type = "message_delta",
                    delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
                    usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
                }, ct);

            await WriteEventAsync(ctx.Response, "message_stop",
                new { type = "message_stop" }, ct);
            await ctx.Response.Body.FlushAsync(ct);
            return Results.Empty;
        }
        catch (UpstreamException ex)
        {
            // Upstream failed mid-stream: emit the real provider error envelope
            // verbatim as an SSE data event, then end the stream.
            if (JsonBody(ex.UpstreamBody, out var json))
                await SseWriter.WriteRawSafeAsync(ctx.Response, json, ct);
            else
                await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("api_error", ex.Message), ct);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — nobody is listening; write nothing.
            return Results.Empty;
        }
        catch (Exception ex)
        {
            await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("api_error", ex.Message), ct);
            return Results.Empty;
        }
    }

    private static async Task WriteEventAsync(HttpResponse resp, string eventType, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await resp.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
    }

    private static IResult HandleCountTokens([FromBody] JsonElement body)
    {
        var req = body.Deserialize<AnthropicRequest>(JsonOpts);
        // Rough local estimator in quarter-token units: ~4 chars/token for ASCII,
        // ~1 token per CJK char (chars/4 badly underestimates Chinese text, which
        // misleads Claude Code's compaction threshold), plus per-message framing
        // and the tool schemas.
        long units = 0;
        void AddText(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (var ch in s)
                units += ch >= 0x2E80 ? 4 : 1;
        }
        foreach (var m in req?.Messages ?? new())
        {
            AddText(AnthropicRequest.ExtractText(m.Content));
            units += 4 * 4;   // role + framing ≈ 4 tokens per message
        }
        AddText(req?.SystemText());
        if (req?.Tools is { Count: > 0 } tools)
        {
            foreach (var t in tools)
            {
                AddText(t.Name);
                AddText(t.Description);
                if (t.InputSchema is not null) AddText(t.InputSchema.RootElement.GetRawText());
                units += 16 * 4;   // per-tool overhead
            }
        }
        return Results.Json(new { input_tokens = Math.Max(1, units / 4) });
    }

    // --- Anthropic <-> Chat conversion ---

    private static ChatRequest AnthropicToChat(AnthropicRequest a)
    {
        var req = new ChatRequest
        {
            Model = a.Model,
            // Preserve the client-facing alias for the call log — ApplyRedirect
            // later overwrites req.Model with the real upstream name, so
            // ClientModel is the only thing carrying the alias through to BuildLog.
            ClientModel = a.Model,
            Stream = a.Stream ?? false,
            MaxTokens = a.MaxTokens,
            Temperature = a.Temperature,
            TopP = a.TopP,
            TopK = a.TopK,
            Stop = a.StopSequences,
        };

        // Extended thinking request → unified reasoning config (a Claude-native
        // upstream re-emits it as the thinking parameter).
        if (a.Thinking is { } th && th.Type == "enabled")
            req.Reasoning = new ReasoningConfig { Enabled = true, MaxThinkingTokens = th.BudgetTokens };

        // System prompt (string or array of text blocks) → system message.
        var sys = a.SystemText();
        if (!string.IsNullOrEmpty(sys))
            req.Messages.Add(new ChatMessage { Role = "system", Content = sys, CacheControl = a.SystemCacheControl() });

        // Convert tools (Anthropic format → OpenAI format).
        if (a.Tools is { Count: > 0 })
        {
            req.Tools = a.Tools.Select(t => new Tool
            {
                Type = "function",
                Function = new FunctionDecl
                {
                    Name = t.Name ?? "",
                    Description = t.Description,
                    Parameters = t.InputSchema,
                }
            }).ToList();
        }
        // Convert tool_choice: Anthropic → OpenAI format.
        // Anthropic: {"type":"auto"} / {"type":"any"} / {"type":"tool","name":"x"}
        // OpenAI:    "auto" / "required" / {"type":"function","function":{"name":"x"}}
        if (a.ToolChoice is not null)
        {
            try
            {
                var tcJson = JsonSerializer.Serialize(a.ToolChoice, JsonOpts);
                using var tcDoc = JsonDocument.Parse(tcJson);
                var tcType = tcDoc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "auto";
                var oaiTc = tcType switch
                {
                    "auto" => JsonDocument.Parse("\"auto\""),
                    "any" => JsonDocument.Parse("\"required\""),
                    "tool" when tcDoc.RootElement.TryGetProperty("name", out var n) =>
                        JsonDocument.Parse($"{{\"type\":\"function\",\"function\":{{\"name\":\"{n}\"}}}}"),
                    _ => JsonDocument.Parse("\"auto\""),
                };
                req.ToolChoice = oaiTc;
            }
            catch { /* leave as null if conversion fails */ }
        }

        foreach (var m in a.Messages ?? new())
        {
            // Content can be a string, or an array of content blocks.
            // We must preserve tool_use and tool_result blocks for multi-turn
            // tool calling — not just extract text.
            if (m.Content is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var textParts = new List<string>();
                var toolCalls = new List<ToolCall>();
                var toolResults = new List<(string toolCallId, string content)>();
                var imageParts = new List<ContentPart>();
                var thinkingBlocks = new List<ThinkingBlockInfo>();
                string? textCacheControl = null;

                foreach (var block in je.EnumerateArray())
                {
                    var btype = block.TryGetProperty("type", out var t) ? t.GetString() : "text";
                    switch (btype)
                    {
                        case "text":
                            textParts.Add(block.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "");
                            // Preserve the Anthropic prompt-caching marker so the
                            // prefix survives the gateway round-trip (Zhipu GLM
                            // only caches blocks that carry cache_control).
                            if (block.TryGetProperty("cache_control", out var bcc)
                                && bcc.ValueKind == JsonValueKind.Object
                                && bcc.TryGetProperty("type", out var bct)
                                && bct.ValueKind == JsonValueKind.String)
                                textCacheControl = bct.GetString();
                            break;
                        case "thinking" when m.Role == "assistant":
                            // Preserve signed thinking blocks verbatim — a
                            // Claude-native upstream rejects thinking+tool_use
                            // turns whose thinking blocks were stripped.
                            thinkingBlocks.Add(new ThinkingBlockInfo
                            {
                                Thinking = block.TryGetProperty("thinking", out var thText) ? thText.GetString() ?? "" : "",
                                Signature = block.TryGetProperty("signature", out var thSig) ? thSig.GetString() : null,
                            });
                            break;
                        case "redacted_thinking" when m.Role == "assistant":
                            thinkingBlocks.Add(new ThinkingBlockInfo
                            {
                                RedactedData = block.TryGetProperty("data", out var rd) ? rd.GetString() : null,
                            });
                            break;
                        case "tool_use" when m.Role == "assistant":
                            // Convert to OpenAI tool_calls format.
                            toolCalls.Add(new ToolCall
                            {
                                Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                                Type = "function",
                                Function = new FunctionCall
                                {
                                    Name = block.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                                    Arguments = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}",
                                }
                            });
                            break;
                        case "tool_result":
                            // Convert to OpenAI role=tool message (handled below).
                            var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";
                            var resultContent = block.TryGetProperty("content", out var rc)
                                ? (rc.ValueKind == JsonValueKind.String ? rc.GetString() ?? ""
                                   : rc.ValueKind == JsonValueKind.Array
                                     ? string.Join("", rc.EnumerateArray()
                                         .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text")
                                         .Select(b => b.TryGetProperty("text", out var bt2) ? bt2.GetString() ?? "" : ""))
                                     : rc.GetRawText())
                                : "";
                            toolResults.Add((toolUseId, resultContent));
                            break;
                        case "image":
                            // Anthropic image block → OpenAI image_url part, so a
                            // Claude client can send images to an OpenAI upstream.
                            // base64 source becomes a data: URI; url source passes as-is.
                            if (block.TryGetProperty("source", out var src) &&
                                src.TryGetProperty("type", out var st))
                            {
                                var url = st.GetString() switch
                                {
                                    "base64" when src.TryGetProperty("media_type", out var mt) &&
                                                  src.TryGetProperty("data", out var d) =>
                                        $"data:{mt.GetString()};base64,{d.GetString()}",
                                    "url" when src.TryGetProperty("url", out var u) => u.GetString(),
                                    _ => null,
                                };
                                if (!string.IsNullOrEmpty(url))
                                    imageParts.Add(new ContentPart
                                    {
                                        Type = "image_url",
                                        ImageUrl = new ImageUrl { Url = url },
                                    });
                            }
                            break;
                    }
                }

                // If assistant message had tool_use, add as a single message with tool_calls.
                if (toolCalls.Count > 0)
                {
                    req.Messages.Add(new ChatMessage
                    {
                        Role = m.Role,
                        Content = string.Join('\n', textParts),
                        ToolCalls = toolCalls,
                        CacheControl = textCacheControl,
                        ThinkingBlocks = thinkingBlocks.Count > 0 ? thinkingBlocks : null,
                    });
                }
                // If user message had tool_results, emit each as a separate tool message.
                if (toolResults.Count > 0)
                {
                    foreach (var (toolCallId, content) in toolResults)
                    {
                        req.Messages.Add(new ChatMessage { Role = "tool", Content = content, ToolCallId = toolCallId });
                    }
                    // Also keep any text content from the same message as a user message.
                    if (textParts.Count > 0 && m.Role == "user")
                        req.Messages.Add(new ChatMessage { Role = "user", Content = string.Join('\n', textParts), CacheControl = textCacheControl });
                }
                // Normal text (possibly with images) message.
                if (toolCalls.Count == 0 && toolResults.Count == 0)
                {
                    if (imageParts.Count > 0)
                    {
                        // Multimodal: build the Parts list (text parts first, wire
                        // order preserved well enough for typical prompts).
                        var parts = textParts.Where(t2 => t2.Length > 0)
                            .Select(t2 => new ContentPart { Type = "text", Text = t2 })
                            .Concat(imageParts)
                            .ToList();
                        // Cache marker applies to the last text block (Anthropic
                        // caches from that block onward), so stamp it there too.
                        if (textCacheControl is not null && parts.Count > 0)
                            parts[^1].CacheControl = textCacheControl;
                        req.Messages.Add(new ChatMessage
                        {
                            Role = m.Role,
                            Content = string.Join('\n', textParts),
                            Parts = parts,
                            CacheControl = textCacheControl,
                            ThinkingBlocks = thinkingBlocks.Count > 0 ? thinkingBlocks : null,
                        });
                    }
                    else
                    {
                        req.Messages.Add(new ChatMessage
                        {
                            Role = m.Role,
                            Content = string.Join('\n', textParts),
                            CacheControl = textCacheControl,
                            ThinkingBlocks = thinkingBlocks.Count > 0 ? thinkingBlocks : null,
                        });
                    }
                }
            }
            else
            {
                // Simple string content.
                req.Messages.Add(new ChatMessage { Role = m.Role, Content = AnthropicRequest.ExtractText(m.Content) });
            }
        }
        return req;
    }

    private static object ChatToAnthropic(ChatResponse resp, AnthropicRequest req)
    {
        var choice = resp.Choices.FirstOrDefault();
        var msg = choice?.Message;

        // Build content blocks: thinking first (Anthropic requires it ahead of
        // text/tool_use), then text + tool_use (if the model called tools).
        var contentBlocks = new List<object>();
        if (!string.IsNullOrEmpty(msg?.ReasoningContent))
        {
            // Carry the signature through when present so the block round-trips
            // verifiably; signature-less reasoning (DeepSeek-style) is display-only.
            if (!string.IsNullOrEmpty(msg!.ThinkingSignature))
                contentBlocks.Add(new { type = "thinking", thinking = msg.ReasoningContent, signature = msg.ThinkingSignature });
            else
                contentBlocks.Add(new { type = "thinking", thinking = msg.ReasoningContent });
        }
        if (!string.IsNullOrEmpty(msg?.RedactedThinkingData))
            contentBlocks.Add(new { type = "redacted_thinking", data = msg!.RedactedThinkingData });
        if (!string.IsNullOrEmpty(msg?.Content))
            contentBlocks.Add(new { type = "text", text = msg.Content });

        if (msg?.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in msg.ToolCalls)
            {
                // Parse arguments JSON to an object for Anthropic's input field.
                object input;
                try { input = JsonSerializer.Deserialize<JsonDocument>(tc.Function.Arguments ?? "{}") ?? (object)new { }; }
                catch { input = new { }; }
                contentBlocks.Add(new
                {
                    type = "tool_use",
                    id = tc.Id,
                    name = tc.Function.Name,
                    input = input,
                });
            }
        }

        // If no content blocks at all, add an empty text block.
        if (contentBlocks.Count == 0)
            contentBlocks.Add(new { type = "text", text = "" });

        var stopReason = msg?.ToolCalls is { Count: > 0 } ? "tool_use" : ToAnthropicStop(choice?.FinishReason);

        return new
        {
            id = resp.Id,
            type = "message",
            role = "assistant",
            model = req.Model,
            content = contentBlocks,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = resp.Usage?.PromptTokens ?? 0,
                output_tokens = resp.Usage?.CompletionTokens ?? 0,
                cache_creation_input_tokens = resp.Usage?.CacheCreationInputTokens ?? 0,
                cache_read_input_tokens = resp.Usage?.CacheReadInputTokens ?? 0,
            },
        };
    }

    private static string ToAnthropicStop(string? fr) => fr switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        _ => "end_turn",
    };

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

    private static bool CheckModelPermission(HttpContext ctx, ConfigService config, string model)
    {
        var snap = config.Snapshot;
        if (snap.ApiKeys.Count == 0) return true;
        var keyValue = ctx.Items["ApiKey"] as string;
        var key = snap.ApiKeys.FirstOrDefault(k => k.KeyValue == keyValue);
        return key?.AllowsModel(model) ?? false;
    }

    private static IResult AnthropicError(int status, string message, string type) =>
        Results.Json(new { type = "error", error = new { type, message } },
            JsonOpts, statusCode: status);

    /// <summary>Pass through an upstream failure verbatim: reuse the upstream
    /// HTTP status and, when the upstream body is JSON, return it untouched so
    /// clients see the real provider error envelope (e.g. Anthropic
    /// {type:"error",error:{type,message}}). Falls back to the gateway envelope.</summary>
    private static IResult UpstreamErrorPassthrough(UpstreamException ex)
    {
        if (JsonBody(ex.UpstreamBody, out var json))
            return new StatusTextResult(json, "application/json", (int)ex.StatusCode);
        return AnthropicError((int)ex.StatusCode, ex.Message, "api_error");
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
}

// Anthropic request model
public class AnthropicRequest
{
    public string Model { get; set; } = "";
    public List<AnthropicMessage> Messages { get; set; } = new();
    /// <summary>System prompt — Anthropic allows either a string OR an array
    /// of content blocks ({"type":"text","text":"..."}). Kept as object to
    /// accept both; SystemText() extracts the text.</summary>
    public object? System { get; set; }
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public bool? Stream { get; set; }
    public List<string>? StopSequences { get; set; }
    /// <summary>Extended thinking config: {"type":"enabled","budget_tokens":N}.</summary>
    public AnthropicThinking? Thinking { get; set; }
    /// <summary>Anthropic metadata.user_id. NOTE: no longer used for sticky
    /// session affinity — only an explicit X-Session-Id header pins a session
    /// (see OpenAi/Anthropic endpoints). Kept on the request model for
    /// pass-through/observability only.</summary>
    public AnthropicMetadata? Metadata { get; set; }
    public List<AnthropicTool>? Tools { get; set; }
    public object? ToolChoice { get; set; }

    /// <summary>Extract text from system (string or content-block array).</summary>
    public string SystemText() => ExtractText(System);

    /// <summary>Cache-control type (e.g. "ephemeral") if any system content block
    /// carries a cache_control marker, else null. Used so the gateway re-emits
    /// the marker on the outbound Anthropic call — Zhipu GLM only caches blocks
    /// explicitly marked with cache_control.</summary>
    public string? SystemCacheControl()
    {
        if (System is not JsonElement je || je.ValueKind != JsonValueKind.Array) return null;
        foreach (var b in je.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("cache_control", out var cc) && cc.ValueKind == JsonValueKind.Object
                && cc.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        return null;
    }

    /// <summary>Robustly extract text from an Anthropic content field that may be
    /// a string, a string array, or an array of content blocks. Never throws.</summary>
    public static string ExtractText(object? content)
    {
        if (content is null) return "";
        if (content is string s) return s;
        if (content is not JsonElement je) return content.ToString() ?? "";

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? "",
            JsonValueKind.Array => string.Join('\n', je.EnumerateArray()
                .Select(b => b.ValueKind == JsonValueKind.Object
                    ? SafeGetStr(b, "text") ?? ""
                    : b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "")),
            _ => ""
        };
    }

    /// <summary>Safe GetString that returns null instead of throwing when the
    /// property is missing or its value isn't a string.</summary>
    private static string? SafeGetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

public class AnthropicMetadata
{
    public string? UserId { get; set; }
}

/// <summary>Anthropic extended-thinking request config.</summary>
public class AnthropicThinking
{
    public string Type { get; set; } = "";   // "enabled" | "disabled"
    public int? BudgetTokens { get; set; }
}

public class AnthropicMessage
{
    public string Role { get; set; } = "";
    /// <summary>Content: string OR array of content blocks (text/image/tool_use/
    /// tool_result). Kept as object to accept all shapes.</summary>
    public object? Content { get; set; }
}

public class AnthropicTool
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonDocument? InputSchema { get; set; }
}
