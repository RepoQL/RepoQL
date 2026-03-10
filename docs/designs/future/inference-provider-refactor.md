---
description: Design for replacing ILlmProvider with IInferenceProvider — clean interface for the cloud inference service
tags: [design, inference, refactor, llm, interface]
audience: { human: 50, agent: 50 }
purpose: { design: 100 }
---

# IInferenceProvider Design

> Replaces `ILlmProvider` with a clean interface shaped by the inference service's actual capabilities.
> Prerequisite for [Plan 03: Host Integration](../../plans/future/inference-service/03-host-integration.md).

## Context

The host has `ILlmProvider` — an interface shaped by its first (and only) implementation, `OpenRouterLlmProvider`. It exposes `SummarizeAsync`, `ExtractAsync`, `ExtractKeywordsAsync` — method names that describe what OpenRouter happened to do, not what consumers actually need.

The inference service (Plans 01+02) exposes two RPCs: `Complete` (unary) and `CompleteWithTools` (bidi streaming). Every current `ILlmProvider` consumer maps cleanly onto one of these two operations. The interface should reflect that.

**Current consumers and what they actually do:**

| Consumer | Current call | What it actually needs |
|----------|-------------|----------------------|
| `RepoQlServiceImpl.Query()` | `SummarizeAsync(data, intent, maxTokens)` | Complete: data as context, intent as prompt |
| `RepoQlServiceImpl.Explain()` | `ExtractKeywordsAsync` + `SummarizeAsync` | Complete for keywords, CompleteWithTools for synthesis |
| `LlmUdf.Ask()` | `SummarizeAsync(data, intent, maxTokens)` | Complete: data as context, intent as prompt |
| `ReadOrchestrator` (question) | `SummarizeAsync(content, question, maxTokens, repoTree)` | Complete: content as context, question as prompt |
| `ExploreOrchestrator` | Injects `ILlmProvider?` | Dead code — never calls it. Remove |

