---
description: Plan for the bidirectional streaming tool loop — budget enforcement, round tracking, parallel calls
tags: [plan, inference, tool-loop, streaming, grpc]
audience: { human: 60, agent: 40 }
purpose: { plan: 100 }
---

# Plan: Tool Loop

Implements: [Inference Service Design](../../../designs/future/inference-service.md) — `CompleteWithTools` bidi streaming RPC, tool relay state machine, pre-dispatch budget enforcement, round tracking, parallel tool calls.

## Scope

**Covers:**
- `CompleteWithTools` bidi streaming RPC implementation
- Tool relay state machine (server ↔ client ↔ LLM)
- Pre-dispatch budget enforcement (extract `tokenBudget` from `arguments_json`, check against remaining pool)
- Post-dispatch budget tracking (deduct `tokens_used` from pool)
- Round tracking and `max_rounds` enforcement
- Parallel tool call relay (`more_in_round` signalling)
- Grok multi-turn tool loop (feed tool results back, get next LLM turn)
- Error handling: malformed tool calls, client disconnect, degenerate loops, budget exhaustion
- Tests for all state machine transitions

**Does not cover:**
- Proto definition (Plan: 01-service-foundation — already exists)
- Grok client basics (Plan: 01-service-foundation — client already talks to Grok)
- Auth (Plan: 01-service-foundation — interceptor already handles bidi)
- Host-side client, tool execution (Plan: 03-host-integration)
- Deploy (Plan: 01-service-foundation — same service, same workflow)

## Enables

Once the tool loop exists:
- **Full inference with follow-up reads** — the LLM can read specific files and symbols after seeing the broad context
- **Plan: 03-host-integration** can implement the tool execution side — sending `ToolResponse` messages with actual read results
- **The inference service replaces `explain`** — same capability (question → tool-assisted answer → citations) via a clean gRPC interface

## Prerequisites

