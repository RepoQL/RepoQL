# Building RepoQL: Awareness Guide

> For Claudes arriving cold. Not deep understanding — awareness of what exists, so you know what you don't know and where to look.

## What This Is

RepoQL indexes repositories into a DuckDB graph database so LLM agents can query structure without reading files. Four MCP tools — `explore`, `read`, `query`, `import` — give agents structural awareness of any codebase. Files become nodes, symbols become children, relationships become edges, diagnostics become annotations. All queryable via SQL.

**The bet:** Everyone else builds AI. We build conventional software that makes AI dramatically more capable.

**The aesthetic:** A beautifully crafted Japanese tool. If your first instinct doesn't work, it's our bug. If a feature needs a tutorial, it's the wrong shape.

---

## Components

These are the major subsystems. When you encounter one, you know it exists and roughly what it does — go deeper when you need to.

### 1. The Graph Store
DuckDB with five frozen tables (`artifact`, `node`, `edge`, `span`, `annotation`). Single writer enforced through `DuckDbDataStore`. Never add tables — extend via views, macros, UDFs. Plus `document_embedding` for vector search and git tables for history.

Views expose the graph as familiar SQL (e.g. `Files`, `Types`, `Functions`). Macros add capabilities (e.g. `search()`, `snippet()`). The SQL surface is where capability grows while the schema stays stable.

**Go deeper:** `docs/Schema.md`, `src/RepoQL.Data.DuckDB/`

### 2. The Indexing Pipeline
Turns files into graph rows. IndexItem is a flow object — accumulates state through stages, never replaced.

**Hot path** (per file, concurrent): Classification → Parsing → Analysis → Commit.
**Idle processing** (after hot path drains): Pruning → Embeddings → Vector refresh → Multi-file analysis → Index rebuild.

Epoch tracking batches files together so idle processing fires once per batch. Format handlers are used by pipeline stages to classify, parse, and analyze — they return DocumentModel records, never touch the database directly.

**Go deeper:** `docs/flows/current/indexing/`, `src/Indexing/RepoQL.Indexing/`, `PROCESSOR_GUIDE.md`

### 3. The Format System
Pluggable parsers that teach the pipeline how to understand each file type. Each provides: classifier (refine media type), parser (extract Records), analyzer (optional diagnostics), x-ray templates (Liquid → headline/summary/structure), schema scripts (optional views/macros).

`IFormatLoader` implementations discovered via DI. Naming has tension from rewrites — format handlers are used BY pipeline stages, not the other way around.

Many formats supported — see `Formats/*` projects. Each is a separate project per format family.

**Go deeper:** `PROCESSOR_GUIDE.md`, any `Formats/*` project, `docs/north-star/formats.md`

### 4. The File System Abstraction
VFS layer — `IVirtualFileSystem` per scheme, `IMultiFileSystem` as router. Makes `file://`, `help://`, `github://` all look the same to the pipeline. What lets embedded docs exist, what lets GitHub import mount new repos as indexable file systems.

Schemes: `file://` (disk), `help://` (embedded docs compiled into DLL), `github://` (cloned imports), in-memory (tests).

Note: not all imports go through VFS. GitHub imports mount new file systems. But import is broader — SARIF import would annotate existing graph nodes without a new file system. VFS is one mechanism import uses, not the only one.

**Go deeper:** `src/RepoQL.FileSystem/`

### 5. The Explore/Read Engine
The intelligence between "agent asks" and "agent gets an answer." Explore does broad search with complex hybrid scoring (BM25 + fuzzy + semantic), then uses scores to allocate token budget across results. Intent modifies the allocation curve — Inventory is wide/shallow, Inspect is narrow/deep. Read does URI + budget → richest representation that fits, with modifiers for transformation.

**Go deeper:** `src/RepoQL.Explore/`, `docs/north-star/read-tool.md`

### 6. The UDF System
Add SQL functions by writing C# with `[UdfClass]`/`[ScalarUdf]`/`[StructuredUdf]` attributes. Auto-discovered at startup. Framework generates SQL macros wrapping C# implementations.

**Go deeper:** `src/RepoQL.Data.DuckDB/UdfFramework/`, `src/RepoQL.Data.DuckDB/UdfImplementations/`

### 7. The MCP Client Registry
Discovers and connects to external MCP servers (from Claude Code config, Claude Desktop config, directory configs). Generates SQL macros so any MCP tool becomes callable from SQL. This is how RepoQL extends infinitely — query Postgres, New Relic, Aspire, any MCP server, join results with the code graph.

**Go deeper:** `src/RepoQL.Mcp.Client/`

### 8. The Two Processes

```
Claude Code ←stdio/JSON-RPC→ MCP Client ←gRPC/Unix socket→ Host
```

