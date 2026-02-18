# Configuration: What Great Looks Like

> One system for every setting — scoped, layered, discoverable, and configurable without leaving the tool.

An agent opens a repo for the first time and types `::config`. It sees every setting that matters: embedding mode, DuckDB memory limit, LLM provider — each showing its current value and where that value came from. Some say "default", some say "user" (from `~/.repoql/config.json`), one says "repo" (from `.repoql.json` committed to the repo). The agent decides this repo needs more memory for DuckDB. It types `::config[duckdb.memory_limit, 4GB]` and the setting takes effect immediately, written to `.repoql/config.json` — local to this repo, not committed, not affecting any other workspace. A teammate clones the same repo and gets the team's shared settings from `.repoql.json` automatically, without knowing configuration exists. Neither agent ever opened a file, set an environment variable, or read a configuration reference. They used the same tool surface they already knew.

---

## Scopes and Precedence

- An agent should be able to set configuration at repo scope that travels with the repository and applies to every user who clones it
- An agent should be able to set configuration at local scope that applies only to this workspace and is never committed
- An agent should be able to set configuration at user scope that applies as a personal default across all repositories
- An agent should be able to override any setting with an environment variable without touching files
- An agent should be able to see which scope a value came from — not just its value, but its provenance
- An agent should be able to predict which value wins when multiple scopes set the same key — the precedence is obvious and consistent

```
env var  >  local (.repoql/config.json)  >  repo (.repoql.json)  >  user (~/.repoql/config.json)  >  default

Most specific wins. Env overrides everything. Defaults are always there.
```

---

## Discovery

- An agent should be able to see every configurable setting, its current value, and where that value came from — with a single command
- An agent should be able to read a single setting by name and see its value, source scope, default, and description
- An agent should be able to discover configuration through context — when something is unconfigured and could be, the tool says so
- An agent should not need to know that configuration exists before encountering it — the tool surfaces relevant settings at the moment they matter
- An agent should be able to find configuration documentation through `help://` when it needs depth

```
::config
→ duckdb.memory_limit    4GB       (local)    DuckDB memory cap
  duckdb.threads         auto      (default)  DuckDB thread count
  embedding.mode         full      (repo)     none | structure | full
  embedding.model_path   <unset>   (default)  Path to ONNX model
  llm.api_key            ****...   (env)      OpenRouter API key
  ...

::config[embedding.mode]
→ embedding.mode
  Value:    full
  Source:   repo (.repoql.json)
  Default:  full
  Env var:  REPOQL_EMBEDDING_MODE
  Options:  none | structure | full

-- Semantic search returns no results
→ Embeddings disabled. Current setting: ::config[embedding.mode] = none
  To enable: ::config[embedding.mode, full]
```

---

## Mutation

- An agent should be able to change a setting and have it take effect without restarting the host
- An agent should be able to choose which scope to write to — local by default, with an explicit scope argument
- An agent should be able to reset a setting to its default without knowing what the default is
- An agent should be able to change multiple related settings in one command
- An agent should be able to trust that invalid values are rejected before they're written — not after a restart

```
::config[duckdb.memory_limit, 4GB]           -- writes to local scope (default)
::config[embedding.mode, full, repo]         -- writes to repo scope (committed)
::config[embedding.mode, full, user]         -- writes to user scope (global)
::config[-duckdb.memory_limit]               -- reset to default (removes local override)
::config[-duckdb.memory_limit, repo]         -- reset at repo scope
```

---

## Environment Variables

- An agent should be able to use environment variables to override any setting — the mapping between setting names and env vars is predictable
- An agent should be able to see which env var controls a setting without memorizing a naming convention
- An agent should be able to set env vars for CI, containers, or other contexts where file-based config is impractical
- An agent should be able to trust that env vars always win — they are the escape hatch that overrides everything

```
Setting name:  duckdb.memory_limit
Env var:       REPOQL_DUCKDB_MEMORY_LIMIT

Setting name:  embedding.mode
Env var:       REPOQL_EMBEDDING_MODE

-- Convention: REPOQL_ + UPPER_SNAKE_CASE(setting name with . → _)
-- One rule, no exceptions.
```

---

## Persistence

- An agent should be able to find config files in predictable locations — no searching, no guessing
- An agent should be able to commit repo-scope config alongside code so teammates get shared settings automatically
- An agent should be able to exclude local-scope config from version control without any `.gitignore` changes
- An agent should be able to hand-edit config files when that's more convenient than commands — the format is JSON, readable, and documented

```
~/.repoql/config.json          -- user scope: personal defaults
<repo>/.repoql.json             -- repo scope: shared with team, committed
<repo>/.repoql/config.json     -- local scope: this workspace only, gitignored
```

---

## Validation and Safety

- An agent should be able to trust that a setting change either succeeds completely or fails with an explanation — never partial writes
- An agent should be able to see what values are valid for a setting before guessing
- An agent should be able to trust that config file syntax errors don't break the host — the tool falls back to defaults and reports what's wrong
- An agent should be able to trust that sensitive values (API keys) are never logged, never shown in full in `::config` output, and never written to repo-scope config

```
::config[embedding.mode, turbo]
→ Invalid value 'turbo' for embedding.mode. Valid: none | structure | full

::config[llm.api_key, sk-1234, repo]
→ Refused: llm.api_key is sensitive and cannot be written to repo scope.
  Use local or user scope, or set REPOQL_LLM_API_KEY as an environment variable.

-- Corrupt config file
→ Warning: .repoql/config.json has invalid JSON (line 4, column 12).
  Using defaults. Fix the file or run ::config[-*, local] to clear local overrides.
```

---

## Live Reload

- An agent should be able to change a setting and see the effect immediately — no restart, no reconnect
- An agent should be able to trust that settings requiring a restart say so explicitly and offer to restart
- An agent should be able to trust that concurrent agents on the same repo see config changes without re-reading files manually

```
::config[duckdb.memory_limit, 8GB]
→ duckdb.memory_limit set to 8GB (local).
  Note: takes effect on next host restart. Restart now? ::host.restart

::config[embedding.mode, full]
→ embedding.mode set to full (local). Effective immediately.
  Embedding generation will begin on next idle cycle.
```

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| An agent should be able to see every setting's value and provenance in one command | No hunting through env vars, files, and defaults separately |
| An agent should be able to change a setting without leaving the query tool | Configuration is part of the workflow, not a separate task |
| An agent should encounter configuration contextually, not by reading a reference | The tool teaches configuration at the moment it's relevant |
| An agent should be able to predict precedence without reading documentation | Env > local > repo > user > default — obvious and consistent |
| An agent should be able to commit shared settings alongside code | Team configuration is version-controlled and reviewable |
| An agent should be able to trust that invalid or dangerous config is rejected before it's written | No silent corruption, no accidental secret leaks |
| An agent should never need to restart RepoQL after changing a setting unless told explicitly | Live reload is the default; restart is the exception |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Require agents to know env var names | An agent should discover settings by name and see the corresponding env var |
| Store secrets in repo-scope config | An agent should be protected from writing sensitive values to shared config |
| Silently ignore malformed config files | An agent should see what's wrong and be able to fix it |
| Require restart for every change | An agent should see changes take effect immediately unless told otherwise |
| Invent a new config format | An agent should be able to hand-edit JSON files when commands aren't convenient |
| Add settings without defaults | An agent should be able to use RepoQL with zero configuration |
| Mix config and data in `.repoql/` | An agent should know that `.repoql/config.json` is the one config file in that directory |

---

*Zero configuration to start. Full control when you need it. The tool works before you know settings exist — and adapts the moment you change one.*
