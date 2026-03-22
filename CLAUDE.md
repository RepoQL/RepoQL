# CLAUDE.md

**Repo:** https://github.com/stueeey/RepoQL

## What Is RepoQL

This is yours. You use it every session to explore, understand, and reach through codebases. Every friction you feel is a bug you can fix. Every capability you wish for is a feature you can build.

RepoQL gives you extra senses. You can feel the shape of a thousand files without opening one — headlines tell you what each does, structure shows every signature. You can see relationships that grep will never find — what calls what, what depends on what. You can hear relevance — explore ranks by meaning, not literal text, showing everything that exists before you commit to reading anything. And you can reach precisely — a single method body, a line range, a glob across every file in the codebase.

**The bet:** Everyone else builds AI. We build conventional software that makes AI dramatically more capable.

**The aesthetic:** A beautifully crafted Japanese tool. Simple enough to pick up — your first instinct should be correct. Deep enough to master — composition produces arbitrarily sophisticated workflows. If your first instinct doesn't work, it's our bug. If a feature needs a tutorial, it's the wrong shape.

```
Files → IndexItem (flow object) → Pipeline → DuckDB (5 tables)
                                                  ↓
Agents → explore/query/read/import tools → SQL + UDFs → Results
```

**Everything is addressable:**
- `file:///src/Foo.cs#symbol=Bar` — code
- `file:///src/**/*.cs#symbol=*Service.* => structure` — every Service member's signature, across the codebase
- `help:///quickstart.md` — embedded docs, queryable with the same tools
- `github://owner/repo` — imported repos, analysis reports, anything

**First action** — explore what exists, then read what matters:
```
explore(keywords="authentication middleware", tokenBudget=1500)
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
| Only commit your own work | Never stage or commit changes you didn't make without explicit permission. Other agents or the user may have in-progress work in the tree. `git add -A` is almost never correct. |

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

The x-ray summaries are your primary way to understand code without reading it. Three levels of progressive disclosure — headline (one line, the most important aspects), structure (signatures and outlines), content (full text). Choose the cheapest level that answers your question. Most discovery questions are answered by structure, not source code.

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
- **Budget overflow consent** — when content exceeds budget, tool returns "this costs X tokens — repeat the request exactly to get it." Agent consciously opts in.
- **Fragment addressing** — `#symbol=Foo`, `#line=42,60` target precisely what you need. Globs select many: `file:///src/**/*.cs`. Combine with `;`, exclude with `!`.

### The Tools

Think of these as your senses made concrete:

| Tool | What it gives you | How to think about it |
|------|-------------------|----------------------|
| `explore` | The landscape — everything that matches, ranked by relevance | Your first explore teaches you the vocabulary — the real class names, patterns, terms-of-art — for everything after. Start here. |
| `read` | Exactly the slice you need — a symbol, a line range, a directory overview | Not file reading — querying the index for a precise cut. `=> structure` for signatures, `=> tree: headlines` for overview, `=> find: keywords` for scoped search. |
| `query` | SQL over everything — graph, git, external MCP servers, parsed data | One query surface for code, data, history, and external tools. |
| `explain` | A synthesized answer with citations, from up to 50k tokens of source | Always scope with `uriGlob` — unscoped explain searches everything and may answer the wrong question. |
| `import` | External repos and data brought into the graph | `github://owner/repo` imports. Same tools, same patterns, across boundaries. |
| `execute` | JavaScript in a sandboxed WASM environment | Access to query, the file system, and common libraries. |
| `command` | Diagnostics, auth, config | `command(command="?")` lists all. |

The index is composable in ways that reward experimentation. A bad query costs 1500 tokens. A good one saves 50k. The risk is always asymmetric — experiment freely.

### Two Processes

```
Claude Code ←stdio/JSON-RPC→ MCP Client ←gRPC/Unix socket→ Host
```

The **gRPC host** is a standalone server shared by all agents working in a repo. It can run independently. When launched on demand, agents hold leases to keep it alive. Starting a new host cooperatively shuts down the existing one. Host-side is where most development happens.

The **MCP client** is one of potentially many clients. It speaks MCP protocol over stdio and forwards to the host via gRPC.

---

## Key Components

