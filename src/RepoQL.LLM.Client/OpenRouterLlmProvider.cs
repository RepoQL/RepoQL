using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Configuration;

namespace RepoQL.LLM.Client;

/// <summary>
/// LLM provider implementation using OpenRouter API with Gemini Flash.
/// </summary>
public sealed class OpenRouterLlmProvider : ILlmProvider, IDisposable
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultModel = "@preset/understand";
    private const int MaxToolCalls = 3;
    private const int DefaultTimeoutSeconds = 120;  // Longer for thinking models

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool Enabled => !string.IsNullOrEmpty(_apiKey);
    public string Model => _model;

    public OpenRouterLlmProvider(
        string? apiKey = null,
        RepoQlConfig.LlmSettings? settings = null,
        string? model = null,
        HttpClient? httpClient = null,
        ILogger<OpenRouterLlmProvider>? logger = null)
    {
        _apiKey = apiKey ?? settings?.ApiKey ?? "";
        _model = model ?? DefaultModel;
        _logger = logger ?? NullLogger<OpenRouterLlmProvider>.Instance;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds) };
            _ownsHttpClient = true;
        }

        if (Enabled)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://repoql.dev");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "RepoQL");
        }
    }

    public async Task<string> SummarizeAsync(
        string jsonData,
        string intent,
        int maxTokens = 500,
        string? repoTree = null,
        CancellationToken ct = default)
    {
        var result = await SummarizeWithReasoningAsync(jsonData, intent, maxTokens, repoTree, ct);
        return result.Content;
    }

    public async Task<LlmSummaryResult> SummarizeWithReasoningAsync(
        string jsonData,
        string intent,
        int maxTokens = 500,
        string? repoTree = null,
        CancellationToken ct = default)
    {
        if (!Enabled)
            return new LlmSummaryResult("LLM not configured (set llm.api_key)");

        try
        {
            var toon = JsonToToonConverter.Convert(jsonData);
            var prompt = LlmPromptTemplates.BuildSummarizePrompt(toon, intent, maxTokens, repoTree);

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }
            };
            var response = await CallApiAsync(messages, tools: null, ct);

            // Extract reasoning trace if available
            string? reasoning = response.ReasoningContent;
            if (string.IsNullOrEmpty(reasoning) && response.ReasoningDetails.HasValue)
            {
                reasoning = ExtractReasoningText(response.ReasoningDetails.Value);
            }

            return new LlmSummaryResult(
                response.Content ?? "No response from LLM",
                reasoning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SummarizeWithReasoningAsync");
            return new LlmSummaryResult($"Error: {ex.Message}");
        }
    }

    public async Task<string> ExtractAsync(
        string jsonData,
        string intent,
        Func<string, int, string> readUri,
        CancellationToken ct = default)
    {
        if (!Enabled)
            return "LLM not configured (set llm.api_key)";

        try
        {
            var toon = JsonToToonConverter.Convert(jsonData);
            // Use simple extraction prompt without tools for now
            // Tool calling has compatibility issues across providers
            var prompt = LlmPromptTemplates.BuildExtractPrompt(toon, intent);

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }
            };
            var response = await CallApiAsync(messages, tools: null, ct);

            return response.Content ?? "No response from LLM";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ExtractAsync");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
    {
        if (!Enabled)
            return question; // Fallback to original

        try
        {
            var prompt = $"""
                Extract search keywords from this question. Return ONLY space-separated keywords, no explanation.
                Include technical terms, class names, function names that might appear in code.

                Question: {question}

                Keywords:
                """;

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            };
            var response = await CallApiAsync(messages, tools: null, ct);

            var keywords = response.Content?.Trim();
            return string.IsNullOrWhiteSpace(keywords) ? question : keywords;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keyword extraction failed, using original question");
            return question; // Fallback to original
        }
    }

    private static object[] BuildToolDefinitions()
    {
        return
        [
            new
            {
                type = "function",
                function = new
                {
                    name = "read_uri",
                    description = "Read content from a repository URI. Use when you need actual code/content that isn't in the search results. Returns the content with line numbers.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            uri = new
                            {
                                type = "string",
                                description = "The repository URI (e.g., file:///src/Auth.cs or file:///src/Auth.cs#line=42)"
                            },
                            context_lines = new
                            {
                                type = "integer",
                                description = "Lines of context around target line (default 5)"
                            }
                        },
                        required = new[] { "uri" }
                    }
                }
            }
        ];
    }

    private async Task<string> CallApiWithToolsAsync(
        PromptPair prompt,
        object[] tools,
        Func<string, int, string> readUri,
        CancellationToken ct)
    {
        // Use JsonArray for consistent serialization (mixing JsonElement with anonymous objects causes issues)
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = prompt.System },
            new JsonObject { ["role"] = "user", ["content"] = prompt.User }
        };
        var toolCallCount = 0;

        while (true)
        {
            var response = await CallApiAsync(messages, tools, ct);

            // Check for API errors (returned as content starting with [API ERROR])
            if (response.Content?.StartsWith("[API ERROR", StringComparison.Ordinal) == true)
            {
                return response.Content;
            }

            // Check if we have tool calls
            if (response.ToolCalls is { Count: > 0 })
            {
                if (toolCallCount >= MaxToolCalls)
                {
                    _logger.LogWarning("Max tool calls ({Max}) reached, returning partial result", MaxToolCalls);
                    return response.Content ?? "Max tool calls reached";
                }

                // Add assistant message with tool calls - MUST preserve reasoning_details for Gemini
                // The raw message contains all fields including reasoning_details/thoughtSignature
                if (response.RawMessage.HasValue)
                {
                    // Convert JsonElement to JsonNode to preserve all fields (reasoning_details, etc.)
                    var rawNode = JsonNode.Parse(response.RawMessage.Value.GetRawText());
                    messages.Add(rawNode);
                }
                else
                {
                    // Fallback: construct message manually (may not work for reasoning models)
                    var toolCallsArray = new JsonArray();
                    foreach (var tc in response.ToolCalls)
                    {
                        toolCallsArray.Add(new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = tc.Arguments
                            }
                        });
                    }
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = response.Content,
                        ["tool_calls"] = toolCallsArray
                    });
                }

                // Execute each tool call
                foreach (var toolCall in response.ToolCalls)
                {
                    toolCallCount++;
                    _logger.LogInformation("Executing tool call: {Name} with args: {Args}", toolCall.Name, toolCall.Arguments);
                    var toolResult = ExecuteToolCall(toolCall, readUri);
                    _logger.LogInformation("Tool result length: {Length}", toolResult?.Length ?? 0);

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolCall.Id,
                        ["content"] = toolResult
                    });
                }

                continue; // Let LLM process tool results
            }

            // No more tool calls, return final response
            return response.Content ?? "No response from LLM";
        }
    }

    private string ExecuteToolCall(ToolCall toolCall, Func<string, int, string> readUri)
    {
        try
        {
            if (toolCall.Name != "read_uri")
                return $"Unknown tool: {toolCall.Name}";

            // Parse arguments flexibly using JsonDocument
            using var doc = JsonDocument.Parse(toolCall.Arguments);
            var root = doc.RootElement;

            string? uri = null;
            int contextLines = 5;

            // Try multiple property name formats (snake_case, camelCase, etc.)
            if (root.TryGetProperty("uri", out var uriProp))
                uri = uriProp.GetString();
            else if (root.TryGetProperty("Uri", out uriProp))
                uri = uriProp.GetString();

            if (root.TryGetProperty("context_lines", out var ctxProp))
                contextLines = ctxProp.GetInt32();
            else if (root.TryGetProperty("contextLines", out ctxProp))
                contextLines = ctxProp.GetInt32();
            else if (root.TryGetProperty("ContextLines", out ctxProp))
                contextLines = ctxProp.GetInt32();

            if (string.IsNullOrEmpty(uri))
                return "Error: uri parameter is required";

            _logger.LogDebug("Executing read_uri: {Uri} with {Context} context lines", uri, contextLines);

            var result = readUri(uri, contextLines);
            _logger.LogDebug("read_uri returned {Length} chars", result?.Length ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool call {ToolName}", toolCall.Name);
            return $"Error executing {toolCall.Name}: {ex.Message}";
        }
    }

    private async Task<LlmResponse> CallApiAsync(
        JsonArray messages,
        object[]? tools,
        CancellationToken ct)
    {
        // Build request as JsonObject for consistent serialization
        // Clone messages to avoid "node already has parent" error on subsequent calls
        var messagesClone = JsonNode.Parse(messages.ToJsonString())!.AsArray();
        var request = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = messagesClone
            // temperature, max_tokens, include_reasoning controlled by OpenRouter preset
        };

        // Add tools if provided
        if (tools is { Length: > 0 })
        {
            request["tools"] = JsonSerializer.SerializeToNode(tools, JsonOptions);
        }

        var json = request.ToJsonString(JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogDebug("Calling OpenRouter API with model {Model}, messages: {Count}", _model, messages.Count);
        if (messages.Count > 1)
        {
            // Log full request for debugging tool calls
            _logger.LogInformation("Full request JSON:\n{Json}", json.Length > 8000 ? json[..8000] + "..." : json);
        }

        var httpResponse = await _httpClient.PostAsync(Endpoint, content, ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("OpenRouter API error {StatusCode}: {Body}", httpResponse.StatusCode, errorBody);
            // Return error as content - this will be visible in the SQL output
            return new LlmResponse { Content = $"[API ERROR {httpResponse.StatusCode}]: {(errorBody.Length > 500 ? errorBody[..500] : errorBody)}" };
        }

        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

        // Log response for debugging
        if (messages.Count > 1)
        {
            _logger.LogInformation("API response (truncated): {Response}", responseBody.Length > 2000 ? responseBody[..2000] + "..." : responseBody);
        }

        // Parse with JsonDocument for flexibility
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            _logger.LogWarning("No choices in API response: {Response}", responseBody.Length > 500 ? responseBody[..500] : responseBody);
            return new LlmResponse { Content = "No response from API" };
        }

        var choice = choices[0];
        string? responseContent = null;
        string? reasoningContent = null;
        List<ToolCall>? toolCalls = null;
        JsonElement? reasoningDetails = null;
        JsonElement? rawMessage = null;

        if (choice.TryGetProperty("message", out var message))
        {
            // Preserve the raw message for multi-turn with reasoning models
            rawMessage = message.Clone();

            if (message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var rawContent = contentProp.GetString();
                // Decode XML entities that some models may return in code snippets
                responseContent = rawContent is not null ? DecodeXmlEntities(rawContent) : null;
            }

            // Capture reasoning content for thinking models (Kimi K2, etc.)
            if (message.TryGetProperty("reasoning_content", out var rc))
                reasoningContent = ExtractReasoningText(rc);
            else if (message.TryGetProperty("reasoning", out var r))
                reasoningContent = ExtractReasoningText(r);

            // Capture reasoning_details for Gemini/reasoning models - must be preserved for tool calls
            if (message.TryGetProperty("reasoning_details", out var rd))
                reasoningDetails = rd.Clone();

            if (message.TryGetProperty("tool_calls", out var toolCallsArray) && toolCallsArray.ValueKind == JsonValueKind.Array)
            {
                toolCalls = new List<ToolCall>();
                foreach (var tc in toolCallsArray.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                    var funcName = "";
                    var funcArgs = "{}";

                    if (tc.TryGetProperty("function", out var func))
                    {
                        if (func.TryGetProperty("name", out var nameProp))
                            funcName = nameProp.GetString() ?? "";
                        if (func.TryGetProperty("arguments", out var argsProp))
                            funcArgs = argsProp.GetString() ?? "{}";
                    }

                    toolCalls.Add(new ToolCall { Id = id, Name = funcName, Arguments = funcArgs });
                }
            }
        }

        return new LlmResponse
        {
            Content = responseContent,
            ToolCalls = toolCalls,
            ReasoningDetails = reasoningDetails,
            ReasoningContent = reasoningContent,
            RawMessage = rawMessage
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    /// <summary>
    /// Extract reasoning text from various formats (string, array of objects with text field).
    /// </summary>
    private static string? ExtractReasoningText(JsonElement element)
    {
        // String format - return directly
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        // Array format (Kimi K2 thinking): [{"type":"reasoning.text","text":"..."},...]
        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                {
                    var text = textProp.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
            }
            return parts.Count > 0 ? string.Join("\n\n", parts) : null;
        }

        return null;
    }

    /// <summary>
    /// Decode only the 5 XML special character entities.
    /// More targeted than WebUtility.HtmlDecode which handles all HTML entities.
    /// </summary>
    private static string DecodeXmlEntities(string text)
    {
        if (!text.Contains('&'))
            return text;

        return text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    #region Internal Types

    private sealed class LlmResponse
    {
        public string? Content { get; set; }
        public List<ToolCall>? ToolCalls { get; set; }
        /// <summary>
        /// Raw reasoning details from Gemini/other reasoning models - must be preserved for tool calls.
        /// </summary>
        public JsonElement? ReasoningDetails { get; set; }
        /// <summary>
        /// Reasoning content text for thinking models (Kimi K2, etc.)
        /// </summary>
        public string? ReasoningContent { get; set; }
        /// <summary>
        /// The raw assistant message element - used to preserve all fields for multi-turn.
        /// </summary>
        public JsonElement? RawMessage { get; set; }
    }

    private sealed class ToolCall
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Arguments { get; set; }
    }

    #endregion
}
