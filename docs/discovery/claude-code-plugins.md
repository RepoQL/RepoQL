# Claude Code Plugins: Discovery Document

> **Date:** January 2026
> **Status:** Research Complete
> **Relevance:** High - Extension mechanism for AI coding assistants

## Executive Summary

Claude Code Plugins are a packaging system for extending Claude Code's functionality. Plugins bundle slash commands, subagents, skills, hooks, MCP servers, and LSP servers into installable packages that can be shared across teams and communities. The system entered public beta in November 2025 and represents Anthropic's primary extension architecture for Claude Code.

**Key Insight for RepoQL:** The plugin system provides a standardized distribution mechanism. RepoQL could be packaged and distributed as a Claude Code plugin, making installation trivial for Claude Code users.

---

## 1. Architecture Overview

### 1.1 Plugin Directory Structure

```
plugin-name/
├── .claude-plugin/
│   └── plugin.json          # Required: Plugin manifest (ONLY file in this dir)
├── commands/                 # Slash commands (.md files)
├── agents/                   # Subagent definitions (.md files)
├── skills/                   # Agent skills (subdirectories with SKILL.md)
│   └── skill-name/
│       └── SKILL.md
├── hooks/
│   └── hooks.json           # Event handler configuration
├── .mcp.json                # MCP server definitions
├── lsp/                     # LSP server configurations
├── scripts/                 # Helper scripts and utilities
└── README.md                # Plugin documentation
```

**Critical Rule:** All component directories (`commands/`, `agents/`, `skills/`, `hooks/`) MUST be at the plugin root level, NOT inside `.claude-plugin/`. Only `plugin.json` goes inside `.claude-plugin/`.

### 1.2 Plugin Manifest (plugin.json)

```json
{
  "$schema": "https://anthropic.com/claude-code/plugin.schema.json",
  "name": "my-plugin",
  "version": "1.0.0",
  "description": "Brief description of plugin functionality",
  "author": "Author Name",
  "repository": "https://github.com/owner/repo"
}
```

### 1.3 Installation Methods

| Method | Command |
|--------|---------|
| From marketplace | `/plugin marketplace add owner/repo` then `/plugin install plugin-name` |
| Direct install | `/plugin install owner/repo` |
| Local development | `claude --plugin-dir ./my-plugin` |
| Team auto-prompt | Add to `.claude/settings.json` under `extraKnownMarketplaces` |

---

## 2. Plugin Components

### 2.1 Slash Commands

**Purpose:** Store frequently-used prompts and procedures for explicit manual invocation.

**Location:** `commands/` directory
**Format:** Markdown files where filename becomes command name
**Naming:** `hello.md` → `/plugin-name:hello`

**Example Command (commands/review.md):**
```markdown
---
description: Perform comprehensive code review on staged changes
allowed-tools: Read, Grep, Glob, Bash
---

Review the staged git changes with focus on:
1. Security vulnerabilities
2. Performance implications
3. Code style consistency
4. Test coverage gaps

$ARGUMENTS

Provide actionable feedback with specific line references.
```

**Key Features:**
- `$ARGUMENTS` placeholder injects user input
- Frontmatter supports `description`, `allowed-tools`, `model` overrides
- Commands can orchestrate subagents and call other commands

### 2.2 Agents (Subagents)

**Purpose:** Specialized AI agents that run in isolated context windows for focused tasks.

**Location:** `agents/` directory
**Format:** Markdown files defining agent behavior

**Example Agent (agents/security-reviewer.md):**
```markdown
---
name: security-reviewer
description: Security-focused code analysis agent
allowed-tools: Read, Grep, Glob
model: claude-sonnet-4-20250514
---

You are a security specialist. Analyze code for:
- OWASP Top 10 vulnerabilities
- Authentication/authorization flaws
- Input validation issues
- Sensitive data exposure

Report findings with severity ratings and remediation steps.
```

**When to Use Agents vs Commands:**
- **Agents:** Research-heavy tasks, parallel processing, context isolation needed
- **Commands:** Explicit user-triggered workflows, main context acceptable

### 2.3 Skills

**Purpose:** Auto-discoverable capabilities that Claude can apply during conversation without explicit invocation.

**Location:** `skills/skill-name/SKILL.md`
**Trigger:** Auto-invoked when Claude's task matches skill description

**Example Skill (skills/dexie-expert/SKILL.md):**
```markdown
---
name: dexie-expert
description: Dexie.js database guidance. Use when working with IndexedDB, schemas, queries, liveQuery, or database migrations.
allowed-tools: Read, Grep, Glob, WebFetch
---

When helping with Dexie.js:
1. Follow IndexedDB best practices
2. Use proper schema versioning
3. Implement liveQuery for reactive updates
4. Handle upgrade paths carefully

Reference patterns in this directory for common implementations.
```

