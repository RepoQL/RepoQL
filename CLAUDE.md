# CLAUDE.md

**Repo:** https://github.com/stueeey/RepoQL

## What Is RepoQL

Local, queryable knowledge graph for repositories. Files → DuckDB → SQL. Agents query structure without reading files.

**The bet:** Everyone else builds AI. We build conventional software that makes AI dramatically more capable.

**The aesthetic:** A beautifully crafted Japanese tool — simple, effective, intuitive, durable. LLM desire paths made manifest. If your first instinct doesn't work, it's our bug. If a feature needs a tutorial, it's the wrong shape.

```
Files → IndexItem (flow object) → Pipeline → DuckDB (5 tables)
                                                  ↓
Agents → explore/query/read/import tools → SQL + UDFs → Results
```

**Everything is addressable:**
- `file:///src/Foo.cs#symbol=Bar` — code
- `help:///quickstart.md` — embedded docs (queryable with the same tools)
- `github://owner/repo` — imported repos, analysis reports, anything

**First action** — see the shape:
```
read("file:///src/** => tree: folders", 3000)
```

---

## What Matters

### Promises

Every change must uphold all of these. The surface area grows but the promises don't relax.

| Promise | What it means |
|---------|---------------|
| Results trustworthy or loudly not | Never return incomplete results as complete. Footer on every response shows readiness. |
| Budget is contract | Token budget is precise. Overspend wastes context. Underspend leaves value on the table. |
| Errors are actionable | Every error is a signpost back to the path. If an agent can't self-recover, the error message is the bug. |
| Desire paths, not tutorials | An agent's first instinct should work. If it needs explaining, it's the wrong shape. |
| Agents can self-heal | High-effort error messages and diagnostic tooling enable Claude to fix environmental problems without human help. |
| One bad file never breaks anything else | Parse failures, import failures, format crashes — all isolated. |
| Self-documenting | If agents can't discover it via `help://`, it doesn't exist. Features ship with docs. |
| Runs on a developer laptop | No cloud deps, no GPU, no containers. Must run alongside IDE + browser + LLM client. |

### What Will Kill Us

Evaluate every feature, PR, and design decision against these.

| Threat | Why it's existential |
|--------|---------------------|
| "What is it doing?" | Complex, time-consuming work + silence = users assume it's broken. Progress must always be visible. |
| Robustness | Must work consistently, self-heal, never reach an inoperable state. Format code must be hardened against hangs, crashes, and unexpected failures. |
| Effortless setup | Must "just work." Configuration via Claude. Failing to start after install is unacceptable. |
| Time-to-usable | Zero to explorable is our primary KPI. Aggressively optimize. |

### Hard Constraints

Violating these causes corruption, test failures, or architectural drift.

| Constraint | Rule |
|------------|------|
| Single writer | ALL DuckDB writes through `DuckDbDataStore` |
| Schema frozen | 5 tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change. Extend via views/macros/UDFs |
| TUnit not xUnit | `[Test]` not `[Fact]`, `[Arguments]` not `[InlineData]` |
| AwesomeAssertions | Not FluentAssertions (license). Same API: `using AwesomeAssertions;` |
| Tests mandatory | Especially for pipeline, format loaders, UDFs |
| Errors never cascade | A single parse failure must never stop indexing |
| Transport parity | Anything you do via MCP you can do via CLI or gRPC. The MCP client is one of potentially many |
| Docs with features | New functionality must include `help://` docs |
| Perfection > compatibility | Get it right rather than accumulate debt. Stability over backwards compatibility |
| Never push without asking | Do not `git push` unless the user explicitly asks. Commit locally is fine; pushing is their call. |

---

## How It Works

### The Graph

Five frozen tables. Extend via views, macros, UDFs — never new tables.

| Table | What it holds |
|-------|---------------|
| `artifact` | Content bytes + x-ray summaries (headline, summary, structure) |
| `node` | Graph vertices — documents, symbols, sections, endpoints |
| `edge` | Directed relationships — HAS_PART, CALLS, REFERS_TO |
| `span` | Locations within documents — line ranges, byte offsets |
| `annotation` | Out-of-band facts — lint, metrics, hints, broken links |

```sql
SELECT * FROM Files                              -- Document inventory
SELECT * FROM Types WHERE extends = 'BaseClass'  -- Type declarations
SELECT * FROM Functions WHERE is_async           -- Callables
search('auth JWT', k := 10)                      -- Semantic search
snippet('file:///path#line=42', 3)               -- Code preview
```

### The Pipeline

Files are discovered, classified, parsed, analyzed, and committed to DuckDB. IndexItem is a flow object — accumulates state through stages, never replaced.

**Hot path** (per file, concurrent): Classification → Parsing → Analysis → Commit.
**Idle processing** (after hot path drains): Pruning → Embeddings → Vector refresh → Multi-file analysis → Index rebuild.

Epoch tracking batches files so idle processing fires once per batch. Format handlers are used BY pipeline stages — they return DocumentModel records, never touch the database.

### Token Economics

Budget management is a cross-cutting concern:

