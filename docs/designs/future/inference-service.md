# Inference Service Design

## North Star

LLM inference as a cloud service — same auth, same infrastructure as the embedding service. The LLM can call tools; the client executes them. Bidirectional streaming: server streams content, pauses for tool calls, client sends results, server continues.

## Context

RepoQL already has LLM-powered features (explain, query summarization) using OpenRouter. These run through the host's `ILlmProvider` — one HTTP call per LLM turn, tool results fed back in a loop. This works but ties LLM inference to the host process and a single provider.

A cloud inference service decouples LLM access from the host. The server handles the LLM API mechanics (auth, streaming, conversation management). The client handles tool execution — it has the RepoQL graph, the indexed files, the read tool. Neither side does both.

**Initial provider:** Grok 4.1 Fast (xAI). Two variants: `grok-4-1-fast-reasoning` (thinking tokens, multi-step planning) and `grok-4-1-fast-non-reasoning` (instant responses). Both have 2M token context, function calling, and streaming. xAI positions these as their best tool-calling models. See [research/grok-4-1-fast.md](../research/grok-4-1-fast.md) for API details and pricing.

**Shares with embedding service:** Auth pattern (Bearer → SHA-256 hash), deploy workflow structure, WIF SA, infrastructure conventions.

## Constraints

- **Client-side tool execution** — the client has the data (RepoQL graph, indexed files). The server has the LLM. Tool calls cross the wire: server requests, client executes, client returns results
- **max_tokens is soft** — guidance to the LLM, not a hard server-side cutoff. The LLM should respect it; the server won't truncate
- **max_rounds is hard** — server-enforced safety limit on the agentic loop. Prevents runaway tool chains
- **Provider-agnostic proto** — Grok first, but the wire format works for any provider with chat completions and function calling
- **Client defines available tools** — the client knows what it can execute. Tool definitions travel with the request. The server presents them to the LLM and relays calls back
- **No server-side state** — each bidi stream is self-contained. The server doesn't remember previous conversations

---

## The gRPC API

```protobuf
syntax = "proto3";

package repoql.inference.v1;

option csharp_namespace = "RepoQL.Inference";

// LLM inference with client-side tool execution.
//
// Bidirectional streaming: the client sends a request, the server
// streams content. When the LLM calls a tool, the server sends a
// ToolRequest and waits. The client executes the tool and sends
// back a ToolResponse. The server feeds the result to the LLM
// and continues streaming.
//
// For pure completion (no tools), omit tool definitions in the
// request. The server streams content and completion — the client
// never needs to send anything after the initial request.
service InferenceService {
  rpc Complete(stream ClientMessage) returns (stream ServerMessage);
}

// ─── Client → Server ──────────────────────────────────────────────

message ClientMessage {
  oneof message {
    // First message on the stream. Exactly one required.
    CompleteRequest request = 1;

    // Tool execution result. Sent in response to a ToolRequest.
    ToolResponse tool_response = 2;
  }
}

message CompleteRequest {
  // The conversation to complete. At minimum, one user message.
  // Multi-turn: include prior assistant messages for context.
  repeated Message messages = 1;

  // System prompt. Prepended to the conversation.
  string system = 2;

  // Model identifier. Empty = server default.
  // Examples: "grok-3", "grok-3-mini", "grok-3-mini-fast".
  string model = 3;

  // Guidance for response length. The LLM should aim for this
  // but the server will not truncate. 0 = no guidance.
  int32 max_tokens = 4;

  // Sampling temperature. 0.0 = deterministic. Range: [0.0, 2.0].
  // Negative = server default.
  float temperature = 5;

  // Tools the client can execute. Empty = no tools (pure completion).
  // The server presents these to the LLM and relays calls back.
  repeated ToolDefinition tools = 6;

  // Maximum tool rounds before the server forces a final response.
  // A round is one LLM turn that produces tool calls.
  // 0 = server default.
  int32 max_rounds = 7;
}

message Message {
  Role role = 1;
  string content = 2;
}

enum Role {
  ROLE_UNSPECIFIED = 0;
  USER = 1;
  ASSISTANT = 2;
}

// ─── Tool Definitions ─────────────────────────────────────────────

// Client-provided tool definition. The server passes these to the
// LLM as function definitions. When the LLM calls one, the server
// sends a ToolRequest and the client executes it.
message ToolDefinition {
  // Tool name. Must be unique within the request.
  string name = 1;

  // Human-readable description. Helps the LLM decide when to use it.
  string description = 2;

  // JSON Schema for the tool's parameters.
  // Passed directly to the LLM provider's function calling API.
  string parameters_json = 3;
}

// Client's response to a ToolRequest.
message ToolResponse {
  // Must match the call_id from the ToolRequest.
  string call_id = 1;

  // Tool output. Fed back to the LLM as the tool result.
  string content = 2;

  // If true, the tool call failed. Content is the error message.
  // The LLM sees the error and can adjust its approach.
  bool is_error = 3;
}

// ─── Server → Client ──────────────────────────────────────────────

message ServerMessage {
  oneof message {
    // Incremental response text.
    ContentDelta content_delta = 1;

    // Reasoning/thinking tokens (if model supports it).
    // Not part of the response — separated for display purposes.
    ThinkingDelta thinking_delta = 2;

    // The LLM wants to call a tool. The server is waiting
    // for a ToolResponse with the matching call_id.
    ToolRequest tool_request = 3;

    // Final event. Always last on the stream.
    Completion completion = 10;
  }
}

message ContentDelta {
  string text = 1;
}

message ThinkingDelta {
  string text = 1;
}

// Server requesting the client to execute a tool call.
// The server blocks until the client sends a ToolResponse
// with the matching call_id.
message ToolRequest {
  // Unique identifier for this tool call. The client must
  // include this in the ToolResponse.
  string call_id = 1;

  // Which tool round (1-indexed).
  int32 round = 2;

  // Tool name (matches a ToolDefinition.name from the request).
  string tool = 3;

  // Tool arguments as JSON, as produced by the LLM.
  string arguments_json = 4;
}

message Completion {
  StopReason stop_reason = 1;
  Usage usage = 2;
  // Model actually used.
  string model = 3;
}

enum StopReason {
  STOP_REASON_UNSPECIFIED = 0;
  // Natural end of generation.
  STOP = 1;
  // LLM stopped due to max_tokens.
  MAX_TOKENS = 2;
  // Hit max_rounds — server forced a final response without tools.
  TOOL_LIMIT = 3;
}

message Usage {
  int32 input_tokens = 1;
  int32 output_tokens = 2;
  // Tokens consumed by tool results injected into LLM context.
  int32 tool_tokens = 3;
  // Reasoning/thinking tokens (not billed as output by most providers).
  int32 thinking_tokens = 4;
}
```

