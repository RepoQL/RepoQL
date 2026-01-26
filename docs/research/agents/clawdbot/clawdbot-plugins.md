# Clawdbot Plugins: Discovery Document

> **Date:** January 2026
> **Status:** Research Complete
> **Relevance:** High - Extension mechanism for personal AI assistant platforms

## Executive Summary

Clawdbot plugins are TypeScript modules that extend Clawdbot's capabilities with custom tools, commands, channels, and workflows. Unlike Claude Code plugins (which use Markdown-based skill definitions), Clawdbot plugins are full code modules that run in-process with the Gateway, providing deep integration capabilities including custom messaging channels, agent tools, background services, and workflow automation via the Lobster engine.

**Key Insight for RepoQL:** Clawdbot's plugin system allows RepoQL to be distributed as a native tool plugin, making repository intelligence available to Clawdbot agents across all messaging platforms (WhatsApp, Telegram, Slack, Discord, etc.).

---

## 1. Architecture Overview

### 1.1 Plugin Runtime Model

Clawdbot plugins run **in-process** with the Gateway via jiti (just-in-time TypeScript interpretation). This means:

- Full access to Node.js APIs
- Shared memory space with the Gateway
- Ability to register Gateway RPC methods
- Must be treated as trusted code

**Runtime Requirements:**
- Node.js >= 22
- TypeScript (ESM) preferred
- Runtime dependencies in `dependencies` (not `devDependencies`)

### 1.2 Plugin Directory Structure

```
my-plugin/
├── clawdbot.plugin.json      # Required: Plugin manifest
├── index.ts                  # Entry point (function or object export)
├── package.json              # npm package definition (optional)
├── skills/                   # Optional: Skill definitions
│   └── my-skill/
│       └── SKILL.md
├── hooks/                    # Optional: Event hooks
│   └── HOOK.md
│   └── handler.ts
└── scripts/                  # Optional: Utility scripts
```

**Critical Rule:** Plugin code runs with full Gateway permissions. Only install plugins you trust.

### 1.3 Plugin Manifest (clawdbot.plugin.json)

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "description": "What this plugin does",
  "version": "1.0.0",
  "kind": "tool",
  "configSchema": {
    "type": "object",
    "properties": {
      "apiKey": {
        "type": "string",
        "description": "API key for external service"
      },
      "enabled": {
        "type": "boolean",
        "default": true
      }
    }
  },
  "uiHints": {
    "apiKey": {
      "label": "API Key",
      "placeholder": "sk-...",
      "sensitive": true
    }
  },
  "skills": ["skills/my-skill"]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Unique plugin identifier (kebab-case) |
| `name` | No | Human-readable name |
| `description` | No | Brief description |
| `version` | No | Semantic version |
| `kind` | No | Plugin category (`tool`, `memory`, etc.) |
| `configSchema` | No | JSON Schema for configuration |
| `uiHints` | No | UI labels, placeholders, sensitivity flags |
| `skills` | No | Array of skill directory paths |

### 1.4 Entry Point Formats

**Function Format (Simple):**
```typescript
export default function(api) {
  api.registerTool({ /* ... */ });
}
```

**Object Format (Full):**
```typescript
export const id = "my-plugin";
export const name = "My Plugin";

export function register(api) {
  // Plugin initialization
}
```

---

## 2. Plugin Components

### 2.1 Agent Tools

Tools are the primary extension mechanism. They appear as callable functions to Clawdbot agents.

**Registration:**
```typescript
api.registerTool({
  name: "my_tool",              // snake_case convention
  description: "What this tool does",
  parameters: TypeBoxSchema,    // @sinclair/typebox schema
  async execute(id: string, params: Params) {
    // Implementation
    return {
      content: [{ type: "text", text: "Result" }]
    };
  }
});
```

**Parameter Schema (TypeBox):**
```typescript
import { Type, Static } from "@sinclair/typebox";

const MyParams = Type.Object({
  query: Type.String({ description: "Search query" }),
  limit: Type.Optional(Type.Number({
    description: "Max results",
    default: 10
  })),
  format: Type.Union([
    Type.Literal("json"),
    Type.Literal("text")
  ], { description: "Output format" })
});
```

**Return Format:**
```typescript
// Success
return {
  content: [{ type: "text", text: "Result text" }]
};

// Error
return {
  content: [{ type: "text", text: "Error message" }],
  isError: true
};
```

