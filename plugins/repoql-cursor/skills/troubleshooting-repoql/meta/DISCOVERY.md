# Discovery — troubleshooting-repoql

Working notes behind the 2026-06 refine. Records what was verified against the live host + `repoql.core` source, and the decisions that shaped the structure. Messy on purpose; the skill is the clean residue.

## Goal

Refine the skill into a lean SKILL.md that **frontloads the non-derivable must-knows** and acts as a **decision tree routing to granular, well-named references** — modelled on what makes `mermaid-diagrams` effective (thin spine + one focused reference per case). Plus an accuracy audit.

## Structural model (why it's shaped this way)

- **`mermaid-diagrams`** = thin spine + a "Choosing the Right Diagram" routing table + one granular reference per type. Adopted: spine of must-knows + symptom→reference table + per-layer references.
- **Mermaid's references are host-served at `help:///skills/mermaid-diagrams/**`.** A troubleshooting skill **cannot** do that: `help://` (and SQL, and `command()`) all travel through the MCP→host bridge, so when the host is down — the #1 troubleshooting symptom — they're all unreachable. **Therefore every reference is a plugin-local file read with native tools.** This is the load-bearing design constraint, confirmed by the engine's own north-star (`docs/north-star/diagnostics.md`: "the diagnostic system can't depend on the thing it's diagnosing").
- References still *route to* `help:///schema/**` and `help:///operations/**` for exhaustive, always-current signatures — but only as "when the host is up" depth, never as the floor. This keeps signatures from drifting (the previous skill's main failure).

## Source of truth

- This plugin repo holds the canonical copy. The upstream `repoql.core/.claude/Skills/diagnose/` is **being retired** (user-confirmed) — do not sync to/from it. It was also stale (see below).
- Two byte-identical copies ship: `plugins/repoql/…` and `plugins/repoql-codex/…`. Keep them identical (author in `repoql`, `cp` to `repoql-codex`).
- `.repoql/cache/` and the imported `repoql.core` are gitignored / partly outside the index → inspected with native `ls`/`cat`. The imported `repoql.core` *is* indexed (it's in `mounts.json`), so source reads of it are valid.

## Accuracy audit (verified 2026-06)

Corrections folded into the rewrite:

1. **`.repoql/` layout was wrong.** Authoritative source `RepoqlPaths.cs` (`RepoQL.Hosting.Contracts`): two zones — `.repoql/` root = committable (`.gitignore` marker, `concepts/`, `mounts.json`); `.repoql/cache/` = gitignored runtime (`host.log`, `host.lock`, `repoql.sock`, `socket.path`, `index.duckdb`, `dashboard-bind.json`, `imports/`, `otel/`). Old `repoql` wrote runtime files to the **root**; current `rql` writes to **`cache/`**. The previous skill documented the old root paths.
2. **`.repoql/diagnostics/*.json` is old-repoql only.** `socket-bind.json`, `database-init.json`, `services-start.json`, `host.version`, host stderr file: **zero** matches in current `repoql.core` source. The stale copies on disk are from v1.5.x. Dropped from the current-state story.
3. **`account login --device-code` → `--mode device-code`** (verified `account login --help`).
4. **"memory breakdown not exposed" was wrong** — `diagnostics memory` exists in the `command` tool.
5. **Aspirational ≠ shipped.** The retired upstream skill and the north-star describe `system_health()`, `processing_queue()`, `failed_files()`, `system_resources()`, `::queue.cancel/retry/skip`, `::diagnostics(.fast/.index/.cloud)`, `reindex`. **None exist** in the live catalog. Replaced with shipped equivalents and an explicit "don't invent these" must-know.

## Live diagnostic surface (verified via duckdb_functions/views + DESCRIBE + help:///schema)

- **One-row health:** `engine_status` (state ∈ indexing|embedding|ready; semantic_percent; failed). This is the real "system_health".
- **Per-file / queue:** `indexing_registry`, `indexing_queue` (views); `indexing_stuck_candidates(n)`, `indexing_file_audit(uri,n)` (macros).
- **Operations / imports:** `Operations` (caller-facing view), `indexing_operations` (raw ledger), `operation_deferrals(op_id)`, `Filesystems` (per-mount aggregation).
- **DB write layer:** `graph_lock_stats(role)`, `graph_write_phase_stats(write_kind,phase)`; DuckDB-native `duckdb_memory`, `duckdb_temporary_files`, `pragma_database_size`.
- **MCP bridge:** `mcp_bridge_errors()` (has `retryable` + `next_action`!), `mcp_servers()`, `mcp_server_sources()`, `mcp_tools()`, `mcp_tool_params()`.
- **Coverage:** `file_coverage(scope)`, `symbol_coverage(uri)`, `uncovered_symbols(scope)`.
- **Scalars:** `host_meta(key)`, `dashboard_url()`, `ask()`.
- **Catalog:** `help:///schema/views/**` (11), `help:///schema/functions/table/**` (30), `help:///schema/functions/scalar/**` (4). `help:///operations/diagnosing-issues.md` is the canonical SQL-first workflow.

## Command surface reality

- In-session `command()` tool exposes only: `init`, `account whoami|login|logout`, `config list|read|set`, `import add|list|remove`, `diagnostics memory`, `host status|start|stop|restart`, `dashboard`.
- The broader `rql` CLI (terminal, `! rql …`) adds `rql mcp list|reload|retry|auth|revoke|add|disable|enable|allow|disallow` and `rql update` — documented at `help:///commands/`. MCP-bridge recovery is CLI-only; the skill says so.

## The trust footer (shipped, was unmentioned)

Every `query`/`read`/`explore` response ends with `[… | index: 57% (2823 pending) | semantic: 79% | stale: 1376]` or `[… | ready]`. It's the cheapest signal (the north-star "Glance" tier) and mirrors `engine_status`/`Trust.FromEngineState`. Now must-know #1.

## Frontloaded must-knows (final set)

1. Trust footer = free first signal. 2. Host auto-launches; restart is deliberate/medium-risk; verify by re-running. 3. `.repoql/` two-zone layout + old/new migration trap. 4. SQL surface is truth + self-describing (`DESCRIBE`, `help:///schema`). 5. Controls are narrow — no queue/reindex commands; don't invent the aspirational surface. 6. Host down ⇒ bridge down ⇒ native file reads only.

## References (granular, plugin-local)

`repoql-folder` · `host-and-connection` · `database-layer` · `indexing-and-coverage` · `imports-and-operations` · `mcp-bridge` · `cloud-auth-and-search` · `host-wont-respond` (the offline floor — native tools only).

## Follow-up additions (user-flagged)

- **Working directory is a top root cause.** The MCP server is a thin client to a *shared host, one per workspace*, resolved from the cwd. `WorkspaceResolver.cs`: walk up to the nearest `.git` or `.repoql/.gitignore` marker; no marker ⇒ refuses to index (so a stray cwd never auto-indexes a home folder); `REPOQL_CWD` pins the root for harnesses with unreliable cwd. Promoted to **must-know #1** + expanded in `host-and-connection.md` + new symptom-table row ("Nothing indexed / the wrong repo's results"). Confirm attachment via `Filesystems.local_path`.
- **Windows antivirus → slow file discovery.** Real-time scanning (Windows Defender) inspects every file during discovery; exclude the workspace/`.repoql/` folder. Deliberately *not* frontloaded — added to `indexing-and-coverage.md` under "Slow discovery on Windows". Tell: nothing is actually hung (`indexing_stuck_candidates`/`graph_lock_stats` look healthy), it's just slow.

## Validation (fresh-subagent test, 2026-06)

Four blind subagents, each given only a symptom + the working-copy files (the deployed plugin cache is still the old monolith). Three diagnosed via the skill; one judged the description's trigger calibration. All four reached the right answer; routing converged with no dead ends. Fixes they surfaced, now applied:

- **Trigger calibration (PASS).** Fired on all 5 genuine malfunctions, skipped PR-review/mermaid/usage-question. Wording gaps closed: description now names *query* slowness and the *not-a-workspace / nothing-indexed* case.
- **Wrong-repo (PASS).** must-know #1 alone pinned it. Fixed: (a) "footer's counts" was misleading for provenance → now `Filesystems.local_path`; (b) added the alternative cause — imports bleeding into *unscoped* results (same symptom, different fix: scope or `imports-and-operations.md`).
- **MCP tool failing (PASS).** Verified every cited macro/column live. Fixed: `mcp-bridge.md` now leads with `mcp_servers()` (always populated; carries the auth-expiry signal) and flags that `mcp_bridge_errors()` is **empty** for never-connected/auth-expired servers — the exact live case (`cloudflare-api` inheritedExpired).
- **Dead host (PARTLY → fixed).** Ground-truth disk check caught two wrong paths in the offline floor: the live host log is the **rolling `cache/host_*.log`**, not `cache/host.log`; and there is **no `cache/host.lock`** — the live PID is in the newest log's `PID` line / `ps`, while the root `host.lock` is the stale-PID trap the skill elsewhere warns about. Both corrected in `host-wont-respond.md` + `repoql-folder.md`, with an explicit "never delete a live host's socket" guard. Confirmed independently: `cache/host_014.log` → `PID 56882` (live); root `host.lock` → `PID:33081` (dead).

Lesson: the offline reference must be checked against a real `.repoql/` on disk, not the engine's path *constants* — `RepoqlPaths` declares `host.log`/`host.lock`, but the running build rolls logs and doesn't surface a cache lock. Disk truth beats source constants for the dead-host path.

## Deep pass — completeness gaps (the "is it as good as it can be?" review)

Adversarial re-read for the high-stakes role (the skill agents reach for when RepoQL fails). Live evidence exposed a real blind spot and two under-used levers, all now closed:

- **User-error vs infrastructure-error gate (was missing — the biggest gap).** A malformed query is the *most common* RepoQL failure, and the engine enriches it: bad column → `Candidate bindings:`; bad view → `Did you mean …?`; read-only surface refuses `SET`/`PRAGMA`/writes/multiple-statements by design. The skill routed *everything* to infra diagnosis, so an agent with a typo'd column would go check `host status`. Added a gate at the top of the symptom router + a top table row that bounces user errors to `DESCRIBE` / `help:///schema`. Verified live (Binder + Catalog errors both enriched).
- **`config` as a recovery lever (under-used).** `config list` is rich and real: `duckdb.memory_limit`, `indexing.workers`, `embedding.batch_size`/`concurrency`, `dotnet.analysis` (expensive, off by default). The north-star's flagship OOM recovery. Added a "Recovering from memory pressure / OOM" section to `database-layer.md` with the real keys + `config set` syntax.
- **Idle shutdown (false-alarm risk).** `host.idle_grace_seconds = 45`: the host shuts *itself* down after idle and relaunches on next call — "it was there, now it's gone" is usually normal. Added to must-know #3.
- **Stale index / "edits not reflected" (was missing).** Footer `stale: N` = `registry.Dirty` (`Trust.cs:37`). Workspace is `watching=True` (auto-resweep); imports are `watching=False` (static — re-`import add` to refresh). Verified live. Added a symptom row + a staleness section to `indexing-and-coverage.md`.

All additions evidence-grounded (live `config list`, `Trust.cs`/`Footer.cs`, `Filesystems.watching`, live enriched errors) — held to the same no-drift bar as the original audit.
