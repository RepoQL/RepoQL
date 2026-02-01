---
description: Design for RepoQL's Clawdbot plugin - enables repository intelligence across messaging platforms
tags: [clawdbot, plugin, integration, mcp]
audience: { human: 40, agent: 60 }
purpose: { design: 85, reference: 15 }
---

# RepoQL Clawdbot Plugin v2 Design

## North Star

The simplest reliable bridge between Clawdbot agents and RepoQL's MCP server. No external dependencies. Starts automatically. Recovers from failures. Agents discover and use it naturally.

## Context

**Problem**: Clawdbot agents across messaging platforms (Telegram, Discord, Slack, WhatsApp) need repository intelligence. The current plugin shells out to `mcporter`, an external tool that may not be installed, adds latency, and provides no lifecycle management.

**Flows enabled**:
- Agent explores unfamiliar codebase via `repoql_explore`
- Agent queries indexed data via `repoql_query`
- Agent retrieves content with token budget via `repoql_read`
- Agent imports external repositories via `repoql_import`

**Inputs**:
- Research: `docs/research/agents/clawdbot/clawdbot-plugins.md` - plugin architecture, APIs, distribution
- Existing: `clawdbot-plugin/` - working but minimal implementation

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| Plugins run in-process | Clawdbot architecture | Full trust, must not block Gateway |
| TypeScript/Node.js 22+ | Clawdbot runtime | Language choice fixed |
| One index per workspace | RepoQL architecture | Plugin manages multiple instances |
| MCP protocol | RepoQL server | Communication format fixed |
| Bundled distribution | Decision | Must integrate with RepoQL installer |

## Design

### Communication: MCP over stdio

Spawn RepoQL as a subprocess and communicate via JSON-RPC 2.0 over stdin/stdout.

**Rationale**: Reuses existing MCP server implementation. Simpler than implementing gRPC client in TypeScript. Standard protocol that RepoQL already speaks.

**Interface**:
```typescript
interface McpClient {
  spawn(exePath: string, workdir: string): Promise<void>;
  call<T>(method: string, params: Record<string, unknown>): Promise<T>;
  kill(): Promise<void>;
  readonly isConnected: boolean;
}
```

**Protocol**:
- Request: `{"jsonrpc":"2.0","id":N,"method":"tools/call","params":{"name":"...","arguments":{...}}}`
- Response: `{"jsonrpc":"2.0","id":N,"result":{"content":[{"type":"text","text":"..."}]}}`
- Timeout per tool (explore: 60s, query: 120s, read: 60s, import: 300s)
- Request queue to serialize concurrent calls

### Lifecycle: Background Service with Multi-Workspace

Register via `api.registerService()`. Manages one RepoQL instance per workspace.

**Rationale**: Always responsive. Index stays fresh. Agents can work across multiple repositories.

**Behavior**:
- On Gateway start: service initializes (no instances yet)
- On first tool call for a workspace: spawn RepoQL instance for that workspace
- Health check each instance every 60s (configurable)
- On instance crash: auto-restart with backoff (max 3 attempts, then error state)
- On Gateway stop: graceful shutdown of all instances

**Interface**:
```typescript
interface InstanceManager {
  getInstance(workdir: string): Promise<McpClient>;  // Returns existing or spawns new
  stopInstance(workdir: string): Promise<void>;
  stopAll(): Promise<void>;
}

api.registerService({
  id: "repoql-service",
  start: async () => { /* initialize manager */ },
  stop: async () => { /* stopAll() */ }
});
```

**Instance lifecycle**: Instances are spawned on-demand per workspace and kept running. No idle timeout - once started, an instance runs until Gateway stops.

### Tools

Four tools exposed to agents:

| Tool | Purpose | Timeout |
|------|---------|---------|
| `repoql_explore` | Intent-based repository discovery | 60s |
| `repoql_query` | DuckDB SQL on indexed data | 120s |
| `repoql_read` | Token-budget-aware content retrieval | 60s |
| `repoql_import` | Import external repositories | 300s |

Each tool:
1. Validates parameters via TypeBox
2. Resolves workspace (from parameter or agent context)
3. Gets/spawns instance via InstanceManager
4. Calls MCP client
5. Returns plain text content block

### Output Format: Plain Text

Return results as plain text. Let agents handle channel-specific formatting.

**Rationale**: Agents already adapt output to channels. Plugin doesn't know what the agent will do with results. Simpler plugin, more flexible agents.

### Executable Discovery

Find RepoQL executable:
1. `config.exePath` if specified
2. `PATH` lookup

**Rationale**: Simple. Standard installs put RepoQL on PATH. Override available if needed.

### Skills

Three focused skills for discoverability:

| Skill | Purpose |
|-------|---------|
| `repoql` | Core skill - when to use RepoQL vs file reads |
| `repoql-sql` | SQL reference - views, functions, patterns |
| `repoql-search` | Search patterns - semantic, regex, scope |

**Rationale**: Split skills are more discoverable than one large skill. Agents can load only what they need.

## Cross-Cutting Concerns

### Error Handling

- MCP client normalizes all errors to `{content: [{type:"text", text:"..."}], isError: true}`
- Background service logs errors, triggers restart if fatal
- Tools return actionable error messages (not stack traces)

### Logging

- Use `api.logger` for structured logging
- Log level: info for lifecycle events, warn for recoverable errors, error for failures
- Include correlation IDs for request tracing

### Configuration

```json
{
  "exePath": "string | PATH lookup",
  "healthCheckIntervalMs": "number | 60000",
  "maxRestartAttempts": "number | 3",
  "defaultTimeoutMs": "number | 60000",
  "workspaceAsRepo": "boolean | true"
}
```

## Trade-offs

| We chose | Over | Because |
|----------|------|---------|
| MCP stdio | Direct gRPC | Simpler, reuses existing server |
| Background service | On-demand | Faster response, index stays fresh |
| Plain text output | Channel-aware formatting | Simpler plugin, agents adapt naturally |
| Bundled distribution | npm package | Single install, always in sync |
| Request queue | Parallel calls | DuckDB is single-writer |
| Multi-workspace | Single workspace | Agents work across repos naturally |

## Alternatives Considered

### Direct gRPC connection
**Rejected**: Would require implementing gRPC client in TypeScript. More complexity for marginal performance gain. MCP protocol already optimized.

### On-demand lifecycle
**Rejected**: Cold start adds latency to first query. Index may not be ready. Background service is more predictable.

### Channel-aware output formatting
**Rejected**: Plugin doesn't know agent's intent. Agents already handle output formatting. Added complexity with little benefit.

### npm distribution
**Rejected**: Creates version sync issues. Users would need to install plugin separately. Bundling ensures compatibility.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| RepoQL not installed | Clear error message with install instructions at service start |
| Subprocess crash mid-query | Auto-restart service, queued request fails with retry hint |
| Index not ready on startup | Service starts immediately, tools return "indexing" status if queried early |
| Subprocess orphaned on Gateway crash | Register `process.on('exit')` cleanup handler |
| Large results cause timeout | Per-tool timeout config, token budgets in RepoQL handle size |

## Resolved Questions

1. **Multi-workspace**: Yes - one instance per workspace, spawned on-demand, kept running
2. **File watching**: Rely on RepoQL's internal watching