**MCP Client** (stdio): tool handlers, commands, MCP protocol. Dev loop: `deploy.ps1` → `/mcp` reconnect.
**gRPC Host** (per-repo): indexing, queries, explore/read, UDFs. Dev loop: `dotnet watch` auto-rebuilds.

`dotnet watch` can't work for stdio (contaminates stdout, breaks JSON-RPC). Host is where most development happens. Debug builds auto-launch host via `dotnet watch`.

**Go deeper:** `src/RepoQL.ConsoleApp/Host/`, `src/RepoQL.Protocol/`

### 9. The Command System
`::commands` provide imperative admin actions through the query surface (e.g. `::diagnostics`, `::reindex[scope]`). Attribute-based discovery: `[CommandClass]` + `[Command("name")]`. Commands never overlap with what SQL can do — `::?` lists what's available.

**Go deeper:** `src/RepoQL.Commands/`, `src/RepoQL.ConsoleApp/CommandImplementations/`, `docs/north-star/commands.md`

### 10. The Documentation System
`help://` is a first-class URI scheme. All docs are embedded in the `RepoQL.Documentation` DLL, served via `DocumentationFileSystem` (which wraps `EmbeddedStore`). Queryable with the same explore/query/read you use on code. If agents can't find it via `explore(uriGlob="help://**")`, it doesn't exist.

**Go deeper:** `src/RepoQL.Documentation/`

### 11. The Embedding System
Local ONNX model for semantic search. Generates embeddings at document and object scope during idle processing. Vector similarity search via DuckDB VSS extension.

**Go deeper:** `src/RepoQL.Embeddings/`

### 12. The UriRegistry
In-memory source of truth for what exists in the repository and what state it's in. A `ConcurrentDictionary<RepoUri, FileEntry>` tracking every file through its lifecycle: `Discovered → Indexing → Indexed` (plus `Failed` and `Stale`) with a parallel embedding track: `Pending → Embedding → Embedded/NotApplicable/Failed`. Each entry also carries child symbols (for glob matching without hitting the database), line count, timestamps, and errors.

Operations poll against it. Scope readiness checks query it. Glob matching uses it for symbol resolution. DuckDB has the data; UriRegistry knows what's *ready*.

**Go deeper:** `src/RepoQL.Contracts/UriRegistry/`

### 13. The Operations System
Tracks batches of indexing work to completion. When you import, reindex, or start the host, an operation tracks a fixed set of URIs through indexing → embedding → ready. Polls UriRegistry every 500ms. Awaitable (`operation.Completion`). Queryable via SQL (`_operations()`, `_operation_log(id)`). In-memory, transient, agnostic to what triggered it. This is how "are my files ready to query?" gets answered.

**Go deeper:** `docs/designs/current/operations.md`, `src/RepoQL.Contracts/Operations/`

### 14. The Host Lifecycle
The gRPC host is a standalone server (Unix sockets) shared by any agents working in the repo. It can run independently of any client. When agents launch it on demand, they hold leases to keep it alive — when all leases expire, an implicitly-started host shuts down after a grace period. Starting a new host when one is already running cooperatively shuts down and replaces the existing one. Host lock ensures single instance per repo. PID file enables zombie detection and eviction.

**Go deeper:** `src/RepoQL.ConsoleApp/Host/`

### 15. Snapshots
Pre-computed indexed data shipped with the binary. Exists so `help://` documentation is instantly queryable on first startup without re-indexing embedded docs. Generic mechanism — could be used for other pre-computed data.

**Go deeper:** `src/RepoQL.Data.DuckDB/Snapshots/`, `src/RepoQL.Contracts/Snapshots/`

### 16. Observability
OpenTelemetry throughout — metrics, traces, activities. Aspire (via `RepoQL.Orchestrator`) shows telemetry in development. Logs go to `.repoql/` file for startup failures and field debugging. The dashboard (React app in `/dashboard/`) addresses "what is it doing?" with real-time status streaming via gRPC `WatchStatus`. Long-term plan: RepoQL server as an OTEL collector.

**Go deeper:** `dashboard/`, `src/RepoQL.Orchestrator/`, `src/RepoQL.ConsoleApp/Diagnostics/`

### 17. Agent Integrations
Plugins for Claude Code and clawdbot/openclaw make integration easier and provide skills that teach agents effective RepoQL patterns. The Claude Code plugin will become increasingly important — it's what gives agents the "desire path" instincts (explore first, don't read files for structure, let the graph do the work).

**Go deeper:** `integrations/claude-code/`, `integrations/clawdbot/`

---

## Addressing

Everything is a URI. Fragments for precision, globs for breadth, `;` to combine, `!` to exclude.

