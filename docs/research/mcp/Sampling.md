# MCP Sampling

> Server-initiated LLM generation requests

## Overview

Sampling allows MCP servers to request LLM completions from the client. This enables **server-initiated agentic behaviors** where the server can leverage AI capabilities without needing direct API access to language models.

**Key principle**: The client maintains control over model access, selection, and permissions. Users must approve sampling requests.

## Capability Declaration

Clients supporting sampling **MUST** declare the capability during initialization:

```json
{
  "capabilities": {
    "sampling": {}
  }
}
```

## Request Flow

```
Server                                    Client                                    LLM
   │                                         │                                       │
   │─── sampling/createMessage ─────────────▶│                                       │
   │    (messages, modelPreferences,         │                                       │
   │     systemPrompt, maxTokens)            │                                       │
   │                                         │                                       │
   │                                         │──── User Approval ────▶               │
   │                                         │                                       │
   │                                         │─── API Request ──────────────────────▶│
   │                                         │                                       │
   │                                         │◀── Response ──────────────────────────│
   │                                         │                                       │
   │◀── result ──────────────────────────────│                                       │
   │    (role, content, model, stopReason)   │                                       │
```

## Protocol Methods

### Create Message

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "sampling/createMessage",
  "params": {
    "messages": [
      {
        "role": "user",
        "content": {
          "type": "text",
          "text": "What is the capital of France?"
        }
      }
    ],
    "modelPreferences": {
      "hints": [
        { "name": "claude-3-sonnet" }
      ],
      "intelligencePriority": 0.8,
      "speedPriority": 0.5,
      "costPriority": 0.3
    },
    "systemPrompt": "You are a helpful geography assistant.",
    "maxTokens": 100
  }
}
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "role": "assistant",
    "content": {
      "type": "text",
      "text": "The capital of France is Paris."
    },
    "model": "claude-3-sonnet-20240307",
    "stopReason": "endTurn"
  }
}
```

### Request Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `messages` | Yes | Conversation history as array of messages |
| `modelPreferences` | No | Hints for model selection |
| `systemPrompt` | No | System-level instructions |
| `maxTokens` | No | Maximum tokens to generate |
| `stopSequences` | No | Sequences that stop generation |
| `temperature` | No | Sampling temperature (0.0-1.0) |
| `includeContext` | No | Context inclusion strategy |
| `metadata` | No | Additional request metadata |

### Model Preferences

```json
{
  "modelPreferences": {
    "hints": [
      { "name": "claude-3-opus" },
      { "name": "claude-3-sonnet" },
      { "name": "gpt-4" }
    ],
    "intelligencePriority": 0.9,
    "speedPriority": 0.3,
    "costPriority": 0.2
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `hints` | array | Preferred models in order |
| `intelligencePriority` | number | 0.0-1.0, importance of capability |
| `speedPriority` | number | 0.0-1.0, importance of latency |
| `costPriority` | number | 0.0-1.0, importance of cost |

**Note**: Clients may ignore preferences and select any appropriate model.

### Message Format

```json
{
  "role": "user",
  "content": {
    "type": "text",
    "text": "Message content"
  }
}
```

#### Roles

| Role | Description |
|------|-------------|
| `user` | User message |
| `assistant` | Assistant response |

#### Content Types

**Text:**
```json
{ "type": "text", "text": "Hello, world!" }
```

**Image:**
```json
{ "type": "image", "data": "base64...", "mimeType": "image/png" }
```

**Audio:**
```json
{ "type": "audio", "data": "base64...", "mimeType": "audio/wav" }
```

### Response Fields

| Field | Description |
|-------|-------------|
| `role` | Always `"assistant"` |
| `content` | Generated content |
| `model` | Model that generated the response |
| `stopReason` | Why generation stopped |

#### Stop Reasons

| Reason | Description |
|--------|-------------|
| `endTurn` | Model completed naturally |
| `maxTokens` | Hit token limit |
| `stopSequence` | Hit a stop sequence |

## Context Inclusion

The `includeContext` parameter controls what additional context is included:

| Value | Description |
|-------|-------------|
| `"none"` | No additional context |
| `"thisServer"` | Include context from requesting server |
| `"allServers"` | Include context from all connected servers |

```json
{
  "params": {
    "messages": [...],
    "includeContext": "thisServer"
  }
}
```

## Human-in-the-Loop

Clients **SHOULD** implement user approval for sampling requests:

1. Display the sampling request to user
2. Show which server is requesting
3. Allow user to approve/deny
4. Optionally allow user to modify the request
5. Show the response before returning to server

### User Controls

Users **MUST** maintain control over:

- Whether sampling occurs at all
- The actual prompt sent to the model
- What results are returned to the server

## Error Handling

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32601,
    "message": "Sampling not supported",
    "data": {
      "reason": "Client does not have sampling capability"
    }
  }
}
```

| Code | Meaning |
|------|---------|
| -32601 | Client doesn't support sampling |
| -32602 | Invalid parameters |
| -32603 | Internal error (e.g., LLM API failure) |

## Security Considerations

1. **Always require user approval** for sampling requests
2. **Display full request details** before approval
3. **Limit what servers can see** of the final prompt
4. **Don't expose sensitive context** automatically
5. **Rate limit** sampling requests per server
6. **Log all sampling activity** for audit

## Use Cases

### Agentic Workflows

Server can create multi-step reasoning:

```json
{
  "messages": [
    {
      "role": "user",
      "content": { "type": "text", "text": "Analyze this codebase and suggest improvements" }
    },
    {
      "role": "assistant",
      "content": { "type": "text", "text": "I'll analyze the code structure first..." }
    },
    {
      "role": "user",
      "content": { "type": "text", "text": "Here's what I found in the analysis: [results]" }
    }
  ],
  "systemPrompt": "You are a code review expert. Provide actionable suggestions."
}
```

### Dynamic Tool Selection

Server requests help choosing which tool to use:

```json
{
  "messages": [
    {
      "role": "user",
      "content": {
        "type": "text",
        "text": "Given these available tools: [list], which should I use for: [task]?"
      }
    }
  ],
  "maxTokens": 50
}
```
