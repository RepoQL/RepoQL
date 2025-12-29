using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.LLM.Client;

/// <summary>
/// LLM provider implementation using OpenRouter API with Gemini Flash.
/// </summary>
public sealed class OpenRouterLlmProvider : ILlmProvider, IDisposable
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultModel = "moonshotai/kimi-k2";
    private const int MaxToolCalls = 3;
    private const int DefaultTimeoutSeconds = 60;

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
        string? model = null,
        HttpClient? httpClient = null,
        ILogger<OpenRouterLlmProvider>? logger = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
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
        CancellationToken ct = default)
    {
        if (!Enabled)
            return "LLM not configured (set OPENROUTER_API_KEY environment variable)";

        try
        {
            var toon = JsonToToonConverter.Convert(jsonData);
            var prompt = LlmPromptTemplates.BuildSummarizePrompt(toon, intent, maxTokens);

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            };
            var response = await CallApiAsync(messages, tools: null, ct);

            return response.Content ?? "No response from LLM";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SummarizeAsync");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> ExtractAsync(
        string jsonData,
        string intent,
        Func<string, int, string> readUri,
        CancellationToken ct = default)
    {
        if (!Enabled)
            return "LLM not configured (set OPENROUTER_API_KEY environment variable)";

        try
        {
            var toon = JsonToToonConverter.Convert(jsonData);
            // Use simple extraction prompt without tools for now
            // Tool calling has compatibility issues across providers
            var prompt = LlmPromptTemplates.BuildExtractPrompt(toon, intent);

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
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
        string prompt,
        object[] tools,
        Func<string, int, string> readUri,
        CancellationToken ct)
    {
        // Use JsonArray for consistent serialization (mixing JsonElement with anonymous objects causes issues)
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = prompt }
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
            ["messages"] = messagesClone,
            ["max_tokens"] = 4096
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
        List<ToolCall>? toolCalls = null;
        JsonElement? reasoningDetails = null;
        JsonElement? rawMessage = null;

        if (choice.TryGetProperty("message", out var message))
        {
            // Preserve the raw message for multi-turn with reasoning models
            rawMessage = message.Clone();

            if (message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                responseContent = contentProp.GetString();

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
            RawMessage = rawMessage
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
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