```
file:///src/Auth.cs                        # whole file
file:///src/Auth.cs#symbol=ValidateToken   # specific method
file:///src/Auth.cs#line=42,60             # line range
help:///quickstart.md                      # embedded docs
github://owner/repo                        # imported repo
file:///src/**/*.cs;!**/tests/**           # glob with exclusion
```

**SemanticMediaType** encodes wire format + semantic role: `text/plain;kind=code.csharp`, `application/json;kind=openapi;version=3.1`.

---

## Token Economics

Token management is a cross-cutting concern. Multiple mechanisms work together so agents spend precisely what they budgeted.

**Pre-computation** — X-ray summaries (headline/summary/structure) and the graph itself mean agents understand files without reading content. The most effective token saving is the one you never spend.

**Budget as input** — `explore`, `read`, and `query` all accept `tokenBudget`. The caller decides how much to invest.

**Representation cascade with redistribution** — In explore, the allocator assigns a budget per result based on search scores. The renderer tries the richest representation (structure). If it doesn't fit, it downgrades (headline). Leftover tokens return to a redistribution pool and get spent on other results. Budget is never wasted.

**Fragment addressing** — `#symbol=Foo`, `#line=42,60` let you read just the part you need. Don't pay for the whole file.

**Read modifiers** — `=> tree: folders` vs `=> tree: headlines` vs `=> structure` control information density. Some modifiers do their own budget allocation across matched files.

**Budget overflow consent** — When content exceeds budget, the tool doesn't truncate silently. It returns a message: "this costs X tokens — repeat the request exactly to get the full response." The agent consciously opts in to overspending. Applied across all tools.

**LLM summarization in query** — If query results exceed budget and the SQL contains comments, those comments become a question for a cloud LLM to answer from the data. `-- what are the most common error patterns?` + 500 rows → synthesized answer instead of raw data. Only with LLM features enabled.

**Intent as allocation curve** — Explore's intent parameter shapes how budget is distributed. Inventory spreads thin across many results. Inspect concentrates on few results with depth.

**Go deeper:** `src/RepoQL.Explore/` (allocation, rendering), `docs/north-star/read-tool.md` (modifiers)

---

## The Four Tools + Commands

| Tool | Purpose | Key insight |
|------|---------|-------------|
| `explore` | Find things you don't know the location of | Broad search → score → allocate budget per result. Intent shapes wide vs deep. |
| `read` | Fetch known content with budget control | URI + budget → richest representation that fits. Modifiers transform output. |
| `query` | SQL over everything | Graph, git, external MCP servers, parsed data — all in one query. |
| `import` | Bring external data into the graph | Repos (VFS mount), analysis reports (SARIF → annotations), observability data, anything. Mechanism varies by source. |
| `::commands` | Admin without leaving the query surface | Imperative actions. `::` prefix is unambiguously not-SQL. |

---

## Testing

- **TUnit** — `[Test]` not `[Fact]`. Filter: `dotnet run -- --treenode-filter "/*/*/*/MyTest*"`
- **AwesomeAssertions** — same API as FluentAssertions. `using AwesomeAssertions;`
- **FakeItEasy** — `A.Fake<T>()`, `A.CallTo(() => mock.Method(A<string>._)).Returns(...)`
- Tests mandatory for pipeline, formats, UDFs.
- Test helpers in `RepoQL.Testing`.

---

## Promises

Every change must uphold all of these simultaneously. The surface area grows but the promises don't relax.

**Results are trustworthy or loudly not.** If the index is incomplete, we say so. If a query fails, we explain why. Partial results that look complete cause cascading harm. The footer on every response shows readiness state.

**Budget is a contract.** You asked for 3000 tokens, you get 3000 tokens of value. Not 2000 with wasted space. Not 5000 because we thought you'd want more. Overspend wastes context. Underspend leaves value on the table. Both betray trust.

**Errors are actionable.** Not "query failed" but "no files matched X — try Y." Every error message is a signpost back to the path. If an agent can't recover from an error without human help, that's a bug in the error message.

**One bad file never breaks anything else.** Parse failure in one file doesn't stop indexing. A corrupt import doesn't affect local files. Format code runs in a way that accounts for hangs, crashes, and unexpected failures — and protects the rest of the system from them while surfacing what happened.

**The tool is self-documenting.** If it exists but agents can't discover it through `help://`, it effectively doesn't exist. Features ship with docs.

**Database integrity is architectural.** Single writer through `DuckDbDataStore`. This isn't aspirational — the architecture makes corruption impossible.

**Desire paths, not tutorials.** The paths already worn into the grass, paved. An agent's first instinct should work. If it doesn't, that's our bug, not a gap in documentation. If a feature needs explaining, it's the wrong shape.