---

## How It Works

### The Flow

```
Client                              Server                          LLM (Grok)
  │                                   │                               │
  │─── CompleteRequest ──────────────►│                               │
  │    (messages, system, tools)      │── build prompt + tools ──────►│
  │                                   │                               │
  │◄── ThinkingDelta ────────────────│◄── thinking tokens ───────────│
  │◄── ContentDelta ─────────────────│◄── response tokens ───────────│
  │◄── ContentDelta ─────────────────│◄── ... ───────────────────────│
  │                                   │                               │
  │                                   │◄── tool_call: read ──────────│
  │◄── ToolRequest(call_id, read) ───│                               │
  │                                   │          (waiting)            │
  │    (executes read tool locally)   │                               │
  │                                   │                               │
  │─── ToolResponse(call_id, data) ─►│── inject result ─────────────►│
  │                                   │                               │
  │◄── ContentDelta ─────────────────│◄── continues generating ──────│
  │◄── ContentDelta ─────────────────│◄── ... ───────────────────────│
  │                                   │                               │
  │◄── Completion ───────────────────│◄── stop ─────────────────────│
```

### Multiple Tool Calls Per Round

Some models produce multiple tool calls in a single turn. The server sends all `ToolRequest` messages for the round, then waits for all corresponding `ToolResponse` messages before continuing. The client can execute them in parallel.

```
Server: ToolRequest(call_id="a", round=1, tool="read", args={uri: "file:///src/A.cs"})
Server: ToolRequest(call_id="b", round=1, tool="read", args={uri: "file:///src/B.cs"})
Client: ToolResponse(call_id="a", content="...")
Client: ToolResponse(call_id="b", content="...")
Server: ContentDelta("Based on both files...")
```

### Pure Completion (No Tools)

When `tools` is empty, the stream is simple — the client sends one `CompleteRequest` and reads `ServerMessage` events until `Completion`. No bidirectional interaction needed.

```
Client: CompleteRequest(messages, model, max_tokens)
Server: ThinkingDelta("...")
Server: ContentDelta("The answer is...")
Server: ContentDelta("...")
Server: Completion(stop_reason=STOP)
```

---

## The Read Tool

