using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using YuSwitch.Gateway;
using YuSwitch.Models;
using YuSwitch.Providers.OpenAI;
using YuSwitch.Services;

namespace YuSwitch.Endpoints;

/// <summary>
/// OpenAI Responses API inbound: /v1/responses. Accepts the Responses request
/// shape (used by Codex CLI with wire_api="responses"), converts to the unified
/// ChatRequest, routes through the gateway, and converts back — non-streaming
/// as a Response object, streaming as typed SSE events (response.created →
/// output_item.added → output_text.delta / function_call_arguments.delta →
/// response.completed). The gateway is stateless: store/previous_response_id
/// semantics are not supported (Codex sends store=false with full history).
/// </summary>
public static class ResponsesEndpoints
{
    public static IEndpointRouteBuilder MapResponsesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1");
        g.MapPost("/responses", HandleResponses);
        return app;
    }

    private static async Task<IResult> HandleResponses(
        HttpContext ctx, [FromBody] JsonElement body,
        GatewayService gw, ConfigService config, CancellationToken ct)
    {
        ResponsesRequest req;
        try { req = body.Deserialize<ResponsesRequest>(JsonOpts) ?? new(); }
        catch (JsonException ex)
        {
            return Error(400, $"invalid request body: {ex.Message}", "invalid_request_error");
        }
        if (string.IsNullOrEmpty(req.Model))
            return Error(400, "model is required", "invalid_request_error");
        // Stateless gateway: we never store responses, so a previous_response_id
        // can't be resolved. Codex sends store=false + full history, so this
        // only rejects clients that genuinely depend on server-side state.
        if (!string.IsNullOrEmpty(req.PreviousResponseId))
            return Error(400, "previous_response_id is not supported: the gateway is stateless; send the full conversation in `input`", "invalid_request_error");

        if (!CheckModelPermission(ctx, config, req.Model))
            return Error(403, "model not allowed for this api key", "invalid_request_error");

        ChatRequest chatReq;
        try { chatReq = ResponsesToChat(req); }
        catch (Exception ex)
        {
            return Error(400, $"invalid input: {ex.Message}", "invalid_request_error");
        }
        chatReq.SessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault();
        chatReq.ExtraHeaders = ExtractForwardableHeaders(ctx.Request.Headers);
        var apiKeyName = ctx.Items["ApiKeyName"] as string ?? "";

        try
        {
            if (req.Stream == true)
                return await StreamResponses(ctx, gw, chatReq, req, apiKeyName, ct);
            return await NonStreamResponses(gw, chatReq, req, apiKeyName, ct);
        }
        catch (ModelNotFoundException ex) when (!ctx.Response.HasStarted)
        {
            return Error(404, ex.Message, "invalid_request_error");
        }
        catch (ServiceCapacityException ex) when (!ctx.Response.HasStarted)
        {
            return Error(503, ex.Message, "server_error");
        }
        catch (UpstreamException ex) when (!ctx.Response.HasStarted)
        {
            return UpstreamErrorPassthrough(ex);
        }
        catch (Exception ex)
        {
            // Once the SSE stream has started a JSON result can't set the status
            // code any more — emit a best-effort SSE error event instead.
            if (ctx.Response.HasStarted)
            {
                await SseWriter.WriteErrorSafeAsync(ctx.Response, new StreamError("api_error", ex.Message), ct);
                return Results.Empty;
            }
            return Error(500, ex.Message, "api_error");
        }
    }

    private static async Task<IResult> NonStreamResponses(
        GatewayService gw, ChatRequest chatReq, ResponsesRequest req,
        string apiKeyName, CancellationToken ct)
    {
        var resp = await gw.ChatAsync(chatReq, apiKeyName, ct);
        return Results.Json(ChatToResponses(resp, req), JsonOpts);
    }

    // --- Responses -> Chat conversion ---

    private static ChatRequest ResponsesToChat(ResponsesRequest r)
    {
        var req = new ChatRequest
        {
            Model = r.Model,
            // Preserve the client-facing alias for the call log — ApplyRedirect
            // later overwrites req.Model with the real upstream name.
            ClientModel = r.Model,
            Stream = r.Stream ?? false,
            MaxTokens = r.MaxOutputTokens,
            Temperature = r.Temperature,
            TopP = r.TopP,
            ParallelToolCalls = r.ParallelToolCalls,
        };

        if (!string.IsNullOrEmpty(r.Instructions))
            req.Messages.Add(new ChatMessage { Role = "system", Content = r.Instructions });

        // reasoning.effort → ReasoningConfig. Parsed and carried on the request
        // (providers that understand it can forward it upstream).
        if (r.Reasoning?.Effort is { Length: > 0 } effort)
        {
            req.Reasoning = new ReasoningConfig
            {
                Enabled = true,
                Effort = effort.ToLowerInvariant() switch
                {
                    "minimal" => ReasoningEffort.Minimal,
                    "low" => ReasoningEffort.Low,
                    "medium" => ReasoningEffort.Medium,
                    "high" => ReasoningEffort.High,
                    "xhigh" => ReasoningEffort.XHigh,
                    _ => ReasoningEffort.Medium,
                },
            };
        }

        // Tools: Responses uses a FLAT function tool ({type,name,parameters,...});
        // chat completions nests it under "function". Non-function tool types
        // are skipped EXCEPT web_search, which the gateway itself serves (see
        // WebSearchService.EnrichAsync) — it's carried through so the request
        // triggers the gateway-side search rather than being silently dropped.
        if (r.Tools is { Count: > 0 })
        {
            var tools = new List<Tool>();
            foreach (var t in r.Tools)
            {
                if (t.Type == "web_search")
                {
                    tools.Add(new Tool
                    {
                        Type = "web_search",
                        ExtensionData = t.ExtensionData, // keep max_results etc.
                    });
                    continue;
                }
                if (t.Type is null or "function" && !string.IsNullOrEmpty(t.Name))
                {
                    tools.Add(new Tool
                    {
                        Type = "function",
                        Function = new FunctionDecl
                        {
                            Name = t.Name!,
                            Description = t.Description,
                            Parameters = t.Parameters,
                            Strict = t.Strict ?? false,
                        }
                    });
                }
            }
            if (tools.Count > 0) req.Tools = tools;
        }

        // tool_choice: "auto"/"required"/"none" pass through; the Responses
        // function form {"type":"function","name":"x"} becomes the chat nested
        // form {"type":"function","function":{"name":"x"}}.
        if (r.ToolChoice is JsonElement tc)
        {
            try
            {
                if (tc.ValueKind == JsonValueKind.String)
                    req.ToolChoice = JsonDocument.Parse(tc.GetRawText());
                else if (tc.ValueKind == JsonValueKind.Object &&
                         tc.TryGetProperty("type", out var tct) && tct.GetString() == "function" &&
                         tc.TryGetProperty("name", out var tcn))
                    req.ToolChoice = JsonDocument.Parse(
                        JsonSerializer.Serialize(new { type = "function", function = new { name = tcn.GetString() } }));
            }
            catch { /* leave as null if conversion fails */ }
        }

        // input: plain string → single user message.
        if (r.Input is JsonElement input)
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                req.Messages.Add(new ChatMessage { Role = "user", Content = input.GetString() ?? "" });
            }
            else if (input.ValueKind == JsonValueKind.Array)
            {
                ConvertInputItems(input, req.Messages);
            }
        }
        return req;
    }

    /// <summary>
    /// Converts a Responses `input` item array to chat messages. Responses
    /// flattens an assistant turn into sibling items (reasoning, message,
    /// function_call×N); chat completions wants the function calls merged into
    /// ONE assistant message with tool_calls[], so consecutive function_call
    /// items are buffered and flushed together.
    /// </summary>
    private static void ConvertInputItems(JsonElement input, List<ChatMessage> messages)
    {
        var pendingToolCalls = new List<ToolCall>();

        void FlushToolCalls()
        {
            if (pendingToolCalls.Count == 0) return;
            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = new(pendingToolCalls) });
            pendingToolCalls.Clear();
        }

        foreach (var item in input.EnumerateArray())
        {
            var itype = item.TryGetProperty("type", out var t) ? t.GetString() : "message";
            switch (itype)
            {
                case "message" or null:
                {
                    FlushToolCalls();
                    var role = item.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user";
                    if (role == "developer") role = "system"; // Responses' developer role ≈ system
                    messages.Add(ConvertMessageItem(item, role));
                    break;
                }
                case "function_call":
                {
                    pendingToolCalls.Add(new ToolCall
                    {
                        Id = SafeGetStr(item, "call_id") ?? SafeGetStr(item, "id") ?? "",
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = SafeGetStr(item, "name") ?? "",
                            Arguments = SafeGetStr(item, "arguments") ?? "{}",
                        },
                    });
                    break;
                }
                case "function_call_output":
                {
                    FlushToolCalls();
                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = SafeGetStr(item, "call_id") ?? "",
                        Content = ExtractOutputText(item),
                    });
                    break;
                }
                case "reasoning":
                    // Skip transparently (don't flush: Codex interleaves
                    // reasoning items between function_calls of one turn).
                    // ChatMessage has no outbound reasoning field and upstreams
                    // reject re-injected reasoning anyway.
                    break;
                // Unknown item types are ignored (forward-compat).
            }
        }
        FlushToolCalls();
    }

    /// <summary>Converts a Responses message item (content: string or array of
    /// input_text/output_text/input_image parts) into a ChatMessage.</summary>
    private static ChatMessage ConvertMessageItem(JsonElement item, string role)
    {
        if (!item.TryGetProperty("content", out var content))
            return new ChatMessage { Role = role, Content = "" };

        if (content.ValueKind == JsonValueKind.String)
            return new ChatMessage { Role = role, Content = content.GetString() ?? "" };

        var textParts = new List<string>();
        var imageParts = new List<ContentPart>();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var ptype = part.TryGetProperty("type", out var pt) ? pt.GetString() : "input_text";
                switch (ptype)
                {
                    case "input_text" or "output_text" or "text":
                        textParts.Add(SafeGetStr(part, "text") ?? "");
                        break;
                    case "input_image":
                        // image_url is a plain string (URL or data: URI).
                        var url = SafeGetStr(part, "image_url");
                        if (!string.IsNullOrEmpty(url))
                            imageParts.Add(new ContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new ImageUrl { Url = url, Detail = SafeGetStr(part, "detail") },
                            });
                        break;
                }
            }
        }

        var text = string.Join('\n', textParts);
        if (imageParts.Count > 0)
        {
            var parts = textParts.Where(s => s.Length > 0)
                .Select(s => new ContentPart { Type = "text", Text = s })
                .Concat(imageParts)
                .ToList();
            return new ChatMessage { Role = role, Content = text, Parts = parts };
        }
        return new ChatMessage { Role = role, Content = text };
    }

    /// <summary>function_call_output.output: string, or array of content parts
    /// — extract text robustly, falling back to raw JSON.</summary>
    private static string ExtractOutputText(JsonElement item)
    {
        if (!item.TryGetProperty("output", out var output)) return "";
        return output.ValueKind switch
        {
            JsonValueKind.String => output.GetString() ?? "",
            JsonValueKind.Array => string.Join("", output.EnumerateArray()
                .Select(p => SafeGetStr(p, "text") ?? "")),
            _ => output.GetRawText(),
        };
    }

    // --- Chat -> Responses conversion (non-streaming) ---

    private static object ChatToResponses(ChatResponse resp, ResponsesRequest req)
    {
        var choice = resp.Choices.FirstOrDefault();
        var msg = choice?.Message;

        var output = new List<object>();
        if (!string.IsNullOrEmpty(msg?.ReasoningContent))
            output.Add(ReasoningItem(NewId("rs"), msg.ReasoningContent));
        if (!string.IsNullOrEmpty(msg?.Content))
            output.Add(MessageItem(NewId("msg"), msg.Content));
        foreach (var tc in msg?.ToolCalls ?? new())
            output.Add(FunctionCallItem(NewId("fc"), tc.Id, tc.Function.Name, tc.Function.Arguments ?? "{}"));

        return BuildResponseObject(
            id: "resp_" + (string.IsNullOrEmpty(resp.Id) ? Guid.NewGuid().ToString("N") : resp.Id),
            createdAt: resp.Created > 0 ? resp.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status: "completed",
            req: req,
            output: output,
            usage: UsageObject(resp.Usage));
    }

    /// <summary>The full Response object shape. Echo fields are filled from the
    /// request so strict deserializers (Codex is Rust/serde) don't trip on
    /// missing members; nulls are omitted (WhenWritingNull) which serde treats
    /// as None.</summary>
    private static object BuildResponseObject(
        string id, long createdAt, string status, ResponsesRequest req,
        List<object> output, object? usage, object? error = null)
    {
        return new
        {
            id,
            @object = "response",
            created_at = createdAt,
            status,
            background = false,
            error,
            incomplete_details = (object?)null,
            instructions = req.Instructions,
            max_output_tokens = req.MaxOutputTokens,
            model = req.Model,
            output,
            parallel_tool_calls = req.ParallelToolCalls ?? true,
            previous_response_id = (string?)null,
            reasoning = new { effort = req.Reasoning?.Effort, summary = req.Reasoning?.Summary },
            store = req.Store ?? false,
            temperature = req.Temperature ?? 1.0f,
            text = new { format = new { type = "text" } },
            tool_choice = req.ToolChoice ?? (object)"auto",
            tools = (object?)req.Tools ?? Array.Empty<object>(),
            top_p = req.TopP ?? 1.0f,
            truncation = "disabled",
            usage,
            user = (string?)null,
            metadata = new { },
        };
    }

    private static object UsageObject(Usage? u) => new
    {
        input_tokens = u?.PromptTokens ?? 0,
        input_tokens_details = new { cached_tokens = u?.CacheReadInputTokens ?? 0 },
        output_tokens = u?.CompletionTokens ?? 0,
        output_tokens_details = new { reasoning_tokens = u?.ReasoningTokens ?? 0 },
        total_tokens = u?.TotalTokens ?? 0,
    };

    private static object MessageItem(string id, string text, string status = "completed") => new
    {
        id,
        type = "message",
        status,
        role = "assistant",
        content = new object[] { new { type = "output_text", text, annotations = Array.Empty<object>() } },
    };

    private static object FunctionCallItem(string id, string callId, string name, string arguments, string status = "completed") => new
    {
        id,
        type = "function_call",
        status,
        call_id = callId,
        name,
        arguments,
    };

    private static object ReasoningItem(string id, string summaryText) => new
    {
        id,
        type = "reasoning",
        summary = new object[] { new { type = "summary_text", text = summaryText } },
    };

    // --- Streaming ---

    /// <summary>
    /// Translates the internal OpenAI-style chunk stream into Responses SSE.
    /// Event framing is `event: <type>\ndata: <json>\n\n` with a monotonically
    /// increasing sequence_number, ending with response.completed (NO
    /// `data: [DONE]` sentinel — the Responses protocol doesn't use it).
    /// </summary>
    private static async Task<IResult> StreamResponses(
        HttpContext ctx, GatewayService gw, ChatRequest chatReq,
        ResponsesRequest req, string apiKeyName, CancellationToken ct)
    {
        await SseWriter.StartSseAsync(ctx.Response);

        var responseId = "resp_" + Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;
        var outputIndex = -1;          // next item allocates ++outputIndex
        Usage? usage = null;

        // Item states. Message/reasoning items are exclusive-open (closed when
        // the next kind starts); function_call items stay open until stream end
        // so parallel tool calls don't clobber each other.
        string? msgItemId = null; var msgIndex = 0; var textBuf = new StringBuilder();
        string? rsItemId = null; var rsIndex = 0; var rsBuf = new StringBuilder();
        var toolItems = new Dictionary<int, ToolItemState>(); // key: upstream delta index
        var completedItems = new List<object>();              // for response.completed

        async Task Emit(string type, object payload) =>
            await WriteEventAsync(ctx.Response, type, payload, ct);

        async Task CloseReasoning()
        {
            if (rsItemId is null) return;
            var text = rsBuf.ToString();
            await Emit("response.reasoning_summary_text.done", new
            { type = "response.reasoning_summary_text.done", sequence_number = seq++, item_id = rsItemId, output_index = rsIndex, summary_index = 0, text });
            await Emit("response.reasoning_summary_part.done", new
            { type = "response.reasoning_summary_part.done", sequence_number = seq++, item_id = rsItemId, output_index = rsIndex, summary_index = 0, part = new { type = "summary_text", text } });
            var item = ReasoningItem(rsItemId, text);
            await Emit("response.output_item.done", new
            { type = "response.output_item.done", sequence_number = seq++, output_index = rsIndex, item });
            completedItems.Add(item);
            rsItemId = null;
        }

        async Task CloseMessage()
        {
            if (msgItemId is null) return;
            var text = textBuf.ToString();
            await Emit("response.output_text.done", new
            { type = "response.output_text.done", sequence_number = seq++, item_id = msgItemId, output_index = msgIndex, content_index = 0, text });
            await Emit("response.content_part.done", new
            { type = "response.content_part.done", sequence_number = seq++, item_id = msgItemId, output_index = msgIndex, content_index = 0, part = new { type = "output_text", text, annotations = Array.Empty<object>() } });
            var item = MessageItem(msgItemId, text);
            await Emit("response.output_item.done", new
            { type = "response.output_item.done", sequence_number = seq++, output_index = msgIndex, item });
            completedItems.Add(item);
            msgItemId = null;
        }

        try
        {
            await Emit("response.created", new
            {
                type = "response.created", sequence_number = seq++,
                response = BuildResponseObject(responseId, createdAt, "in_progress", req, new(), null),
            });
            await Emit("response.in_progress", new
            {
                type = "response.in_progress", sequence_number = seq++,
                response = BuildResponseObject(responseId, createdAt, "in_progress", req, new(), null),
            });

            await foreach (var chunk in gw.StreamAsync(chatReq, apiKeyName, ct))
            {
                if (chunk.Usage is { } u) usage = u;
                if (chunk.Choices is null || chunk.Choices.Count == 0) continue;
                var delta = chunk.Choices[0].Delta;

                // Reasoning content → reasoning item + summary_text deltas.
                if (!string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    if (rsItemId is null)
                    {
                        rsItemId = NewId("rs"); rsIndex = ++outputIndex;
                        await Emit("response.output_item.added", new
                        { type = "response.output_item.added", sequence_number = seq++, output_index = rsIndex,
                          item = new { id = rsItemId, type = "reasoning", summary = Array.Empty<object>() } });
                        await Emit("response.reasoning_summary_part.added", new
                        { type = "response.reasoning_summary_part.added", sequence_number = seq++, item_id = rsItemId, output_index = rsIndex, summary_index = 0,
                          part = new { type = "summary_text", text = "" } });
                    }
                    rsBuf.Append(delta.ReasoningContent);
                    await Emit("response.reasoning_summary_text.delta", new
                    { type = "response.reasoning_summary_text.delta", sequence_number = seq++, item_id = rsItemId, output_index = rsIndex, summary_index = 0, delta = delta.ReasoningContent });
                }

                // Text content → message item + output_text deltas.
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    if (msgItemId is null)
                    {
                        await CloseReasoning(); // reasoning precedes the answer
                        msgItemId = NewId("msg"); msgIndex = ++outputIndex;
                        await Emit("response.output_item.added", new
                        { type = "response.output_item.added", sequence_number = seq++, output_index = msgIndex,
                          item = new { id = msgItemId, type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() } });
                        await Emit("response.content_part.added", new
                        { type = "response.content_part.added", sequence_number = seq++, item_id = msgItemId, output_index = msgIndex, content_index = 0,
                          part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
                    }
                    textBuf.Append(delta.Content);
                    await Emit("response.output_text.delta", new
                    { type = "response.output_text.delta", sequence_number = seq++, item_id = msgItemId, output_index = msgIndex, content_index = 0, delta = delta.Content });
                }

                // Tool call deltas → function_call items.
                if (delta.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in delta.ToolCalls)
                    {
                        if (!toolItems.TryGetValue(tc.Index, out var state))
                        {
                            // First delta for this tool call: close open text/
                            // reasoning items, then open a function_call item.
                            await CloseReasoning();
                            await CloseMessage();
                            state = new ToolItemState
                            {
                                ItemId = NewId("fc"),
                                OutputIndex = ++outputIndex,
                                CallId = string.IsNullOrEmpty(tc.Id) ? NewId("call") : tc.Id,
                                Name = tc.Function.Name,
                            };
                            toolItems[tc.Index] = state;
                            await Emit("response.output_item.added", new
                            { type = "response.output_item.added", sequence_number = seq++, output_index = state.OutputIndex,
                              item = new { id = state.ItemId, type = "function_call", status = "in_progress", call_id = state.CallId, name = state.Name, arguments = "" } });
                        }
                        if (string.IsNullOrEmpty(state.Name) && !string.IsNullOrEmpty(tc.Function.Name))
                            state.Name = tc.Function.Name;
                        if (!string.IsNullOrEmpty(tc.Function.Arguments))
                        {
                            state.Args.Append(tc.Function.Arguments);
                            await Emit("response.function_call_arguments.delta", new
                            { type = "response.function_call_arguments.delta", sequence_number = seq++, item_id = state.ItemId, output_index = state.OutputIndex, delta = tc.Function.Arguments });
                        }
                    }
                }
            }

            // Stream ended: close whatever is still open, in item order.
            await CloseReasoning();
            await CloseMessage();
            foreach (var state in toolItems.Values.OrderBy(s => s.OutputIndex))
            {
                var args = state.Args.Length > 0 ? state.Args.ToString() : "{}";
                await Emit("response.function_call_arguments.done", new
                { type = "response.function_call_arguments.done", sequence_number = seq++, item_id = state.ItemId, output_index = state.OutputIndex, arguments = args });
                var item = FunctionCallItem(state.ItemId, state.CallId, state.Name, args);
                await Emit("response.output_item.done", new
                { type = "response.output_item.done", sequence_number = seq++, output_index = state.OutputIndex, item });
                completedItems.Add(item);
            }

            await Emit("response.completed", new
            {
                type = "response.completed", sequence_number = seq++,
                response = BuildResponseObject(responseId, createdAt, "completed", req, completedItems, UsageObject(usage)),
            });
            await ctx.Response.Body.FlushAsync(ct);
            return Results.Empty;
        }
        catch (UpstreamException ex)
        {
            // Upstream failed mid-stream → still terminate with response.failed
            // (established ordering), but carry the real provider error envelope
            // verbatim as the response.error when it parses as JSON.
            object? error = new { code = ex.IsServerError ? "server_error" : "upstream_error", message = ex.Message };
            if (JsonBody(ex.UpstreamBody, out var json))
            {
                using var doc = JsonDocument.Parse(json);
                error = doc.RootElement.Clone();
            }
            var failed = BuildResponseObject(responseId, createdAt, "failed", req, completedItems, null, error: error);
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await WriteEventAsync(ctx.Response, "response.failed",
                        new { type = "response.failed", sequence_number = seq++, response = failed }, CancellationToken.None);
                    await ctx.Response.Body.FlushAsync(CancellationToken.None);
                }
                catch { /* client disconnected mid-write */ }
            }
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — nobody is listening; write nothing.
            return Results.Empty;
        }
        catch (Exception ex)
        {
            // Stream already started → embed the error in a failed Response.
            var failed = BuildResponseObject(responseId, createdAt, "failed", req, completedItems, null,
                error: new { code = "server_error", message = ex.Message });
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await WriteEventAsync(ctx.Response, "response.failed",
                        new { type = "response.failed", sequence_number = seq++, response = failed }, CancellationToken.None);
                }
                catch { /* client disconnected mid-write */ }
            }
            return Results.Empty;
        }
    }

    private class ToolItemState
    {
        public string ItemId = "";
        public int OutputIndex;
        public string CallId = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static async Task WriteEventAsync(HttpResponse resp, string eventType, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await resp.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
    }

    private static string? SafeGetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

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

    private static IResult Error(int status, string message, string type) =>
        Results.Json(new ErrorResponse { Error = new ErrorDetail { Message = message, Type = type } },
            JsonOpts, statusCode: status);

    /// <summary>Pass through an upstream failure verbatim: reuse the upstream
    /// HTTP status and, when the upstream body is JSON, return it untouched so
    /// clients see the real provider error envelope. Falls back to the gateway
    /// envelope.</summary>
    private static IResult UpstreamErrorPassthrough(UpstreamException ex)
    {
        if (JsonBody(ex.UpstreamBody, out var json))
            return new StatusTextResult(json, "application/json", (int)ex.StatusCode);
        return Error((int)ex.StatusCode, ex.Message, "upstream_error");
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

// --- Responses API request model (lenient POCOs; unknown fields are ignored
// by the default deserializer, matching the AnthropicRequest convention) ---

public class ResponsesRequest
{
    public string Model { get; set; } = "";
    public string? Instructions { get; set; }
    /// <summary>string OR array of input items (message / function_call /
    /// function_call_output / reasoning). Kept as object to accept both.</summary>
    public object? Input { get; set; }
    public List<ResponsesTool>? Tools { get; set; }
    /// <summary>"auto"/"required"/"none" OR {type:"function",name:"x"}.</summary>
    public object? ToolChoice { get; set; }
    public bool? ParallelToolCalls { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? Stream { get; set; }
    /// <summary>Accepted and ignored — the gateway never stores responses.</summary>
    public bool? Store { get; set; }
    /// <summary>Not supported (stateless gateway) — non-empty is a 400.</summary>
    public string? PreviousResponseId { get; set; }
    public ResponsesReasoning? Reasoning { get; set; }
    /// <summary>Accepted and ignored.</summary>
    public List<string>? Include { get; set; }
}

/// <summary>Responses function tool — FLAT shape, unlike chat completions'
/// nested {type,function:{...}}.</summary>
public class ResponsesTool
{
    public string? Type { get; set; } = "function";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonDocument? Parameters { get; set; }
    public bool? Strict { get; set; }
    /// <summary>Additional fields (e.g. web_search's max_results) preserved for
    /// the gateway-side web search handler.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class ResponsesReasoning
{
    public string? Effort { get; set; }   // minimal/low/medium/high
    public string? Summary { get; set; }  // auto/concise/detailed
}