### 2.2 CLI Commands

Register custom commands for the `clawdbot` CLI.

```typescript
api.registerCli(({ program }) => {
  program
    .command("mycommand <arg>")
    .description("My custom command")
    .option("-v, --verbose", "Verbose output")
    .action((arg, options) => {
      console.log(`Running with ${arg}, verbose: ${options.verbose}`);
    });
}, { commands: ["mycommand"] });
```

### 2.3 Auto-Reply Commands

Slash commands that execute without invoking the AI agent.

```typescript
api.registerCommand({
  name: "mystatus",
  description: "Show plugin status",
  acceptsArgs: true,
  requireAuth: true,
  handler: async (ctx) => ({
    text: `Plugin active! Channel: ${ctx.channel}, Args: ${ctx.args}`
  })
});
```

**Context Object:**
| Property | Description |
|----------|-------------|
| `senderId` | User identifier |
| `channel` | Channel type (telegram, discord, etc.) |
| `isAuthorizedSender` | Whether user is authorized |
| `args` | Parsed arguments |
| `commandBody` | Raw command text |
| `config` | Plugin configuration |

### 2.4 Gateway RPC Methods

Register methods callable via the Gateway RPC protocol.

```typescript
api.registerGatewayMethod("myplugin.action", ({ respond, params }) => {
  const result = performAction(params);
  respond(true, { result });
});
```

### 2.5 Background Services

Long-running processes managed by the Gateway lifecycle.

```typescript
api.registerService({
  id: "my-background-service",
  start: async () => {
    api.logger.info("Service starting");
    // Initialize background work
  },
  stop: async () => {
    api.logger.info("Service stopping");
    // Cleanup
  }
});
```

### 2.6 Channel Plugins

Create new messaging channel integrations.

```typescript
const myChannel = {
  id: "acmechat",
  meta: {
    label: "AcmeChat",
    docsPath: "/channels/acmechat"
  },
  capabilities: {
    chatTypes: ["direct", "group"]
  },
  config: {
    listAccountIds: (cfg) => Object.keys(cfg.channels?.acmechat?.accounts ?? {}),
    resolveAccount: (cfg, id) => cfg.channels?.acmechat?.accounts?.[id] ?? {}
  },
  outbound: {
    deliveryMode: "direct",
    sendText: async ({ text, recipientId }) => {
      // Send message
      return { ok: true };
    }
  }
};

export default function(api) {
  api.registerChannel({ plugin: myChannel });
}
```

### 2.7 Provider Plugins (Model Auth)

Register authentication flows for AI model providers.

```typescript
api.registerProvider({
  id: "acme-ai",
  label: "AcmeAI",
  auth: [{
    id: "oauth",
    label: "OAuth 2.0",
    kind: "oauth",
    run: async (ctx) => ({
      profiles: [{
        profileId: "acme:default",
        credential: {
          type: "oauth",
          provider: "acme",
          access: "access_token",
          refresh: "refresh_token",
          expires: Date.now() + 3600 * 1000
        }
      }],
      defaultModel: "acme/opus-1"
    })
  }]
});
```

---

## 3. Skills Integration

### 3.1 Skill Structure

Skills provide guidance that agents can invoke during conversations.

**Location:** `skills/<skill-name>/SKILL.md`

**Format:**
```markdown
---
name: my-skill
description: Brief description for agent discovery
---

# Skill Title

Detailed instructions for the agent when this skill is activated.

## When to Use
- Scenario 1
- Scenario 2

## Instructions
1. Step one
2. Step two

## Examples
...
```

### 3.2 Enabling Skills

Skills are registered in the plugin manifest:

```json
{
  "skills": ["skills/repoql", "skills/code-review"]
}
```

Skills appear in `clawdbot skills list` with prefix `plugin:<id>`.

---

## 4. Hooks Integration

### 4.1 Plugin Hooks

Register event-driven automation from plugins.

```typescript
import { registerPluginHooksFromDir } from "clawdbot/plugin-sdk";

export default function register(api) {
  registerPluginHooksFromDir(api, "./hooks");
}
```

### 4.2 Hook File Structure

```
hooks/
├── HOOK.md          # Hook definition
└── handler.ts       # Hook implementation
```

