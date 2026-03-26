---
description: "Inspect and change RepoQL configuration values across local, repo, and user scopes."
tags: ["command", "config", "settings", "configuration", "scope"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::config

Inspect and manage RepoQL settings. Shows resolved values with provenance and supports scoped writes to local, repo, or user config files.

---

## Capsule: BasicUsage

**Invariant**
`::config` lists all registered settings with resolved value, source, and description.

**Example**
```
::config
→ duckdb
    memory_limit    = <not set>  (default)  DuckDB memory cap (e.g. 4GB, 512MB)
    read_pool_size  = 3          (local)    Read connection pool size (1-4)

  llm
    api_key         = abcd****yz (user)     LLM API key
```

**Depth**
- Output groups settings by section (`duckdb`, `embedding`, `llm`, etc.)
- Values include provenance: `default`, `user`, `repo`, `local`, `environment`
- Sensitive values are masked in list/read output

---

## Capsule: ReadOne

**Invariant**
`::config.read[key]` shows full metadata for one setting key.

**Example**
```
::config.read[duckdb.memory_limit]
→ duckdb.memory_limit
    Value:          <not set>
    Source:         default
    Default:        (none)
    Env var:        REPOQL_DUCKDB_MEMORY_LIMIT
    Legacy env var: DUCKDB_MEMORY_LIMIT
    Valid values:   e.g. 4GB, 512MB
    Restart:        yes
    Description:    DuckDB memory cap (e.g. 4GB, 512MB)
```

//BOUNDARY: Unknown keys return an error and include the closest known key when available.

---

## Capsule: SetAndReset

**Invariant**
`::config.set[key, value, scope?]` writes a value.  
`::config.reset[key, scope?]` removes a value.

**Example**
```
::config.set[duckdb.read_pool_size, 3]
→ Set duckdb.read_pool_size = 3 (local)

::config.set[embedding.model_path, /models/e5.onnx, repo]
→ Set embedding.model_path = /models/e5.onnx (repo)

::config.reset[embedding.model_path, repo]
→ Reset embedding.model_path (repo)
```

**Depth**
- Default scope is `local` (`<repo>/.repoql/config.json`)
- `repo` scope writes `<repo>/.repoql.json`
- `user` scope writes `~/.repoql/config.json`
- Sensitive settings cannot be written to `repo` scope (use `local`, `user`, or env vars instead)
- Type validation is enforced before write (e.g. integer/bool settings)
- Writes are atomic (`.tmp` + rename) and config is reloaded immediately
- For restart-bound settings, success output includes `run ::host.restart`

---

## Help

```
::config --help
::config.read --help
::config.set --help
::config.reset --help
```