- Plan: 01-service-foundation complete — proto generated, service running, Grok client working, unary `Complete` operational, auth covering bidi
- Understanding of Grok gRPC Chat API tool calling:
  - Function call results in the `GetCompletion` / `GetCompletionChunk` response
  - Function results fed back by appending to conversation history on next call
  - gRPC Chat API is stateless — explicit conversation management required
  - See [research notes](../../../research/grok-4-1-fast.md#tool-calling)

## North Star

The LLM asks for three files. The server checks each against the budget, relays to the client, feeds results back, and the LLM synthesizes an answer. If the budget runs out on the second call, the LLM is told why and answers from what it has. The whole exchange is one stream — opens, tool rounds happen, closes with a `Completion`. No ambiguous states.

## Done Criteria

### Stream State Machine

- When the client opens a `CompleteWithTools` stream, the first message shall be a `CompleteRequest`
  - If the first message is not a `CompleteRequest`, the service shall close the stream with `INVALID_ARGUMENT`
- When the client sends a second `CompleteRequest`, the service shall close the stream with `INVALID_ARGUMENT`
- The service shall send zero or more `ToolRequest` messages followed by exactly one `Completion`
  - The `Completion` shall always be the last message on the stream
- When the LLM produces tool calls, the service shall send one `ToolRequest` per call
  - The service shall wait for all `ToolResponse` messages (matching by `call_id`) before sending results to the LLM
- When the client sends a `ToolResponse` with an unrecognized `call_id`, the service shall close the stream with `INVALID_ARGUMENT`

### Budget Enforcement

- The service shall initialize a remaining budget from `tool_token_budget`
- When `tool_token_budget` is 0, the service shall not send any `ToolRequest` messages
  - The service shall instruct the LLM to answer without tools
- When the LLM produces a tool call, the service shall extract `tokenBudget` from `arguments_json`
  - If `tokenBudget` exceeds the remaining pool, the service shall reject the call
  - The rejected call shall be reported to the LLM as a tool error with the remaining budget
  - If `tokenBudget` is absent or 0, the service shall reject the call as malformed (budget is required, not optional)
- When the client returns a `ToolResponse`, the service shall deduct `tokens_used` from the remaining pool
  - Token counts are approximate (host uses a Claude tokenizer, not Grok's). Small overruns are acceptable — budget is directional, not byte-exact
- When the remaining pool reaches 0, the service shall instruct the LLM to answer with gathered context
  - The `Completion` shall have `stop_reason = TOOL_BUDGET`

### Round Tracking

- The service shall track the current round number (1-indexed)
- When `max_rounds` is reached, the service shall instruct the LLM to produce a final response without tools
  - The `Completion` shall have `stop_reason = TOOL_LIMIT`
- When `max_rounds` is 0, the service shall use a server-configured default (e.g., 10)
- Each LLM turn that produces tool calls shall increment the round counter

### Parallel Tool Calls

- When the LLM produces multiple tool calls in one turn, the service shall send them as separate `ToolRequest` messages
  - All messages except the last shall have `more_in_round = true`
  - The last message shall have `more_in_round = false` (or absent)
- The service shall evaluate each call's budget sequentially
  - If the first fits but the second doesn't, relay the first and reject the second
  - The rejected call shall be reported to the LLM as a budget error
- The service shall wait for `ToolResponse` messages for all relayed calls before continuing

### Grok Multi-Turn

- The service shall maintain the conversation history for the Grok API within the request lifecycle
  - Developer message + tool definitions + context + user message → first Grok call
  - Tool results appended → subsequent Grok calls
  - Conversation state is in-memory, never persisted (gRPC Chat API is stateless)
- When the LLM produces content (no tool calls), that content becomes the `Completion`
- When the LLM produces both content and tool calls, tool calls take precedence (process tools, then re-query)
- When using `EFFORT_HIGH` (reasoning model), the service shall preserve `encrypted_content` across Grok API calls within the tool loop
  - Include `["reasoning.encrypted_content"]` in each Grok request
  - Pass the returned `encrypted_content` from each response into the next request
  - This preserves thinking state across tool rounds without exposing plaintext reasoning

### Error Handling

- When the LLM produces malformed `arguments_json`, the service shall return an error as a tool result to the LLM
  - The error message shall describe the parse failure
  - This counts as a round
- When the client disconnects mid-stream, the service shall cancel the Grok API call and log the event
- When no `ToolResponse` is received within the configured timeout, the service shall close the stream with `DEADLINE_EXCEEDED`
- When the LLM produces identical consecutive tool calls (degenerate loop), the service shall force a final response
  - Detection: same tool + same arguments for N consecutive calls (N configurable, default 3)
- When the Grok API fails mid-loop, the service shall return gRPC `INTERNAL` with details logged
  - There is no server-side fallback provider. The service either completes via Grok or fails

### Observability

- The service shall emit OTel traces spanning the full tool loop lifecycle
  - One span per Grok API call (including round number)
  - One span per tool relay (ToolRequest → ToolResponse, including `tokens_used`)
  - Budget remaining and round number as span attributes
- The service shall log at `Info` level: round transitions, budget deductions, forced completions

### Tests

- State machine: valid sequence (request → tool rounds → completion)
- State machine: invalid sequences (missing request, duplicate request, unknown call_id)
- Budget: pre-dispatch rejection when `tokenBudget` exceeds remaining
- Budget: pool deduction from `tokens_used`
- Budget: `tool_token_budget = 0` → no tool calls, direct completion
- Budget: missing `tokenBudget` in arguments → malformed error to LLM
- Budget: partial rejection in parallel batch (first fits, second rejected)
- Rounds: `max_rounds` enforcement
- Parallel: `more_in_round` signalling correct
- Reasoning: `encrypted_content` preserved across rounds for `EFFORT_HIGH`
- Error: malformed arguments → error fed back to LLM
- Error: client disconnect → cleanup
- Error: degenerate loop detection
- Error: Grok failure mid-loop → `INTERNAL` with no fallback
- Integration: mock Grok with scripted tool call → tool result → content sequence

## Constraints

- **Budget is a contract** — pre-dispatch enforcement is mandatory, not optional. The LLM never overspends. This is a design constraint, not an optimization.
- **Read tool only** — the design restricts tools to `read`. The tool loop implementation is tool-agnostic (it relays whatever the LLM calls), but test scenarios should use `read` tool arguments.
- **No data retention** — conversation state lives in-memory for the request duration. gRPC Chat API is stateless by default. No database, no cache, no files.
- **Grok conversation management** — gRPC Chat API has no `previous_response_id` continuation. Must pass full conversation history on each Grok call. See [design alternatives](../../../designs/future/inference-service.md#alternatives-considered).

## References

- [Design: Inference Service](../../../designs/future/inference-service.md) — stream state machine, budget enforcement, trade-offs
- [Flow: Tool-Use Completion](../../../flows/future/inference-service/tool-use-completion.md) — 8-stage flow this plan implements
- [Grok Research: Tool Calling](../../../research/grok-4-1-fast.md#tool-calling) — parallel calls, `max_turns`, function call response format
- [Grok Research: Reasoning Continuation](../../../research/grok-4-1-fast.md#reasoning-continuation) — `encrypted_content` for multi-turn thinking state
- [Testing Guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions

## Error Policy

The tool loop must be resilient. A single malformed tool call should not end the stream — feed the error back to the LLM and let it retry. Client disconnection should be clean — cancel upstream, log, release resources. Budget exhaustion is normal — not an error, a signal to complete. Only unrecoverable errors (Grok API down with no gathered context, protocol violations) should close the stream with an error status.
