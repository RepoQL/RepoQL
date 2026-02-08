# CLAUDE.md

## What Is RepoQL

Local, queryable knowledge graph for repositories. Files → DuckDB → SQL. Agents query structure without reading files.

**The bet:** Everyone else builds AI. We build conventional software that makes AI dramatically more capable.

**The aesthetic:** A beautifully crafted Japanese tool — simple, effective, intuitive, durable. LLM desire paths made manifest. If your first instinct doesn't work, it's our bug. If a feature needs a tutorial, it's the wrong shape.

```
Files → IndexItem (flow object) → Pipeline → DuckDB (5 tables)
                                                  ↓
Agents → explore/explain/query/read tools → SQL + UDFs → Results
```

**Everything is addressable:**
- `file:///src/Foo.cs#symbol=Bar` — code
- `help:///quickstart.md` — embedded docs (queryable)
- `github://owner/repo` — imports

**First action** — see the shape:
```
read("file:///src/** => tree: folders", 3000)
```

---

## Hard Constraints

**Violating these causes corruption, test failures, or architectural drift.**

| Constraint | Consequence | Rule |
|------------|-------------|------|
| Single writer | DB corruption | ALL DuckDB writes through `DuckDbDataStore` |
| Schema frozen | Architectural drift | 5 tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change. Extend via views/macros/UDFs |
| TUnit not xUnit | Tests silently don't run | Use `[Test]` not `[Fact]`, `[Arguments]` not `[InlineData]` |
| AwesomeAssertions | Compile errors | Not FluentAssertions (license). Same API: `using AwesomeAssertions;` |
| Tests mandatory | Bugs in indexing are expensive | Especially for pipeline, format loaders, UDFs |
| Errors never cascade | One bad file breaks trust | A single parse failure must never stop indexing |
| Perfection > compatibility | We're pre-1.0 | Get it right rather than accumulate debt |

---

## Gotchas

| Gotcha | Detail |
|--------|--------|
| Tests prefer `dotnet run` | TUnit uses Microsoft.Testing.Platform; `dotnet run -- --treenode-filter "..."` for filtering |
| Don't read files for structure | X-ray summaries (`headline`, `summary`, `structure`) are pre-computed on artifacts |
| Spans: 1-based lines, 0-based chars | `#line=42` = line 42. `#char=100,150` = bytes [100,150) |
| Mocking uses FakeItEasy | `A.Fake<T>()`, `A.CallTo(() => fake.Method(A<string>._)).Returns(...)` |
| Current vs Future docs | Don't update future/ to match limitations. Don't update current/ with aspirations. The gap = work to do |
| Class docs required | Purpose (why it exists) + Complexity (what's contained). The "and" test: rarely need "and" in a class's purpose |

---

## Finding Documentation

RepoQL's own docs live at `help://` — queryable with the same tools you use on code. This is the primary documentation surface.

```
explore(intent="Locate", uriGlob="help://**", keywords="your question", tokenBudget=2000)
read("help://** => tree: headlines", 3000)
```

**Key docs** (also available as files when `help://` is unavailable):

| Topic | help:// | File path |
|-------|---------|-----------|
| Vision | `help:///` | `docs/north-star/README.md` |
| Design | `help:///` | `docs/RepoqlDesign.md` |
| Schema | `help:///` | `docs/Schema.md` |
| Testing | `help:///` | `docs/knowledge/testing-guidelines.md` |
| Format vision | `help:///` | `docs/north-star/formats.md` |
| Indexing pipeline | `help:///` | `docs/flows/current/indexing/` |
| Failure modes | `help:///` | `docs/flows/current/*/failure-modes/` |

When you write docs for RepoQL, they become part of `help://` and are immediately queryable by all agents.

---

## Build and Test

```bash
dotnet build RepoQL.sln                    # Build
dotnet test RepoQL.sln                     # Test all

# Filter tests (from test project directory)
cd src/tests/RepoQL.Data.DuckDB.Tests
dotnet run -- --treenode-filter "/*/*/*/MyTestName*"
dotnet run -- --output Detailed            # Verbose output
```

**Live testing:**
- **Fast path (server changes):** Aspire MCP → restart host (hot reload)
- **Full deploy:** `deploy.ps1` → kills instances, publishes, copies. User reconnects via `/mcp`

---

## Architecture

### Core Tables (frozen)

| Table | Purpose |
|-------|---------|
| `artifact` | Content bytes + x-ray summaries (headline, summary, structure) |
| `node` | Graph vertices (documents, symbols, endpoints) |
| `edge` | Relationships (HAS_PART, CALLS, REFERS_TO) |
| `span` | Locations (line ranges, byte offsets) |
| `annotation` | Out-of-band facts (lint, metrics, hints) |

### Key Views and Macros

```sql
SELECT * FROM Files                              -- Document inventory
SELECT * FROM Types WHERE extends = 'BaseClass'  -- Type declarations
SELECT * FROM Functions WHERE is_async           -- Callables

search('auth JWT', k := 10)                      -- Semantic search
snippet('file:///path#line=42', 3)               -- Code preview
```

### Project Layout

| Project | Purpose |
|---------|---------|
| `RepoQL.ConsoleApp` | CLI + MCP host, tool handlers |
| `RepoQL.Data.DuckDB` | Graph store, UDFs (single writer enforced here) |
| `Indexing/RepoQL.Indexing` | Pipeline, file systems, processors |
| `Formats/*` | File parsers (C#, Markdown, GraphQL, etc.) |
| `RepoQL.Explore` | Search orchestration, rendering |
| `RepoQL.Contracts` | Shared types (RepoUri, SemanticMediaType, models) |

---

## How Do I...

| Task | Approach |
|------|----------|
| Find anything | `explore(intent=Locate, keywords="...")` or `search()` macro |
| Understand structure | X-ray summaries on artifacts — don't read files |
| Add file format | `src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| Add SQL function | `[UdfClass]` + `[UdfMethod]` in `UdfImplementations/`, auto-discovered |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message` |
| Propose architecture | Read `docs/RepoqlDesign.md` first. Extend via SQL surface |
| Find docs | `explore(uriGlob="help://**", keywords="topic")` |

---

## Working with Codex

Codex (GPT-5.2-codex) is available as MCP. Use as a partner:

- **You:** Translate vague intent → clear goals
- **Codex:** Execute systematically, surface what you'd miss
- **Always review** output before committing

```
mcp__codex__codex(prompt: "...", cwd: "C:\\Source\\RepoQL")
mcp__codex__codex-reply(threadId: "...", prompt: "follow-up")
```

**When to delegate:** Investigation, race conditions, implementation with clear spec, code review.

**Key insight:** Codex won't intuit what you didn't say. State steps, not just outcomes.

See `.claude/Skills/codex/SKILL.md` for templates.

---

RepoQL indexes repos into a graph database so agents query structure without reading files. Extend via SQL surface only. Single writer. Tests mandatory. Build the tool you'd never want to work without.
