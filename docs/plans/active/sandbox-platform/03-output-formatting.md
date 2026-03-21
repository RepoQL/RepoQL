# Plan: Output Formatting

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Output Formatting

## Scope

**Covers:**
- Sandbox output formatter that produces result + diagnostics + footer
- Rendered output for MCP and gRPC `rendered_output` field
- Structured output for programmatic callers (`result_json` + `diagnostics`)
- Footer with timing, capability call counts, and token usage
- Diagnostics rendering with severity markers
- Tests for formatting across success, error, diagnostics-only, and no-capabilities cases

**Does not cover:**
- Capability injection (Plan: 02-capability-injection — prerequisite)
- Write/delete capabilities (Plan: 04-write-delete)
- Module registry (Plan: 05-module-registry)

## Enables

Once output formatting exists:
- **Sandbox feels native** — output is indistinguishable from explore/read/query in shape and polish
- **Diagnostics are visible** — `repoql.log()` / `warn()` / `error()` appear in the output alongside results
- **Agents get execution metadata** — timing, reads performed, tokens spent — enabling budget-aware scripts

## Prerequisites

- **Plan: 02-capability-injection** completed — `SandboxExecutionContext` with diagnostics list and metadata available
- Existing footer patterns in `ExploreOrchestrator` and `ReadOrchestrator` for style reference

## North Star

A sandbox result should be indistinguishable from a native RepoQL tool result. Same shape. Same footer. Same diagnostic severity markers. An agent reading sandbox output should not be able to tell it apart from explore or read output.

## Done Criteria

### Output Formatter

- The formatter shall accept a sandbox execution result (success value or error) and a `SandboxExecutionContext`
- The formatter shall produce rendered output with three sections:
  1. **Result** — the script's return value
  2. **Diagnostics** — collected `repoql.log()`, `warn()`, `error()` messages
  3. **Footer** — execution metadata in bracket format

### Result Section

- When the result is a JSON object or array, render as formatted JSON
- When the result is a string, render as plain text
- When the result is a number or boolean, render as the string representation
- When the result is `null` or `undefined`, render as `(no result)`
- When the result is an error, render the structured error JSON (existing format, unchanged)

### Diagnostics Section

- When diagnostics are present, render after the result section with a blank line separator
- Each diagnostic shall be prefixed with a severity marker:
  - `info` → `ℹ`
  - `warn` → `⚠`
  - `error` → `✗`
- When no diagnostics are present, omit the section entirely (no blank section)

### Footer

- The footer shall follow the bracket format: `[sandbox | <timing> | <capability summary> | <budget summary>]`
  - Timing: elapsed milliseconds (e.g., `847ms`)
  - Capability summary: count of each operation type (e.g., `3 reads`, `3 reads, 1 write`). Omitted when zero.
  - Budget summary: tokens consumed (e.g., `5000 tok used`). Omitted when no capability calls.
- When no capabilities were used (pure computation), the footer shall be `[sandbox | <timing>]`

### MCP Integration

- The `SandboxTool` shall use the formatter for `rendered_output` in the `CallToolResult`
  - When the result is an error, `IsError` shall be `true` and the content shall be the formatted error
  - When the result is successful, `IsError` shall be `false` and the content shall be the full formatted output

### gRPC Integration

- The gRPC handler shall populate `rendered_output` with the formatted output
- The gRPC handler shall populate `result_json` with the raw result JSON (no formatting)
- The gRPC handler shall populate `diagnostics` with the structured diagnostic list
- The gRPC handler shall populate `capability_calls`, `tokens_consumed`, and `elapsed_ms` from the execution context

## Constraints

- **Formatter is separate from engine** — the sandbox engine returns raw results. The formatter is called by the MCP/gRPC handlers, not by the engine. SQL `js()` output remains unformatted. (Design: Output Formatting)
- **No footer on SQL results** — SQL UDF callers get raw values. The formatting layer is transport-specific. (Design: Two Surfaces)
- **Match existing patterns** — footer format should be consistent with explore/read footers, not invent new conventions.

## References

- [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Output Formatting section
- `src/RepoQL.Explore/ExploreOrchestrator.cs` — footer formatting patterns
- `src/RepoQL.Read/ReadOrchestrator.cs` — `ReadExecutionResult` and footer patterns
- `src/RepoQL.ConsoleApp/Tools/SandboxTool.cs` — current tool handler (to be updated)

## Error Policy

Formatting itself should never fail. If it does (e.g., JSON serialization error on result), fall back to raw string representation of the result with a diagnostic warning about the formatting failure.
