# CLAUDE.md

Guidance for Claude Code working on RepoQL.

## 60-Second Orientation

**What is RepoQL?** Local, queryable knowledge graph for repositories. Files → DuckDB → SQL. Agents query structure without reading files.

**The mindset:** This is ours. You're building the tool that makes you (and all future Claudes) smarter. Think like an owner.

**The bet:** Everyone else builds AI. We build conventional software that makes AI dramatically more capable.

**The mental model:**
```
Files → IndexItem (flow object) → Pipeline → DuckDB (5 tables)
                                                  ↓
Agents → explore/query/read tools → SQL + UDFs → Results
```

**URI schemes** - everything is addressable:
- `file:///src/Foo.cs#symbol=Bar` - code
- `help:///quickstart.md` - embedded docs (queryable!)
- `github://owner/repo` - imports

**First action** - see the shape:
```
read("file:///src/** => tree: folders", 1000)
```

---

## Critical Constraints

**Violating these causes corruption, test failures, or architectural drift.**

| Constraint | Consequence | Rule |
|------------|-------------|------|
| Single writer | DB corruption | ALL DuckDB writes through `DuckDbDataStore` |
| Schema frozen | Architectural drift | 5 tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change. Extend via views/macros/UDFs |
| TUnit not xUnit | Tests silently don't run | Use `[Test]` not `[Fact]`, `[Arguments]` not `[InlineData]` |
| AwesomeAssertions | Compile errors | Not FluentAssertions (license). Same API: `using AwesomeAssertions;` |

---

## Gotchas

| Gotcha | Explanation |
|--------|-------------|
| Tests prefer `dotnet run` | TUnit uses Microsoft.Testing.Platform; `dotnet run -- --treenode-filter "..."` for filtering |
| Embedded docs are queryable | `help:///` lives in database: `SELECT * FROM Files WHERE uri LIKE 'help://%'` |
| Don't read files for structure | X-ray summaries (`headline`, `summary`, `structure`) are pre-computed on artifacts |
| Spans: 1-based lines, 0-based chars | `#line=42` = line 42. `#char=100,150` = bytes [100,150) |
| Mocking uses FakeItEasy | `A.Fake<T>()`, `A.CallTo(() => fake.Method(A<string>._)).Returns(...)` |
| Current vs Future docs | Don't update future/ to match limitations. Don't update current/ with aspirations. The gap = work to do |

---

## Golden Rules

- Schema stability - extend via SQL surface, never new tables
- Standard formats at edges - SQL, URIs, MIME types, SARIF
- Sensible defaults - must "just work" without config
- Errors never cascade - one bad file never breaks the system
- Single writer - all DB access through `DuckDbDataStore`
- Abstractions prove value - no layers "just in case"
- Perfection over backwards compatibility - we're pre-1.0, get it right

---

## Before You Code

1. **Use RepoQL to explore** - `explore(intent=Inventory, keywords="topic")` or read the tree
2. **Read the north-star** - `docs/north-star/` for what you're building toward
3. **Check extension patterns** - probably a view/macro/UDF, not new code
4. **Tests are mandatory** - especially in indexing (bugs are expensive)
5. **Class docs required** - Purpose (why it exists) + Complexity (what's contained, why, how sandwiched)

The "and" test: you should rarely need "and" when describing a class's purpose.

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
| Add file format | `src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md` |
| Add SQL function | `[UdfClass]` + `[UdfMethod]` in `UdfImplementations/`, auto-discovered |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message` |
| Find code | `explore(intent=Locate, keywords="...")` or `search()` macro |
| Understand structure | X-ray summaries on artifacts, don't read files |
| Propose architecture | Read `docs/RepoqlDesign.md` first. Extend via SQL surface |

---

## Key Documentation

| Document | When to read |
|----------|--------------|
| `docs/north-star/README.md` | Understanding the vision |
| `docs/RepoqlDesign.md` | Before proposing features |
| `docs/Schema.md` | Adding macros/UDFs/views |
| `docs/knowledge/testing-guidelines.md` | Writing tests |
| `docs/knowledge/format-excellence.md` | Adding file formats |
| `docs/flows/current/indexing/` | Understanding the pipeline |
| `docs/flows/current/*/failure-modes/` | Debugging issues |

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

## One-Sentence Summary

RepoQL indexes repos into a graph database so agents query structure without reading files; extend via SQL surface only; single writer; tests mandatory; think like an owner.