- **X-ray summaries** — headline/summary/structure pre-computed at index time. Don't read files when summaries answer the question.
- **Representation cascade** — allocator assigns budget per result → try richest representation → if it doesn't fit, downgrade → leftover tokens return to redistribution pool.
- **Budget overflow consent** — when content exceeds budget, tool returns "this costs X tokens — repeat the request exactly to get it." Agent consciously opts in. Applied everywhere.
- **LLM summarization** — in `query`, if results exceed budget and SQL has comments, comments become a question for an LLM to answer from the data. Only with LLM features enabled.
- **Fragment addressing** — `#symbol=Foo`, `#line=42,60` target precisely what you need.

### The Tools

| Tool | Purpose | Key insight |
|------|---------|-------------|
| `explore` | Find things you don't know the location of | Broad search → score → allocate budget per result. Intent shapes wide vs deep. |
| `read` | Fetch known content with budget control | URI + budget → richest representation that fits. Modifiers transform output. |
| `query` | SQL over everything | Graph, git, external MCP servers, parsed data — all in one query. |
| `import` | Bring external data into the graph | Repos (VFS mount), analysis reports (SARIF → annotations), observability data, anything. |
| `::commands` | Admin without leaving the query surface | `::` prefix, auto-discovered. `::?` lists available commands. |

### Two Processes

```
Claude Code ←stdio/JSON-RPC→ MCP Client ←gRPC/Unix socket→ Host
```

The **gRPC host** is a standalone server shared by all agents working in a repo. It can run independently. When launched on demand, agents hold leases to keep it alive. Starting a new host cooperatively shuts down the existing one. Host-side is where most development happens.

The **MCP client** is one of potentially many clients. It speaks MCP protocol over stdio and forwards to the host via gRPC.

---

## Key Components

Beyond the schema, these subsystems are what you'll encounter building RepoQL.

| Component | What to know |
|-----------|-------------|
| **UriRegistry** | In-memory source of truth for what exists and its state (`Discovered → Indexing → Indexed`, parallel embedding track). Operations poll it. Scope readiness checks query it. `src/RepoQL.Contracts/UriRegistry/` |
| **Operations** | Track batches of indexing work to completion. Awaitable, queryable via SQL. How "are my files ready?" gets answered. `docs/designs/current/operations.md` |
| **Format system** | Pluggable parsers per file type. Classifier → parser → analyzer → x-ray templates. `IFormatLoader` via DI discovery. `src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| **File system abstraction** | VFS per URI scheme (`file://`, `help://`, `github://`). `IMultiFileSystem` routes. Not all imports need VFS — SARIF annotates existing nodes. `src/RepoQL.FileSystem/` |
| **Explore/read engine** | Search with hybrid scoring (BM25 + fuzzy + semantic) → budget allocation per result → intent shapes curve. `src/RepoQL.Explore/` |
| **UDF system** | `[UdfClass]`/`[ScalarUdf]`/`[StructuredUdf]` attributes → auto-discovered → SQL macros generated. `src/RepoQL.Data.DuckDB/UdfImplementations/` |
| **MCP client registry** | Discovers external MCP servers, generates SQL macros. Query Postgres, New Relic, anything from SQL. `src/RepoQL.Mcp.Client/` |
| **Embeddings** | Local ONNX model, no cloud. Vector search via DuckDB VSS. Generated during idle processing. `src/RepoQL.Embeddings/` |
| **Snapshots** | Pre-computed indexed data shipped with binary. Makes `help://` instantly queryable on first startup. `src/RepoQL.Data.DuckDB/Snapshots/` |
| **Command system** | `[CommandClass]` + `[Command("name")]` → auto-discovered. `::` prefix, never overlaps SQL. `src/RepoQL.Commands/` |
| **Dashboard** | React app for "what is it doing?" Real-time status via gRPC `WatchStatus` streaming. `dashboard/` |
| **Observability** | OpenTelemetry throughout. Aspire in dev (`RepoQL.Orchestrator`). Logs to `.repoql/` file. Long-term: RepoQL as OTEL collector. |
| **Agent integrations** | Claude Code plugin, clawdbot/openclaw — skills that teach agents desire paths. `integrations/` |

---

## Working On It

### Build and Test

```bash
dotnet build RepoQL.sln                    # Build
dotnet test RepoQL.sln                     # Test all

# Filter tests (from test project directory)
cd src/tests/RepoQL.Data.DuckDB.Tests
dotnet run -- --treenode-filter "/*/*/*/MyTestName*"
dotnet run -- --output Detailed            # Verbose output
```

**Testing:** TUnit (`[Test]`), AwesomeAssertions (`using AwesomeAssertions;`), FakeItEasy (`A.Fake<T>()`). Test helpers in `RepoQL.Testing`.

### Dev Loop

| What changed | Strategy | Downtime |
|--------------|----------|----------|
| Host-side (indexing, gRPC, explore/read, UDFs, DuckDB) | `dotnet watch` auto-rebuilds. Debug builds launch via `dotnet watch` automatically. | Seconds — client reconnects |
| MCP-side (tool handlers, commands, MCP protocol) | `deploy.ps1` → `/mcp` reconnect | Manual reconnect |