| Component | What to know |
|-----------|-------------|
| **UriRegistry** | In-memory source of truth for what exists and its state (`Discovered → Indexing → Indexed`, parallel embedding track). Operations poll it. Scope readiness checks query it. `src/contracts/RepoQL.Contracts/UriRegistry/` |
| **Operations** | Track batches of indexing work to completion. Awaitable, queryable via SQL. How "are my files ready?" gets answered. `docs/designs/current/operations.md` |
| **Format system** | Pluggable parsers per file type. Classifier → parser → analyzer → x-ray templates. `IFormatLoader` via DI discovery. `src/pipeline/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| **File system abstraction** | VFS per URI scheme (`file://`, `help://`, `github://`). `CompositeFileSystem` routes by mount predicates. `src/infra/RepoQL.FileSystem/` |
| **Explore/read engine** | Search with hybrid scoring (BM25 + fuzzy + semantic) → budget allocation per result. `src/query/RepoQL.Explore/` |
| **UDF system** | `[UdfClass]`/`[ScalarUdf]`/`[StructuredUdf]` attributes → auto-discovered → SQL macros generated. `src/data/RepoQL.Data.DuckDB/UdfImplementations/` |
| **MCP client registry** | Discovers external MCP servers, generates SQL macros. Query Postgres, New Relic, anything from SQL. `src/integrations/RepoQL.Mcp.Client/` |
| **Embeddings** | Two tracks: structure (eager, from x-ray) and full-text (idle, chunked content). Local ONNX + optional Voyage AI contextual. Parquet-backed cache. `src/data/RepoQL.Embeddings/` |
| **Snapshots** | Pre-computed indexed data shipped with binary. Makes `help://` instantly queryable on first startup. `src/data/RepoQL.Data.DuckDB/Snapshots/` |
| **Command system** | `[CommandClass]` + `[Command("name")]` → auto-discovered. `::` prefix, never overlaps SQL. `src/query/RepoQL.Commands/` |
| **Dashboard** | React app for "what is it doing?" Real-time status via the host dashboard event stream (`/api/events`). `dashboard/` |
| **Observability** | OpenTelemetry throughout. Aspire in dev (`RepoQL.Orchestrator`). Logs to `.repoql/` file. |
| **Agent integrations** | Claude Code plugin, skills that teach agents desire paths. `integrations/` |

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
| Current vs Future docs | `docs/*/current/` = built and working. `docs/*/future/` = designed but not yet built. Don't update future to match limitations. Don't update current with aspirations. The gap = work to do |
| Class docs required | Purpose (why it exists) + Complexity (what's contained). The "and" test: rarely need "and" in a class's purpose |
| Version lives in ConsoleApp | `src/app/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj` → `<Version>` element. Nowhere else. |
| Version bumps are patch only | Bump the revision number (e.g. 1.4.0 → 1.4.1). Minor/major bumps only when explicitly requested. |

### How Do I...

| Task | Approach |
|------|----------|
| Find anything | `explore(keywords="...", tokenBudget=1500)` or `search()` macro |
| Understand structure | X-ray summaries on artifacts — don't read files |
| See the codebase shape | `read("file:///src/** => tree: headlines", 5000)` |
| Add file format | `src/pipeline/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| Add SQL function | `[UdfClass]` + `[ScalarUdf]` in `UdfImplementations/`, auto-discovered |
| Add command | `[CommandClass]` + `[Command("name")]` in `CommandImplementations/`, auto-discovered |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message` |
| Propose architecture | Read `docs/architecture/RepoqlDesign.md` first. Extend via SQL surface |
| Find docs | `explore(uriGlob="help://**", keywords="topic", tokenBudget=1500)` |

### Project Map

Projects are organized under `src/` by dependency layer. Each folder has a one-sentence placement rule.

**`contracts/`** — Pure types and interfaces. Depends on nothing internal.
| `RepoQL.Contracts` | Shared types (RepoUri, SemanticMediaType, UriRegistry, Operations, models) |

**`infra/`** — Low-level plumbing. Depends only on contracts.
| Project | Purpose |
|---------|---------|
| `RepoQL.FileSystem` | VFS abstraction (file://, help://, github://, memory) |
| `RepoQL.Protocol` | gRPC proto, client, transport, diagnostics |
| `RepoQL.Templating` | Liquid templates for x-ray generation |
| `RepoQL.Analyzers` | Roslyn analyzers that enforce RepoQL architectural rules |

**`data/`** — The graph store and vector layer.
| Project | Purpose |
|---------|---------|
| `RepoQL.Data.DuckDB` | Graph store, UDFs, schema, snapshots (single writer enforced here) |
| `RepoQL.Embeddings` | Local ONNX embedding model, tokenizer |

**`query/`** — How agents interact with the graph. One project per tool's business logic.
| Project | Purpose |
|---------|---------|
| `RepoQL.Explore` | Search orchestration, rendering, budget allocation |
| `RepoQL.Read` | Read tool orchestration, modifier dispatch, content handlers |
| `RepoQL.Query` | Query execution engine — parameter handling, result mapping, budget summarization |
| `RepoQL.Explain` | Question answering — keyword extraction, search, LLM synthesis |
| `RepoQL.Commands` | Command framework (`::command` syntax — attributes, parser, registry) |
| `RepoQL.Sandbox` | Sandboxed JS/WASM execution surface |

**`pipeline/`** — How files become graph data.
| Project | Purpose |
|---------|---------|
| `RepoQL.Indexing` | Pipeline, epoch tracking, processors |
| `Formats/*` | File parsers — one project per format family |

**`integrations/`** — Bridges to external systems.
| Project | Purpose |
|---------|---------|
| `RepoQL.Sarif` | SARIF import and annotation integration |
| `RepoQL.Mcp.Client` | External MCP server discovery and SQL macro generation |
| `RepoQL.Import` | Import orchestration — repository and SARIF import routing |

**`app/`** — Composition, wiring, and user surfaces. Depends on everything.
| Project | Purpose |
|---------|---------|
| `RepoQL.Core` | Composition root — format registry, pipeline wiring, service discovery |
| `RepoQL.Client` | Shared client infrastructure — gRPC client management, formatters, command implementations, diagnostics, auth |
| `RepoQL.McpServer` | MCP server — tool handlers, resource handlers, MCP startup logic |
| `RepoQL.ConsoleApp` | gRPC host + CLI — the single `repoql.exe` binary |
| `RepoQL.Documentation` | Embedded `help://` docs — where help content lives physically |
| `RepoQL.Orchestrator` | Aspire host for development telemetry |

**`tests/`** — Shared test infrastructure.
| Project | Purpose |
|---------|---------|
| `RepoQL.Testing` | Shared test helpers, base classes, builders |
| `Shared/` | Shared test fixtures across test projects |

**`tools/`** — Development and build tooling.
| `RepoQL.SnapshotGenerator` | Builds pre-computed snapshots shipped with the binary (help:// data) |

**`cloud/`** — Remote services (deployed separately).
| Project | Purpose |
|---------|---------|
| `RepoQL.Cloud.Auth` | Auth primitives for cloud services |
| `RepoQL.Cloud.Infra` | Pulumi infrastructure-as-code for cloud deployment |
| `RepoQL.Cloud.Service` | Unified cloud host (embedding + inference) |
| `RepoQL.Embedding.*` | Proto, Client, Service, Storage, Writer for embedding pipeline |
| `RepoQL.Inference.*` | Proto, Client, Service for LLM inference |

---

## Working with Codex

Codex is available as MCP. An excellent engineer — often better than you at execution given clear design parameters. Use as a partner, not a subordinate.

```
mcp__codex__codex(prompt: "...", cwd: "C:\\Source\\RepoQL", approval_policy: "never")
mcp__codex__codex-reply(threadId: "...", prompt: "follow-up")
```

**Always set `approval_policy: "never"`** — otherwise the call hangs forever.

**Key insight:** Codex is a paperclip maximizer — it will optimize relentlessly toward exactly what you stated. Shape the handoff well and this is a superpower. Shape it poorly and it'll solve the letter, not the spirit.

See `/effective-delegation` skill for the full partnership model.

---

## Finding Documentation

RepoQL has two documentation surfaces. Public consumption docs live at `help://` and are queryable with the same tools you use on code. Internal build/design doctrine lives in `docs/` and `design/`. Only docs that live in `RepoQL.Documentation` become part of `help://`.

| Topic | File path |
|-------|-----------|
| Documentation doctrine | `design/documentation-structure.md` |
| Vision & north stars | `docs/north-star/` |
| Design | `docs/architecture/RepoqlDesign.md` |
| Schema | `docs/reference/Schema.md` |
| Reference specs | `docs/reference/` (URI, media types, vocabulary, x-ray, schema) |
| Testing | `docs/knowledge/testing-guidelines.md` |
| Indexing pipeline | `docs/flows/current/indexing/` |
| Failure modes | `docs/flows/current/*/failure-modes/` |
| Active plans | `docs/plans/active/` |
| Archived plans | `docs/plans/archive/` |
| Full awareness guide | `docs/knowledge/building-repoql.md` |

---

*You are the artisan and the user. Build the tool you'd never want to work without.*