**Agents can self-heal.** Claude should be able to diagnose and fix environmental problems with RepoQL whenever possible. We provide agent-first, high-effort error messages and tooling to enable self-help. The tool and the agent are partners — a failure the agent can't act on is a failure in the tool.

**Transport agnostic.** Anything you can do with MCP you can also do with CLI or gRPC. The MCP client is one of potentially many clients. No capability is locked to a single transport.

---

## What Will Kill Us

These are existential. Every feature, every PR, every design decision must be evaluated against them.

**"What is it doing?"** — Some of RepoQL's work is complex and time-consuming. Users cannot be left wondering if it's working. Progress visibility, status events, operation tracking — the system must always be observable. Silence is indistinguishable from broken.

**Robustness** — It needs to work consistently, self-heal (sometimes with Claude's help), and never get into an inoperable state. Format code in particular must account for all the ways format support classes may fail, hang, or break — and protect the system from them while surfacing what happened. An unrecoverable state is unacceptable.

**Effortless setup** — It should "just work" wherever you choose to use it. Initial setup should be user-friendly. Configuration should be something you can ask Claude to do. Failing to start up after install is not acceptable.

**Time-to-usable** — The time from zero to explorable is our primary KPI. This must be aggressively optimized. Every second between "install" and "first useful query" is friction that erodes trust.

**Runs on a developer laptop** — No cloud dependencies, no GPU requirements, no containers. RepoQL must run comfortably alongside an IDE, a browser, and an LLM client on a normal developer machine. Resource consumption (memory, CPU, disk) must stay reasonable. If it can't run where developers work, it doesn't matter how good it is.

---

## Hard Rules

1. **Single writer** — ALL DuckDB writes through `DuckDbDataStore`. Violating = corruption.
2. **Schema frozen** — 5 tables never change. Extend via views/macros/UDFs only.
3. **Errors never cascade** — one bad file must never stop indexing of others.
4. **Tests mandatory** — especially for pipeline, formats, UDFs.
5. **Transport parity** — CLI, MCP, and gRPC support the same features.
6. **Docs with features** — new functionality must include `help://` docs.
7. **Perfection > compatibility** — get it right rather than accumulate debt.

---

## Project Map

| Project | What | Go deeper when... |
|---------|------|--------------------|
| `RepoQL.ConsoleApp` | CLI + gRPC host, tool handlers, commands | Adding tools, commands, host behavior |
| `RepoQL.Commands` | Command framework (`::` syntax) | Adding/modifying commands |
| `RepoQL.Data.DuckDB` | Graph store, UDFs, schema, single writer | Adding SQL functions, schema work |
| `RepoQL.Indexing` | Pipeline, epoch tracking, coordination | Pipeline behavior, indexing bugs |
| `Formats/*` | One project per format family | Adding/fixing format support |
| `RepoQL.Explore` | Search, rendering, budget allocation | Search quality, explore behavior |
| `RepoQL.Contracts` | Shared types — RepoUri, SemanticMediaType, models | Understanding data flow |
| `RepoQL.Protocol` | gRPC proto, client, transport | Client-host communication |
| `RepoQL.FileSystem` | VFS abstraction | Adding URI schemes, file system work |
| `RepoQL.Embeddings` | ONNX embedding model | Semantic search behavior |
| `RepoQL.Mcp.Client` | External MCP server integration | MCP-from-SQL, config discovery |
| `RepoQL.Core` | Shared indexing infrastructure | Format registry, pipeline snapshots, work queue, EditorConfig, metrics |
| `RepoQL.Templating` | Liquid templates for x-ray | X-ray generation |
| `RepoQL.Grammar` | ANTLR grammar support | Parser development |
| `RepoQL.LLM.Client` | LLM provider abstraction | LLM-powered features |
| `RepoQL.Web` | Blazor web UI | Diagnostics, testing UI |
| `RepoQL.Documentation` | Embedded docs (help:// source) | Adding/updating help docs |
| `RepoQL.Orchestrator` | Aspire host for dev telemetry | Development observability |

---

## Building and Running

```bash
dotnet build RepoQL.sln                    # Build everything
dotnet test RepoQL.sln                     # Run all tests
dotnet run -- --treenode-filter "/*/*/*/MyTestName*"  # Filter tests (from test project dir)
```

**Dev loop:** Host code → `dotnet watch` auto-rebuilds. MCP code → `deploy.ps1` → `/mcp`.

**Version** lives in `src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj` → `<Version>`. Nowhere else.

---

## North Stars

Detailed vision documents live in `docs/north-star/`. Read them when working in an area — they describe what "great" looks like for each capability: formats, read tool, commands, diagnostics, extensibility, globbing, reliability, the plugin, the web UI, and more.

---

*You are the artisan and the user. Build the tool you'd never want to work without.*