The primary tool. Defined by the client, executed by the client. The server just relays.

**Typical definition sent by a RepoQL host:**

```json
{
  "name": "read",
  "description": "Read content from the repository. Returns text with line numbers. Supports URI fragments: #line=42,60 for line ranges, #symbol=ClassName.Method for symbols. Append ' => modifier' for views: structure (signatures), headline (summaries), history (git log), blame (attribution).",
  "parameters": {
    "type": "object",
    "properties": {
      "uri": {
        "type": "string",
        "description": "URI to read (e.g., file:///src/Auth.cs#symbol=ValidateToken)"
      },
      "budget": {
        "type": "integer",
        "description": "Maximum tokens to return. Controls detail level."
      }
    },
    "required": ["uri"]
  }
}
```

The client receives the `ToolRequest`, calls its local RepoQL read tool, and returns the content in the `ToolResponse`. The server feeds it to the LLM as a tool result. The server never sees or understands the tool semantics — it's a pure relay.

This means any tool the client can execute works. `read` is the obvious first tool, but `explore`, `query`, or custom tools work identically — define them in `tools`, handle the `ToolRequest`, return a `ToolResponse`.

---

## Client Usage Patterns

### Simple completion (no tools)

```
stream = client.Complete()
stream.send(CompleteRequest(
    messages=[Message(USER, "Explain dependency injection")],
    model="grok-3-mini-fast",
    max_tokens=1000
))
for event in stream:
    if event.content_delta: print(event.content_delta.text, end="")
    if event.completion: break
```

### Code question with read tool

```
stream = client.Complete()
stream.send(CompleteRequest(
    messages=[Message(USER, "How does token refresh work?")],
    system="Answer questions about this codebase with citations.",
    tools=[ToolDefinition(
        name="read",
        description="Read from the repository...",
        parameters_json=READ_TOOL_SCHEMA
    )],
    max_rounds=5
))
for event in stream:
    if event.content_delta:
        print(event.content_delta.text, end="")
    if event.tool_request:
        result = repoql.read(event.tool_request.arguments_json)
        stream.send(ToolResponse(
            call_id=event.tool_request.call_id,
            content=result
        ))
    if event.completion:
        break
```

### No tools (explicit)

```
stream.send(CompleteRequest(
    messages=[Message(USER, "Summarize this: ...")],
    tools=[]  # empty = pure completion, no tool calls possible
))
```

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Bidirectional streaming | Server-streaming + multi-turn reconnect | One connection per logical operation. No reconnection between tool rounds. Server manages LLM conversation state. |
| Client-side tool execution | Server-side execution | The client has the data (RepoQL graph). The server has the LLM. Neither needs what the other has. Clean separation. |
| Client-defined tools | Server-defined tools | The client knows what it can execute. Different clients may offer different tools. The server is a generic LLM relay. |
| Soft max_tokens | Hard enforcement | The LLM should aim for the budget. Truncating mid-thought produces worse output than a slightly long response. |
| Hard max_rounds | Soft guidance | Unbounded tool loops are a real risk. This is a safety limit, not a quality knob. |
| Provider-agnostic proto | Grok-specific | Wire format works for any chat-completion provider. Model field routes to backends. |
| ToolRequest/ToolResponse with call_id | Positional matching | Parallel tool calls within a round need stable identity. call_id is the LLM provider's tool call ID passed through. |
| Thinking tokens as separate events | Mixed into content | Reasoning tokens aren't the answer. Separate them so clients can display or discard independently. |

---

## Extension Points

- **New providers** — add Anthropic, OpenAI, Gemini behind the same proto. The `model` field routes.
- **New tools** — any tool the client can define and execute works. `explore`, `query`, `search` — just add the definition and handle `ToolRequest`.
- **Structured output** — add `response_format` field for JSON schema constraints.
- **Multimodal** — extend `Message` with a `parts` field for images, audio.
- **Server-side tools** — some tools could execute server-side (web search, API calls). Add a `server_tools` field for tools the server handles without round-tripping to the client.

---

## Related

- [Embedding Service Design](../current/cloud-embedding-cache.md) — sibling service, same infrastructure pattern
- [Operations Guide](../current/cloud-embedding-cache-ops.md) — auth, secrets, deploy patterns (reusable for inference)
- [OpenRouterLlmProvider](../../src/RepoQL.LLM.Client/OpenRouterLlmProvider.cs) — existing tool loop implementation
- [embedding.proto](../../src/RepoQL.Embedding.Proto/Protos/embedding.proto) — proto conventions