**Key Differences from Commands:**
| Aspect | Slash Commands | Skills |
|--------|----------------|--------|
| Invocation | Explicit (`/command`) | Auto-discovered |
| Context | Can spawn subagent | Runs in main conversation |
| Platform | Claude Code only | Web, Desktop, and Claude Code |
| Reliability | 100% when invoked | May not auto-trigger |

**Limitation:** Auto-invocation isn't guaranteed. Some developers create wrapper slash commands to ensure skill execution.

### 2.4 Hooks

**Purpose:** Execute shell commands at specific points in Claude's lifecycle for automation, validation, and customization.

**Location:** `hooks/hooks.json` or inline in `plugin.json`
**Configuration:** Also supported in `.claude/settings.json`

#### 2.4.1 Hook Events

| Event | When Fired | Use Cases |
|-------|------------|-----------|
| `UserPromptSubmit` | Before Claude processes user input | Input validation, logging, context injection |
| `PreToolUse` | Before tool execution | Block dangerous operations, modify inputs |
| `PostToolUse` | After tool completion | Auto-formatting, result validation, feedback |
| `Stop` | When Claude finishes responding | Completion notifications, cleanup |
| `SubagentStop` | When subagent finishes | Task completion alerts |
| `SessionStart` | Session begins/resumes | Load context, inject environment |
| `SessionEnd` | Session terminates | Cleanup, logging |
| `PreCompact` | Before context compaction | Backup transcripts |
| `Notification` | When Claude sends notification | Custom alerts, TTS |
| `PermissionRequest` | Permission prompt shown | Auto-approve safe commands |

#### 2.4.2 Hook Configuration

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Write|Edit",
        "hooks": [
          {
            "type": "command",
            "command": "${CLAUDE_PLUGIN_ROOT}/scripts/validate-path.sh"
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "Write|Edit",
        "hooks": [
          {
            "type": "command",
            "command": "prettier --write \"$CLAUDE_TOOL_INPUT_FILE_PATH\""
          }
        ]
      }
    ],
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "git status --short && cat TODO.md"
          }
        ]
      }
    ]
  }
}
```

#### 2.4.3 Matcher Patterns

- `"Write"` - Exact match
- `"Write|Edit"` - Match either tool
- `"*"` - Match all tools
- `"Bash(npm test*)"` - Match Bash with specific command pattern

#### 2.4.4 Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Allow/OK - proceed normally |
| 2 | Block (PreToolUse only) - stderr sent to Claude as feedback |
| Other | Non-blocking error shown to user |

#### 2.4.5 Environment Variables Available to Hooks

| Variable | Description |
|----------|-------------|
| `CLAUDE_PROJECT_DIR` | Project root path |
| `CLAUDE_PLUGIN_ROOT` | Plugin installation directory |
| `CLAUDE_TOOL_INPUT_FILE_PATH` | Path of file being operated on |
| `CLAUDE_CODE_REMOTE` | Boolean for web environment |
| `CLAUDE_ENV_FILE` | Path for persisting variables (SessionStart) |

#### 2.4.6 Hook Input (via stdin)

All hooks receive JSON:
```json
{
  "session_id": "abc123",
  "transcript_path": "/path/to/transcript.jsonl",
  "cwd": "/current/directory",
  "permission_mode": "default",
  "hook_event_name": "PreToolUse",
  "tool_name": "Write",
  "tool_input": { "file_path": "/src/app.ts", "content": "..." }
}
```

#### 2.4.7 Input Modification (v2.0.10+)

PreToolUse hooks can modify tool inputs by outputting modified JSON to stdout:
```json
{
  "tool_input": {
    "file_path": "/src/app.ts",
    "content": "// Modified content..."
  }
}
```

### 2.5 MCP Servers

**Purpose:** Connect Claude to external tools and data sources via Model Context Protocol.

**Location:** `.mcp.json` at plugin root

**Example Configuration:**
```json
{
  "mcpServers": {
    "repoql": {
      "command": "repoql",
      "args": ["mcp", "--db", "${CLAUDE_PROJECT_DIR}/.repoql/index.db"],
      "env": {
        "REPOQL_LOG_LEVEL": "info"
      }
    },
    "github": {
      "command": "npx",
      "args": ["@modelcontextprotocol/server-github"],
      "env": {
        "GITHUB_TOKEN": "${GITHUB_TOKEN}"
      }
    },
    "remote-api": {
      "type": "sse",
      "url": "https://api.example.com/mcp/sse",
      "headers": {
        "Authorization": "Bearer ${API_TOKEN}"
      }
    }
  }
}
```

**Configuration Scopes:**
| Scope | Location | Visibility |
|-------|----------|------------|
| Local | `.mcp.json` | Current project, your machine |
| Project | `.mcp.json` (committed) | Team-shared |
| User | `~/.claude.json` | All your projects |
| Managed | `managed-mcp.json` | Organization-controlled |

### 2.6 LSP Servers

**Purpose:** Provide real-time code intelligence (diagnostics, navigation, hover).

**Added:** Claude Code v2.0.74 (December 2025)

**Supported Operations:**
- `goToDefinition` - Jump to symbol definition
- `findReferences` - Find all usages
- `hover` - Display documentation/types
- `documentSymbol` - List symbols in file
- `getDiagnostics` - Get errors/warnings

**Performance:** 50ms navigation vs 45 seconds with text search

**Installation:**
```bash
# From marketplace
/plugin marketplace add boostvolt/claude-code-lsps
/plugin install typescript-lsp
```

**Available LSP Plugins (boostvolt/claude-code-lsps):**
- Bash/Shell, C/C++/Objective-C, C#, Clojure
- Dart/Flutter, Elixir, Gleam, Go, Java
- Kotlin, Lua, Nix, OCaml, PHP, Python
- Ruby, Rust, Swift, Terraform
- TypeScript/JavaScript, YAML, Zig

---

## 3. Marketplace Distribution

### 3.1 Creating a Marketplace

A marketplace is a Git repository containing:
```
marketplace-repo/
├── .claude-plugin/
│   └── marketplace.json      # Catalog of available plugins
├── plugin-a/                 # Individual plugin directories
│   └── .claude-plugin/
│       └── plugin.json
├── plugin-b/
│   └── ...
└── README.md
```

**marketplace.json:**
```json
{
  "$schema": "https://anthropic.com/claude-code/marketplace.schema.json",
  "name": "my-marketplace",
  "description": "Collection of productivity plugins",
  "plugins": [
    {
      "name": "plugin-a",
      "path": "./plugin-a",
      "description": "Description of plugin A"
    },
    {
      "name": "plugin-b",
      "path": "./plugin-b",
      "description": "Description of plugin B"
    }
  ]
}
```

### 3.2 Distribution Commands

```bash
# Add marketplace
/plugin marketplace add owner/repo

