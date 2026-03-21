---
description: Plan for queue manipulation commands — cancel, skip, retry with stage-boundary checks and skip-list persistence
tags: [diagnostics, queue, commands, control, skip-list, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Queue Commands

Implements: [Runtime Observability Design](../../../designs/future/runtime-observability.md) — Queue Commands (::queue.cancel, ::queue.skip, ::queue.retry), stage-boundary checks, skip-list persistence

## Scope

**Covers:**
- Pipeline stage-boundary status checks — guard clauses at each transition point (classification → parsing → analysis → commit)
- `::queue.cancel[uri]` command — mark URI as Failed, pipeline stops at next stage boundary
- `::queue.skip[uri]` command — mark URI as Skipped, persist to `.repoql/skip-list.txt`, exclude from future processing
- `::queue.retry[uri]` command — reset Failed URI to Discovered, remove from skip list if present
- `Skipped` status (or equivalent flag) on `FileEntry` in UriRegistry
- `.repoql/skip-list.txt` persistence — read on startup, written by skip/retry commands
- `help://` documentation for queue commands at `src/RepoQL.Documentation/repoql/commands/queue.md`
- Tests for each command and stage-boundary checks

**Does not cover:**
- Per-item CancellationToken in WorkQueue (design rejected this approach)
- Queue observability UDFs (Plan: [05-queue-observability](05-queue-observability.md))
- Trust signal / footer changes (Plan: [04-trust-signal](04-trust-signal.md))
- Failure history or attempt counting (design explicitly deferred)

## Enables

- Agent cancels a stuck file without restarting the host — surgical control
- Agent permanently excludes a toxic file that crashes the parser on every restart
- Agent retries a file after fixing the underlying issue (updated parser, freed resources)
- North-star satisfied: "An agent should be able to cancel, retry, or skip individual items in the queue"

## Prerequisites

- Plan: [05-queue-observability](05-queue-observability.md) — `processing_queue()` and `failed_files()` are needed to verify commands worked. Without them, the agent can't confirm the cancel/skip/retry took effect.

## North Star

Surgical control over individual items. Cancel stops one file, not the pipeline. Skip persists across restarts, so the toxic file never poisons the queue again. Retry gives a second chance without reindexing everything. Each command confirms what it did.

## Done Criteria

### Stage-Boundary Status Checks

- The indexing pipeline shall check `UriRegistry[uri].Status` at each stage transition:
  1. Before parsing (after classification)
  2. Before analysis (after parsing)
  3. Before commit (after analysis)
- When status is `Failed` or `Skipped` at a boundary, the pipeline shall skip all remaining stages for that item
- The check shall be a dictionary lookup on UriRegistry — O(1), no measurable throughput impact
- The item remains in WorkQueue until the current stage naturally completes — no mid-stage interruption
- A test shall verify that a file marked Failed during processing is not committed to the database
- A test shall verify that a file marked Skipped during processing is not committed to the database
- A test shall verify normal files (status Indexing) pass through all stages unchanged

### ::queue.cancel Command

- A command `::queue.cancel[uri]` shall be registered following the `[CommandClass]` + `[Command]` pattern
- The command shall accept a single URI argument
- The command shall set the URI's status to `Failed` in UriRegistry with error message "Cancelled by user"
- When the URI is found in the registry:
  - If currently in the queue (status Indexing or Discovered), return: `Cancelled: {uri} (was {status} in {stage})`
  - The item stops at the next stage boundary
- When the URI is not found in the registry, return an error: `Not found: {uri}`
- When the URI is already in a terminal state (Indexed, Failed), return: `Already {status}: {uri}`
- A test shall verify cancel sets status to Failed
- A test shall verify cancel on unknown URI returns error
- A test shall verify cancel on already-indexed URI returns appropriate message

### ::queue.skip Command

- A command `::queue.skip[uri]` shall be registered
- The command shall mark the URI as Skipped in UriRegistry (either a new `Skipped` status or a separate flag on FileEntry)
- The command shall append the URI to `.repoql/skip-list.txt` (one URI per line)
- If the file is currently in the queue, it stops at the next stage boundary (same mechanism as cancel)
- The command shall return: `Skipped: {uri} (will not be processed)`
- When the URI is already skipped, return: `Already skipped: {uri}`
- A test shall verify skip sets the Skipped flag
- A test shall verify skip persists the URI to skip-list.txt
- A test shall verify skip on already-skipped URI is idempotent

### Skip List Persistence

- `.repoql/skip-list.txt` shall be a plain text file, one URI per line, no headers
- The indexing engine shall read the skip list on startup and mark matching URIs as Skipped in UriRegistry
- During file discovery, URIs in the skip list shall not be enqueued for processing
- The file shall be human-readable and human-editable — agents and users can inspect and modify it directly
- Empty lines and lines starting with `#` shall be ignored (comments)
- A test shall verify that a skipped file is not re-enqueued after host restart (simulated by re-reading the skip list)
- A test shall verify that removing a URI from the skip list allows it to be enqueued again

### ::queue.retry Command

- A command `::queue.retry[uri]` shall be registered
- When the URI's status is `Failed`, the command shall reset it to `Discovered` in UriRegistry
- When the URI's status is `Skipped`, the command shall reset it to `Discovered` in UriRegistry and remove it from `.repoql/skip-list.txt`
- The next processing cycle shall pick up the URI for re-processing
- The command shall return: `Re-enqueued: {uri} (previous: {old_status}, error: {old_error})`
- When the URI is not in Failed or Skipped state, return: `Cannot retry: {uri} is {status}`
- A test shall verify retry resets Failed to Discovered
- A test shall verify retry resets Skipped to Discovered and removes from skip list
- A test shall verify retry on non-failed, non-skipped URI returns appropriate error

### Help Documentation

- A help document shall be created at `src/RepoQL.Documentation/repoql/commands/queue.md`
- The document shall have YAML frontmatter with `description`, `tags` (queue, cancel, skip, retry, commands, diagnostics), `audience`, and `categories`
- The document shall document all three commands: `::queue.cancel[uri]`, `::queue.skip[uri]`, `::queue.retry[uri]`
- The document shall include example usage and expected output for each command
- The document shall explain that cancel stops at the next stage boundary (not immediately)
- The document shall explain that skip persists across restarts via `.repoql/skip-list.txt`
- The document shall show verification queries: `SELECT * FROM processing_queue()` and `SELECT * FROM failed_files()`

### Skipped Status on FileEntry

- `FileEntry` in UriRegistry shall support a Skipped state (either as a new `FileStatus.Skipped` enum value or as a separate boolean flag)
- Skipped files shall not count toward `IndexPending` in the footer — they are intentionally excluded, not pending
- Skipped files shall appear in `failed_files()` with a distinguishable status so the agent can see what's been excluded
- A test shall verify Skipped files are excluded from pending counts

## Constraints

- **No per-item CancellationToken** — design chose UriRegistry status change over WorkQueue changes. Trade-off: stuck parser won't be interrupted mid-stage. Processing timeouts handle true infinite loops. Restart remains the fallback for a truly stuck host.
- **Single-writer for skip list** — one host per repo (enforced by HostLock). No concurrent write conflicts.
- **O(1) stage-boundary checks** — dictionary lookup on UriRegistry. Must not degrade hot-path throughput. Design: "straightforward — a guard clause before each stage."
- **`[Command]` pattern** — follow existing command conventions in `CommandImplementations/`. Auto-discovered via attributes.
- **Skip list is plain text** — design chose human-readable over structured format. No JSON, no SQLite, no schema. One URI per line.

## References

- [Runtime Observability Design](../../../designs/future/runtime-observability.md) — Queue Commands section, stage-boundary checks
- [Queue Observability Flow](../../../flows/future/diagnostics/queue-observability.md) — intervention stages, verification queries
- `src/RepoQL.Commands/` — command framework (`[CommandClass]`, `[Command]`)
- `src/RepoQL.ConsoleApp/CommandImplementations/` — existing command implementations
- `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs` — pipeline stages where boundary checks go
- `src/RepoQL.Contracts/UriRegistry/` — FileEntry, FileStatus
- `src/RepoQL.Core/WorkQueue.cs` — queue infrastructure (no changes needed)
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

Commands are user-initiated actions — errors should be clear and specific:
- URI not found → `Not found: {uri}. Check the URI with: SELECT uri FROM _indexer_status_internal() WHERE uri LIKE '%{filename}%'`
- Invalid URI format → `Invalid URI: {input}. Expected format: file:///path/to/file`
- Skip list write fails → return error with path and reason, but still update in-memory state (skip works for current session, persistence retried on next command)

Stage-boundary checks never throw — they silently skip remaining stages. The cancel/skip command has already informed the agent of the action.