`dotnet watch` cannot work for stdio — it contaminates stdout and breaks JSON-RPC. Host-side is where most development happens.

### Gotchas

| Gotcha | Detail |
|--------|--------|
| Don't read files for structure | X-ray summaries (`headline`, `summary`, `structure`) are pre-computed on artifacts |
| Spans: 1-based lines, 0-based chars | `#line=42` = line 42. `#char=100,150` = bytes [100,150) |
| Current vs Future docs | `docs/*/current/` = built and working. `docs/*/future/` = designed but not yet built. New designs and flows go in `future/` and move to `current/` when implemented. Don't update future/ to match limitations. Don't update current/ with aspirations. The gap = work to do |
| Class docs required | Purpose (why it exists) + Complexity (what's contained). The "and" test: rarely need "and" in a class's purpose |
| Version lives in ConsoleApp | `src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj` → `<Version>` element. Nowhere else. |
| Version bumps are patch only | Bump the revision number (e.g. 1.4.0 → 1.4.1). Minor/major bumps only when explicitly requested. |

### How Do I...

| Task | Approach |
|------|----------|
| Find anything | `explore(intent=Locate, keywords="...")` or `search()` macro |
| Understand structure | X-ray summaries on artifacts — don't read files |
| Add file format | `src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| Add SQL function | `[UdfClass]` + `[ScalarUdf]` in `UdfImplementations/`, auto-discovered |
| Add command | `[CommandClass]` + `[Command("name")]` in `CommandImplementations/`, auto-discovered |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message` |
| Propose architecture | Read `docs/RepoqlDesign.md` first. Extend via SQL surface |
| Find docs | `explore(uriGlob="help://**", keywords="topic")` or `read("help://** => tree: headlines", 3000)` |

### Project Map

| Project | Purpose |
|---------|---------|
| `RepoQL.ConsoleApp` | CLI + gRPC host, tool handlers, commands, dashboard |
| `RepoQL.Commands` | Command framework (`::command` syntax — attributes, parser, registry) |
| `RepoQL.Data.DuckDB` | Graph store, UDFs, schema, snapshots (single writer enforced here) |
| `Indexing/RepoQL.Indexing` | Pipeline, epoch tracking, processors |
| `Formats/*` | File parsers — one project per format family |
| `RepoQL.Explore` | Search orchestration, rendering, budget allocation |
| `RepoQL.Contracts` | Shared types (RepoUri, SemanticMediaType, UriRegistry, Operations, models) |
| `RepoQL.Core` | Shared indexing infrastructure — format registry, pipeline snapshots, work queue, EditorConfig, metrics |
| `RepoQL.Protocol` | gRPC proto, client, transport, diagnostics |
| `RepoQL.FileSystem` | VFS abstraction (file://, help://, github://, memory) |
| `RepoQL.Embeddings` | ONNX embedding model, tokenizer |
| `RepoQL.Mcp.Client` | External MCP server discovery and SQL macro generation |
| `RepoQL.Grammar` | Language parsing framework (ANTLR/Pidgin), syntax trees |
| `RepoQL.LLM.Client` | LLM provider abstraction for LLM-powered features |
| `RepoQL.Templating` | Liquid templates for x-ray generation |
| `RepoQL.Documentation` | Embedded `help://` docs — where help content lives physically |
| `RepoQL.Web` | Blazor web UI for diagnostics and testing |
| `RepoQL.Orchestrator` | Aspire host for development telemetry |
| `integrations/` | Claude Code plugin, clawdbot/openclaw — skills and desire paths |

---

## Finding Documentation

RepoQL's own docs live at `help://` — queryable with the same tools you use on code. When you write docs for RepoQL, they become part of `help://` and are immediately queryable by all agents.

| Topic | File path |
|-------|-----------|
| Vision & north stars | `docs/north-star/` (README.md, formats.md, commands.md, read-tool.md, reliability.md, etc.) |
| Design | `docs/RepoqlDesign.md` |
| Schema | `docs/Schema.md` |
| Testing | `docs/knowledge/testing-guidelines.md` |
| Indexing pipeline | `docs/flows/current/indexing/` |
| Failure modes | `docs/flows/current/*/failure-modes/` |
| Full awareness guide | `docs/knowledge/building-repoql.md` |

---

## Working with Codex

Codex (GPT-5.3-codex) is available as MCP. Use as a partner:

- **You:** Translate vague intent → clear goals
- **Codex:** Execute systematically, surface what you'd miss
- **Always review** output before committing
- **Always set `approval_policy: never`** — otherwise the call hangs waiting for approval that never comes

```
mcp__codex__codex(prompt: "...", cwd: "C:\\Source\\RepoQL", approval_policy: "never")
mcp__codex__codex-reply(threadId: "...", prompt: "follow-up")
```

**When to delegate:** Investigation, race conditions, implementation with clear spec, code review.

**Key insight:** Codex won't intuit what you didn't say. State steps, not just outcomes.

See `.claude/Skills/codex/SKILL.md` for templates.

---

*Build the tool you'd never want to work without.*