**What gets deleted:**
- `ILlmProvider` interface
- `DisabledLlmProvider`
- `LlmSummaryResult` record
- `OpenRouterLlmProvider` (entire class)
- `RepoQL.LLM.Client` project (all contents are OpenRouter-specific or dead)
- `LlmUdf.Extract()` method (unused)
- `ExtractAsync` from interface (unused)
- `SummarizeWithReasoningAsync` from interface (only called internally by OpenRouter's `SummarizeAsync`)

## Constraints

- **No new project** — the `IInferenceProvider` interface and types live in `RepoQL.Contracts`. The gRPC client implementation lives in a new `RepoQL.Inference.Client` project (follows `RepoQL.Embedding.Client` pattern)
- **`=> question:` modifier still works** — the host's `ReadOrchestrator` uses unary `Complete` for the question modifier. The forwarded read tool for `CompleteWithTools` strips `=> question:` from description and rejects it at execution time
- **Sync UDF compatibility** — `LlmUdf.Ask()` blocks on `.GetAwaiter().GetResult()`. The interface stays async; the UDF continues to block. No special sync path needed
- **Test stubs change** — many test files implement `ILlmProvider`. All switch to `IInferenceProvider`. The stub pattern simplifies (Complete returns a string, no method zoo)

---

## Design

### The Interface

```csharp
namespace RepoQL.Contracts.Inference;

public interface IInferenceProvider
{
    /// Whether the inference service is configured and reachable.
    bool Available { get; }

    /// Simple completion — prompt in, response out. No tools.
    Task<InferenceResult> CompleteAsync(
        InferenceRequest request,
        CancellationToken ct = default);

    /// Completion with tool execution. The provider handles the
    /// streaming tool loop internally — the consumer provides tools
    /// and a callback to execute them.
    Task<InferenceResult> CompleteWithToolsAsync(
        InferenceRequest request,
        ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct = default);
}
```

Two methods, not five. One for each RPC the inference service exposes.

### Types

```csharp
namespace RepoQL.Contracts.Inference;

/// How hard to think. Maps to server-side model selection.
public enum InferenceEffort { Low, Balanced, High }

/// What to send to the LLM.
public record InferenceRequest
{
    public required string Prompt { get; init; }
    public string? Context { get; init; }
    public string? System { get; init; }
    public InferenceEffort Effort { get; init; } = InferenceEffort.Balanced;
    public int MaxTokens { get; init; }
}

/// What comes back.
public record InferenceResult
{
    public required string Content { get; init; }
    public string? Reasoning { get; init; }
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int ThinkingTokens { get; init; }
    public int ToolTokens { get; init; }
}

/// Tool configuration for CompleteWithTools.
public record ToolOptions
{
    public required IReadOnlyList<InferenceToolDefinition> Tools { get; init; }
    public int ToolTokenBudget { get; init; } = 30_000;
    public int MaxRounds { get; init; } = 5;
}

/// A tool the LLM can call.
public record InferenceToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ParametersJson { get; init; }
}

/// Server requesting the consumer to execute a tool.
public record ToolCall
{
    public required string CallId { get; init; }
    public required string Tool { get; init; }
    public required string ArgumentsJson { get; init; }
}

/// Consumer's response after executing the tool.
public record ToolCallResult
{
    public required string Content { get; init; }
    public bool IsError { get; init; }
    public int TokensUsed { get; init; }
}
```

### DisabledInferenceProvider

When the inference service is not configured, all LLM features degrade gracefully:

```csharp
public sealed class DisabledInferenceProvider : IInferenceProvider
{
    public bool Available => false;

    public Task<InferenceResult> CompleteAsync(InferenceRequest request, CancellationToken ct)
        => Task.FromResult(new InferenceResult
        {
            Content = "Inference service not configured (set inference.service_url and inference.api_key)"
        });

    public Task<InferenceResult> CompleteWithToolsAsync(
        InferenceRequest request, ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct)
        => CompleteAsync(request, ct);
}
```

### InferenceClient (gRPC implementation)

New project: `RepoQL.Inference.Client`. References `RepoQL.Inference.Proto` and `RepoQL.Contracts`.

```
InferenceClient : IInferenceProvider
├── CompleteAsync → creates CompleteRequest proto, calls InferenceService.Complete, maps Completion → InferenceResult
├── CompleteWithToolsAsync → opens bidi stream, sends CompleteRequest, loops: receive ToolRequests → call executeTool → send ToolResponses → until Completion
└── Available → true when service URL is configured (lazy channel creation)
```

**CompleteWithTools loop** (contained inside the client):
1. Send `CompleteRequest` as first `ClientMessage`
2. Read `ServerMessage`s from the stream
3. If `ToolRequest`: call `executeTool` callback, accumulate `ToolResponse`s
4. When all tool responses for a round are ready, send them as `ClientMessage`s
5. If `Completion`: return as `InferenceResult`
6. Handle parallel calls (`more_in_round`): execute concurrently, send all responses

The consumer never sees the stream. It provides tools and a callback, gets a result.

**Error mapping:**
- Service unreachable → `InferenceUnavailableException`
- gRPC status codes → typed exceptions consumers can catch
- Timeout → `InferenceTimeoutException`

### Configuration

New settings section in `RepoQlConfig`:

```csharp
public sealed class InferenceSettings
{
    [Setting("Inference service gRPC URL",
        RequiresRestart = true)]
    public string? ServiceUrl { get; set; }

    [Setting("Inference service API key",
        Sensitive = true, RequiresRestart = true)]
    public string? ApiKey { get; set; }

    [Setting("Default tool token budget for explain",
        DefaultValue = "30000")]
    public int ToolTokenBudget { get; set; } = 30_000;

    [Setting("Default max tool rounds for explain",
        DefaultValue = "5")]
    public int MaxRounds { get; set; } = 5;
}
```

Replaces `LlmSettings`. The `OPENROUTER_API_KEY` env var and `llm.api_key` config are removed.

### Consumer Migrations

**`RepoQlServiceImpl.Explain()`** — the main rewrite:

```
Before:
  1. ExtractKeywordsAsync(question) via OpenRouter
  2. explore(keywords, breadth=2, budget=50k)
  3. SummarizeAsync(exploreResults, question) via OpenRouter

After:
  1. CompleteAsync(prompt=question, effort=Low) → extract keywords
  2. explore(keywords, breadth=2, budget=50k) — unchanged
  3. CompleteWithToolsAsync(
       context=exploreResults, prompt=question, effort=High,
       tools=[read], executeTool=host read callback)
     → synthesized answer with follow-up reads
```

Step 3 is the key improvement — the LLM can now read specific files after seeing the broad explore context.

**`RepoQlServiceImpl.Query()`** — simple mapping:

```
Before: SummarizeAsync(formatted, intent, maxTokens)
After:  CompleteAsync(context=formatted, prompt=intent, maxTokens=budget)
```

**`LlmUdf.Ask()`** — same pattern:

```
Before: SummarizeAsync(jsonData, intent, maxTokens)
After:  CompleteAsync(context=jsonData, prompt=intent, maxTokens=budget)
```

**`ReadOrchestrator.ExecuteDirectLlmAsync()`** — same pattern:

```
Before: SummarizeAsync(contextWithFiles, question, maxTokens, repoTree)
After:  CompleteAsync(context=contextWithFiles+repoTree, prompt=question, maxTokens=budget)
```

**`ExploreOrchestrator`** — remove dead `ILlmProvider?` parameter.

### Read Tool Forwarding

The explain flow provides a `read` tool to the inference service. The tool definition is built at call time by stripping the `=> question:` modifier from the description. The execution callback:

1. Parses `arguments_json` to extract `uriGlob` and `tokenBudget`
2. Rejects if the URI contains `=> question:` (returns `is_error = true`)
3. Calls the host's `ReadOrchestrator` to execute the read
4. Counts tokens in the result using the host's tokenizer
5. Returns `ToolCallResult` with content and `tokens_used`

### DI Registration

In `RepoIndexerServiceCollectionExtensions`:

```csharp
// Replace ILlmProvider registration:
services.AddSingleton<IInferenceProvider>(sp =>
{
    var config = sp.GetRequiredService<RepoQlConfig>();
    var settings = config.Inference;
    if (string.IsNullOrWhiteSpace(settings.ServiceUrl) ||
        string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        return new DisabledInferenceProvider();
    }

    return new InferenceClient(
        settings.ServiceUrl,
        settings.ApiKey,
        sp.GetService<ILogger<InferenceClient>>());
});
```

### Cross-Cutting Concerns

**Error propagation** — when the inference service is down, every consumer gets a clear error. `Available` check lets consumers skip the call entirely (query comment summarization is optional; explain returns an error).

**Cancellation** — flows through: consumer cancels → client cancels gRPC stream → server cancels Grok call.

**Observability** — the client adds OTel spans for each call. The server already has its own spans. Together they trace from consumer intent through to Grok response.

---

## Trade-offs

| Chose | Over | Why |
|-------|------|-----|
| Two methods (Complete, CompleteWithTools) | One method with optional tools | Unary and bidi streaming are fundamentally different at the gRPC level. Consumers that don't need tools shouldn't pay the complexity |
| Callback for tool execution | Consumer manages the stream | The bidi streaming loop is complex. The client contains it — consumers never see gRPC streams |
| Own types in Contracts | Reuse proto types | Contracts can't depend on Proto. Clean separation between transport and domain |
| Delete ILlmProvider entirely | Adapter pattern | Adapter would preserve an interface shaped by the wrong abstraction. Clean break is cleaner |
| InferenceSettings replaces LlmSettings | Keep both | One provider, one config section. No migration period needed — nobody has the old config in production (it barely worked) |

## Alternatives Considered

**Adapter pattern** — implement `ILlmProvider` on top of the inference client. Rejected: preserves an interface shaped by OpenRouter's HTTP API, not by what consumers need. Adds a translation layer that maps between two models when we can just use the right model.

**Keep ExtractAsync** — the `readUri` callback pattern for tool use. Rejected: the callback was synchronous, the tool loop is async, and `ExtractAsync` in OpenRouter never actually used tools (`tools: null` on line 139). Dead code with a misleading signature.

**Separate keyword extraction method** — `ExtractKeywordsAsync` as a first-class method. Rejected: it's just a `CompleteAsync` call with `Effort.Low` and a prompt that says "extract keywords." No dedicated method needed.

## Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| Many test stubs need updating | Pattern is simpler — most stubs just return a canned string. Codex handles the mechanical changes |
| `LlmUdf.Ask()` blocks synchronously | Same as today — `.GetAwaiter().GetResult()`. The interface stays async; the UDF blocks. No behavioral change |
| Cloud service latency vs. local OpenRouter | Grok is fast (4.1 Fast). Explain gets better answers via tool loop. Net positive |
| `RepoQL.LLM.Client` deletion removes `OpenRouterEmbeddingProvider` | Dead code (only self-references). Already replaced by Voyage AI embedding service |