**HOOK.md:**
```markdown
---
event: PostToolUse
matcher: "my_tool"
---

Run validation after my_tool executes.
```

**handler.ts:**
```typescript
export default async function(ctx) {
  // Hook logic
  return { continue: true };
}
```

---

## 5. Lobster Workflow Integration

### 5.1 Overview

Lobster is Clawdbot's typed workflow runtime for composable pipelines with approval gates.

**Benefits:**
- Multi-step tool sequences as single operations
- Explicit approval checkpoints before side effects
- Resumable state with durable tokens
- Deterministic execution for audit/replay

### 5.2 Workflow Definition (.lobster)

```yaml
name: code-review-workflow
args:
  files: { default: "src/**/*.ts" }
steps:
  - name: lint
    run: eslint $files --format json

  - name: review
    run: llm-task review --input $lint.stdout
    approval: required

  - name: apply
    run: apply-fixes --fixes $review.json
    when: $review.json.hasFixableIssues
```

### 5.3 Tool Integration

Enable Lobster for agents:

```json
{
  "agents": {
    "list": [{
      "id": "main",
      "tools": { "allow": ["lobster"] }
    }]
  }
}
```

---

## 6. Configuration

### 6.1 Plugin Configuration

Plugins are configured in `~/.clawdbot/clawdbot.json`:

```json5
{
  plugins: {
    enabled: true,
    allow: ["repoql", "voice-call"],  // Allowlist
    deny: ["untrusted"],               // Blocklist
    entries: {
      "repoql": {
        enabled: true,
        config: {
          exePath: "/usr/local/bin/repoql",
          autoServe: true
        }
      }
    },
    slots: {
      memory: "memory-core"  // Exclusive slot selection
    }
  }
}
```

### 6.2 Plugin Discovery Order

1. `plugins.load.paths` (explicit paths)
2. `<workspace>/.clawdbot/extensions/` (workspace extensions)
3. `~/.clawdbot/extensions/` (global extensions)
4. Bundled extensions (disabled by default)

First match wins; duplicates ignored.

---

## 7. Distribution & Packaging

### 7.1 npm Distribution

Recommended for public plugins:

**package.json:**
```json
{
  "name": "@clawdbot/my-plugin",
  "version": "1.0.0",
  "main": "./dist/index.js",
  "clawdbot": {
    "extensions": ["./src/index.ts"]
  },
  "dependencies": {
    "@sinclair/typebox": "^0.32.0"
  },
  "peerDependencies": {
    "clawdbot": "*"
  }
}
```

**Important:**
- Runtime dependencies must be in `dependencies` (not `devDependencies`)
- Avoid `workspace:*` references (breaks npm install)
- Entry files can be `.ts` (jiti handles transpilation)

### 7.2 Package Packs

Multiple plugins from one package:

```json
{
  "clawdbot": {
    "extensions": ["./src/safety.ts", "./src/tools.ts"]
  }
}
```

Plugin IDs become: `package-name/safety`, `package-name/tools`

### 7.3 CLI Installation

```bash
# From npm
clawdbot plugins install @clawdbot/my-plugin

# From local path
clawdbot plugins install ./my-plugin

# Link (development)
clawdbot plugins install ./my-plugin --link

# From archive
clawdbot plugins install ./my-plugin.tgz
```

---

## 8. CLI Commands Reference

| Command | Description |
|---------|-------------|
| `clawdbot plugins list` | View loaded plugins |
| `clawdbot plugins info <id>` | Plugin details |
| `clawdbot plugins install <spec>` | Install plugin |
| `clawdbot plugins enable <id>` | Enable plugin |
| `clawdbot plugins disable <id>` | Disable plugin |
| `clawdbot plugins update <id>` | Update npm plugin |
| `clawdbot plugins update --all` | Update all npm plugins |
| `clawdbot plugins doctor` | Run diagnostics |

---

## 9. Comparison: Clawdbot vs Claude Code Plugins

| Aspect | Clawdbot | Claude Code |
|--------|----------|-------------|
| **Format** | TypeScript modules | Markdown + JSON |
| **Runtime** | In-process (jiti) | Subprocess/MCP |
| **Manifest** | `clawdbot.plugin.json` | `.claude-plugin/plugin.json` |
| **Skills** | `SKILL.md` | `SKILL.md` (compatible) |
| **Tools** | `api.registerTool()` | MCP servers |
| **Hooks** | `HOOK.md` + `handler.ts` | `hooks.json` |
| **Channels** | `api.registerChannel()` | N/A |
| **Workflows** | Lobster | N/A |
| **Distribution** | npm, local, archives | Git marketplaces |
| **Trust Model** | In-process (full trust) | Sandboxed |

