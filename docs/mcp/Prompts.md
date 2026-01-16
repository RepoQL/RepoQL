# MCP Prompts

> Templated message sequences for structured AI interactions

## Overview

Prompts allow MCP servers to expose reusable, parameterized message templates. Unlike tools (server-executed) or resources (data), prompts are **user-controlled** templates that generate structured messages for LLM interactions.

## Capability Declaration

Servers supporting prompts **MUST** declare the capability during initialization:

```json
{
  "capabilities": {
    "prompts": {
      "listChanged": true
    }
  }
}
```

| Sub-capability | Description |
|----------------|-------------|
| `listChanged` | Server emits notifications when prompt list changes |

## Prompt Definition

```json
{
  "name": "code_review",
  "title": "Code Review Assistant",
  "description": "Analyzes code quality and suggests improvements",
  "arguments": [
    {
      "name": "code",
      "description": "The code to review",
      "required": true
    },
    {
      "name": "language",
      "description": "Programming language",
      "required": false
    },
    {
      "name": "focus",
      "description": "Review focus: security, performance, readability",
      "required": false
    }
  ]
}
```

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Unique identifier |
| `title` | No | Human-readable display name |
| `description` | No | What the prompt does |
| `arguments` | No | List of customization parameters |

### Argument Definition

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Argument identifier |
| `description` | No | What the argument controls |
| `required` | No | Whether argument must be provided (default: false) |

## Protocol Methods

### List Prompts

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "prompts/list",
  "params": {
    "cursor": "optional-pagination-cursor"
  }
}
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "prompts": [
      {
        "name": "code_review",
        "title": "Code Review",
        "description": "Analyze code quality",
        "arguments": [
          { "name": "code", "required": true },
          { "name": "language", "required": false }
        ]
      },
      {
        "name": "explain_error",
        "title": "Error Explainer",
        "description": "Explain an error message",
        "arguments": [
          { "name": "error", "required": true }
        ]
      }
    ],
    "nextCursor": "next-page-cursor"
  }
}
```

### Get Prompt

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "prompts/get",
  "params": {
    "name": "code_review",
    "arguments": {
      "code": "def hello():\n    print('world')",
      "language": "python"
    }
  }
}
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "description": "Code review for Python code",
    "messages": [
      {
        "role": "user",
        "content": {
          "type": "text",
          "text": "Please review the following Python code for quality, best practices, and potential improvements:\n\n```python\ndef hello():\n    print('world')\n```"
        }
      }
    ]
  }
}
```

### List Changed Notification

When prompts change:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/prompts/list_changed"
}
```

## Message Format

Prompt results contain an array of `PromptMessage` objects:

```json
{
  "messages": [
    {
      "role": "user",
      "content": { "type": "text", "text": "..." }
    },
    {
      "role": "assistant",
      "content": { "type": "text", "text": "..." }
    }
  ]
}
```

### Roles

| Role | Description |
|------|-------------|
| `user` | Message from user perspective |
| `assistant` | Pre-filled assistant response |

### Content Types

**Text Content:**

```json
{
  "type": "text",
  "text": "The message content"
}
```

**Image Content:**

```json
{
  "type": "image",
  "data": "base64-encoded-image",
  "mimeType": "image/png"
}
```

**Audio Content:**

```json
{
  "type": "audio",
  "data": "base64-encoded-audio",
  "mimeType": "audio/wav"
}
```

**Embedded Resource:**

```json
{
  "type": "resource",
  "resource": {
    "uri": "file:///project/src/main.rs",
    "mimeType": "text/x-rust",
    "text": "fn main() { ... }"
  }
}
```

## Multi-Turn Prompts

Prompts can define conversation flows with multiple messages:

```json
{
  "messages": [
    {
      "role": "user",
      "content": {
        "type": "text",
        "text": "I need help debugging this code:"
      }
    },
    {
      "role": "user",
      "content": {
        "type": "resource",
        "resource": {
          "uri": "file:///src/buggy.py",
          "mimeType": "text/x-python",
          "text": "def divide(a, b):\n    return a / b"
        }
      }
    },
    {
      "role": "assistant",
      "content": {
        "type": "text",
        "text": "I'll analyze this code for potential issues."
      }
    }
  ]
}
```

## Error Handling

| Code | Meaning | When |
|------|---------|------|
| -32602 | Invalid params | Unknown prompt name, missing required arguments |
| -32603 | Internal error | Server-side failure |

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "reason": "Missing required argument: code"
    }
  }
}
```

## Argument Completion

Clients can use the completion API to get suggestions for prompt arguments:

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "completion/complete",
  "params": {
    "ref": {
      "type": "ref/prompt",
      "name": "code_review"
    },
    "argument": {
      "name": "language",
      "value": "py"
    }
  }
}
```

## Security Considerations

1. **Validate all arguments** before processing
2. **Sanitize inputs** to prevent prompt injection
3. **Escape special characters** in generated messages
4. **Review prompts** for potential security issues

## Best Practices

### Prompt Design

- Use clear, descriptive names
- Provide helpful descriptions
- Document all arguments thoroughly
- Use sensible defaults for optional arguments
- Keep prompts focused on a single purpose

### Argument Design

```json
{
  "arguments": [
    {
      "name": "code",
      "description": "Source code to analyze. Supports any programming language.",
      "required": true
    },
    {
      "name": "max_issues",
      "description": "Maximum number of issues to report (default: 10)",
      "required": false
    }
  ]
}
```