# List available plugins
/plugin discover

# Install specific plugin
/plugin install plugin-name

# Remove marketplace
/plugin marketplace remove owner/repo
```

### 3.3 Team Configuration

Auto-prompt team members to install marketplace:
```json
// .claude/settings.json
{
  "extraKnownMarketplaces": [
    "your-org/internal-plugins"
  ]
}
```

### 3.4 Private Repositories

Set authentication token in environment:
```bash
export GITHUB_TOKEN=ghp_xxxxx
```

### 3.5 Reserved Marketplace Names

Anthropic reserves these names:
- `claude-code-marketplace`, `claude-code-plugins`, `claude-plugins-official`
- `anthropic-marketplace`, `anthropic-plugins`
- `agent-skills`, `life-sciences`

---

## 4. Security Considerations

### 4.1 Trust Model

Claude Code plugins run with the same permissions as the invoking user. This means:
- Full filesystem read access
- Bash command execution capability
- Multi-file modification ability
- External tool integration via MCP

**Anthropic's guidance:** Treat Claude Code as a "brilliant but untrusted intern" - capable but requiring review.

### 4.2 Plugin Security Checklist

- [ ] Review all hook scripts before installation
- [ ] Audit MCP server configurations
- [ ] Check for command injection vulnerabilities
- [ ] Verify plugin source and maintainer reputation
- [ ] Test in isolated environment first
- [ ] Review permissions requested in `allowed-tools`

### 4.3 Hook Security

**Warning:** Hooks execute arbitrary shell commands automatically.

**Mitigations:**
- Exit code 2 blocks operations (PreToolUse only)
- Input validation in hook scripts
- Path sanitization for file operations
- Blocklist for dangerous commands

### 4.4 MCP Server Security

- Only enable MCP servers from trusted sources
- Anthropic does not audit third-party MCP servers
- Review MCP server code before enabling
- Use scoped authentication tokens

### 4.5 Prompt Injection Safeguards

Built-in protections:
- Permission system for sensitive operations
- Context-aware analysis for harmful instructions
- Input sanitization
- Command blocklist (`curl`, `wget` blocked by default)

**Limitation:** Security reviews are not hardened against prompt injection. Only use for trusted code.

---

## 5. Best Practices

### 5.1 Plugin Development

| Practice | Rationale |
|----------|-----------|
| Use kebab-case naming | Consistency across ecosystem |
| Include comprehensive README | Discoverability and onboarding |
| Test with `--plugin-dir` flag | Catch issues before publishing |
| Use `${CLAUDE_PLUGIN_ROOT}` for paths | Portable across installations |
| Validate in PreToolUse hooks | Prevent rather than correct |
| Provide skill fallback commands | Auto-invocation isn't guaranteed |

### 5.2 Command Design

- Keep commands focused on single workflows
- Use clear, descriptive names
- Document expected `$ARGUMENTS` format
- Specify minimal `allowed-tools`
- Include usage examples in description

### 5.3 Hook Design

- Use exit code 2 sparingly (blocks operations)
- Send clear feedback via stderr when blocking
- Make hooks idempotent where possible
- Log actions for debugging
- Handle missing dependencies gracefully

### 5.4 MCP Server Integration

- Use environment variables for secrets
- Scope tokens minimally
- Document required environment variables
- Test server connectivity during SessionStart
- Provide fallback behavior when server unavailable

---

## 6. Relevance to RepoQL

### 6.1 Distribution Opportunity

RepoQL could be packaged as a Claude Code plugin, providing:
- One-command installation via `/plugin install`
- Automatic MCP server configuration
- Pre-built slash commands for common queries
- Skills for auto-triggered code intelligence
- Hooks for index maintenance

### 6.2 Potential Plugin Structure

```
repoql-plugin/
├── .claude-plugin/
│   └── plugin.json
├── commands/
│   ├── explore.md          # /repoql:explore - Codebase exploration
│   ├── query.md            # /repoql:query - Direct SQL queries
│   └── find.md             # /repoql:find - Semantic search
├── skills/
│   └── code-intelligence/
│       └── SKILL.md        # Auto-trigger for code questions
├── hooks/
│   └── hooks.json          # Auto-index on file changes
├── .mcp.json               # RepoQL MCP server config
└── README.md
```

### 6.3 Hook Integration Ideas

| Hook | RepoQL Use Case |
|------|-----------------|
| `PostToolUse` (Write/Edit) | Trigger incremental re-index |
| `SessionStart` | Verify index freshness, show stale warnings |
| `PreCompact` | Persist important query results |

### 6.4 Competitive Advantage

The plugin ecosystem enables RepoQL to:
- Reach Claude Code users with zero-friction installation
- Integrate deeply with the development workflow
- Provide auto-triggered intelligence (via Skills)
- Automate index maintenance (via Hooks)
- Bundle with complementary MCP servers

---

## 7. Sources

### Official Documentation
- [Create plugins - Claude Code Docs](https://code.claude.com/docs/en/plugins)
- [Plugins reference - Claude Code Docs](https://code.claude.com/docs/en/plugins-reference)
- [Hooks reference - Claude Code Docs](https://code.claude.com/docs/en/hooks)
- [Configure hooks - Claude Blog](https://claude.com/blog/how-to-configure-hooks)
- [Connect to MCP - Claude Code Docs](https://code.claude.com/docs/en/mcp)
- [Plugin marketplaces - Claude Code Docs](https://code.claude.com/docs/en/plugin-marketplaces)
- [Security - Claude Code Docs](https://code.claude.com/docs/en/security)

### Community Resources
- [anthropics/claude-code plugins README](https://github.com/anthropics/claude-code/blob/main/plugins/README.md)
- [disler/claude-code-hooks-mastery](https://github.com/disler/claude-code-hooks-mastery)
- [boostvolt/claude-code-lsps](https://github.com/boostvolt/claude-code-lsps)
- [wshobson/commands](https://github.com/wshobson/commands)
- [Customize Claude Code with plugins - Claude Blog](https://claude.com/blog/claude-code-plugins)

### Tutorials & Guides
- [Claude Code customization guide](https://alexop.dev/posts/claude-code-customization-guide-claudemd-skills-subagents/)
- [How to Use Claude Code Features](https://www.producttalk.org/how-to-use-claude-code-features/)
- [Claude Code Security Best Practices](https://www.backslash.security/blog/claude-code-security-best-practices)
- [Claude Code LSP Setup Guide](https://www.aifreeapi.com/en/posts/claude-code-lsp)

---

## 8. Appendix: Quick Reference

### Plugin Component Locations

| Component | Location | File Format |
|-----------|----------|-------------|
| Manifest | `.claude-plugin/plugin.json` | JSON |
| Commands | `commands/*.md` | Markdown |
| Agents | `agents/*.md` | Markdown |
| Skills | `skills/*/SKILL.md` | Markdown |
| Hooks | `hooks/hooks.json` | JSON |
| MCP Servers | `.mcp.json` | JSON |

### Hook Event Quick Reference

| Event | Blocking | Input Modification |
|-------|----------|-------------------|
| UserPromptSubmit | Yes (exit 2) | No |
| PreToolUse | Yes (exit 2) | Yes (v2.0.10+) |
| PostToolUse | No | No |
| Stop | No | No |
| SessionStart | No | No |

### Common Matchers

| Pattern | Matches |
|---------|---------|
| `Write` | Write tool only |
| `Write\|Edit` | Write or Edit |
| `Bash(npm*)` | Bash with npm commands |
| `*` | All tools |