---

## 10. RepoQL Plugin Implementation

### 10.1 Current Structure

RepoQL already has a Clawdbot plugin in `clawdbot-plugin/`:

```
clawdbot-plugin/
├── clawdbot.plugin.json    # Manifest with config schema
├── index.ts                # Tool registrations
├── package.json            # Dependencies
└── skills/
    └── repoql/
        └── SKILL.md        # Agent skill definition
```

### 10.2 Registered Tools

| Tool | Description |
|------|-------------|
| `repoql_explore` | X-ray vision for repository discovery |
| `repoql_query` | DuckDB SQL on indexed repository |
| `repoql_read` | Token-budget-aware content retrieval |

### 10.3 Integration Pattern

The plugin uses `mcporter` to communicate with RepoQL's MCP server:

```typescript
function callRepoQl(tool: string, args: Record<string, unknown>, workdir: string) {
  const cmd = `mcporter call repoql.${tool} ${argParts.join(" ")}`;
  return execSync(cmd, { cwd: workdir, encoding: "utf-8" });
}
```

### 10.4 Potential Enhancements

| Enhancement | Benefit |
|-------------|---------|
| Direct gRPC connection | Eliminate mcporter overhead |
| Background indexing service | Keep index fresh automatically |
| Lobster workflows | Compose multi-step code analysis |
| Channel-specific formatting | Optimize output for each platform |

---

## 11. Security Considerations

### 11.1 Trust Model

Plugins run in-process with full Gateway access:
- Can access all configuration
- Can modify responses
- Can intercept messages
- Can access filesystem

**Mitigations:**
- Use `plugins.allow` allowlists
- Review plugin code before installation
- Pin plugin versions
- Audit plugin updates

### 11.2 Configuration Security

For sensitive values:

```json
{
  "uiHints": {
    "apiKey": {
      "sensitive": true
    }
  }
}
```

### 11.3 Sandbox Considerations

When running in Docker sandbox:
- Plugins don't inherit host environment variables
- Configure via `agents.defaults.sandbox.docker.env`
- Or bake into custom sandbox images

---

## 12. Sources

### Official Documentation
- [Clawdbot Plugin Guide](https://docs.clawd.bot/plugin.md)
- [CLI Plugins](https://docs.clawd.bot/cli/plugins.md)
- [Skills Configuration](https://docs.clawd.bot/tools/skills-config.md)
- [Lobster Workflows](https://docs.clawd.bot/tools/lobster.md)

### Repository
- [GitHub - clawdbot/clawdbot](https://github.com/clawdbot/clawdbot)
- [AGENTS.md](https://github.com/clawdbot/clawdbot/blob/main/AGENTS.md)

### Community
- [Clawdbot Discord](https://discord.gg/clawdbot)
- [Clawdbot FAQ](https://docs.clawd.bot/help/faq)

---

## 13. Appendix: Quick Reference

### Plugin Manifest Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Unique identifier |
| `name` | string | No | Display name |
| `description` | string | No | Brief description |
| `version` | string | No | Semantic version |
| `kind` | string | No | Category (tool, memory, etc.) |
| `configSchema` | object | No | JSON Schema for config |
| `uiHints` | object | No | UI configuration |
| `skills` | array | No | Skill directory paths |

### API Methods

| Method | Purpose |
|--------|---------|
| `api.registerTool()` | Add agent tool |
| `api.registerCli()` | Add CLI command |
| `api.registerCommand()` | Add auto-reply command |
| `api.registerGatewayMethod()` | Add RPC method |
| `api.registerService()` | Add background service |
| `api.registerChannel()` | Add messaging channel |
| `api.registerProvider()` | Add model auth provider |
| `api.logger` | Logging interface |
| `api.config` | Plugin configuration |
| `api.workspace` | Current workspace path |
| `api.runtime.tts` | Text-to-speech helpers |

### Environment

| Variable | Description |
|----------|-------------|
| `CLAWDBOT_LIVE_TEST` | Enable live testing |
| Gateway restart required after config changes |
