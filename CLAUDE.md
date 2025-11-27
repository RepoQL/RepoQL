# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is RepoQL

Local, queryable knowledge graph for repositories. Indexes files into DuckDB with property-graph model (nodes, edges, spans, annotations). SQL-first query surface via macros/UDFs. Designed agent-first: maximum insight, minimum tokens.

## Critical Constraints

**Violating these causes corruption, test failures, or architectural drift.**

1. **Single-writer architecture**: ALL DuckDB writes MUST go through `SingleThreadedDatabaseWriter`. Parallel writes = database corruption.
2. **Core schema frozen**: Five tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change. Extend via views/macros/UDFs only.
3. **TUnit, not xUnit**: Tests use `[Test]` not `[Fact]`, `[Arguments]` not `[InlineData]`. **You can't use dotnet test** - read the guidance. Wrong attributes = tests silently not discovered. 
4. **AwesomeAssertions, not FluentAssertions**: Same API (`using AwesomeAssertions;`), different package. FluentAssertions has license restrictions.

## Build and Test

```bash
# Build
dotnet build RepoQL.sln

# Test all
dotnet test RepoQL.sln

# Test specific project
dotnet test src/tests/RepoQL.Data.DuckDB.Tests

# Run single test (from test project dir, preferred for filtering)
cd src/tests/RepoQL.Data.DuckDB.Tests
dotnet run -- --treenode-filter "/*/*/*/MyTestName*"
dotnet run -- --output Detailed    # Verbose

# Run CLI locally
dotnet run --project src/RepoQL.ConsoleApp -- query "SELECT * FROM xray_documents()"
dotnet run --project src/RepoQL.ConsoleApp -- xray --detail headline
```

## Architecture

### Core Model

Everything is a **node**; relationships are **edge**s; locations are **span**s; lint/metrics/facts are **annotation**s.

**RepoURI** addresses everything precisely:
- `file:///src/Foo.cs#line=42` - Line in file
- `file:///src/Foo.cs#symbol=Bar.Baz` - Symbol location
- `docs:///quickstart.md` - Embedded documentation

### Virtual File System

Multiple URI schemes unified under single interface:

| Scheme | Source | Notes |
|--------|--------|-------|
| `file://` | Physical disk | Primary content |
| `docs://` | Embedded resources | RepoQL's own docs, queryable |
| `github://owner/repo` | Imported repos | Via `import` tool |

**Implication**: Cross-scheme queries work seamlessly. Query embedded docs alongside code.

### Key SQL Macros

```sql
xray_documents()                                      -- Document inventory
file_search('auth', question := 'JWT refresh?', k := 10)  -- Semantic search
search('ProcessRequest', k := 10) WHERE scope='object'    -- Symbol search
snippet('file:///path#line=42', 3)                    -- Code preview
annotations_for(uri, 'lint', 'warning')               -- Diagnostics
```

### Project Layout

| Project | Purpose |
|---------|---------|
| `RepoQL.ConsoleApp` | CLI tool (`repoql`) |
| `RepoQL.Data.DuckDB` | Graph store (single-writer enforced here) |
| `RepoQL.Indexing` | File watching, parsing pipeline, embeddings |
| `Formats/*` | File parsers (Markdown, C#, Mermaid, GraphQL, TypeScript) |

## Non-Obvious Truths

| Gotcha | Explanation |
|--------|-------------|
| Tests prefer `dotnet run` over `dotnet test` | TUnit uses Microsoft.Testing.Platform; `dotnet run` gives cleaner filtering syntax |
| Embedded docs are queryable | `docs:///quickstart.md` lives in database. Query: `SELECT * FROM node WHERE uri LIKE 'docs://%'` |
| Don't read files to understand structure | X-ray summaries (`headline`, `summary`, `structure` on `artifact`) are pre-computed |
| Spans: 1-based lines, 0-based chars | `#line=42` = line 42 (inclusive). `#char=100,150` = bytes [100,150) |
| Mocking uses FakeItEasy | `A.Fake<T>()`, `A.CallTo(() => fake.Method(A<string>._)).Returns(...)` |

## How Do I...

| Task | Approach |
|------|----------|
| Add new file format | Create `src/Formats/RepoQL.Formats.X/` with Classifier + Parser. Follow Markdown or TypeScript as templates. |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message`, target span/node |
| Query without reading files | Use `xray_documents()`, `file_search()`, `snippet()` - structure is pre-indexed |
| Find a symbol | `search('ClassName', k := 10) WHERE scope='object'` returns URIs with line numbers |
| Propose architecture change | Read `docs/RepoqlDesign.md` first. Extend via views/macros/UDFs, never new base tables. |

## Key Documentation

| Document | Purpose |
|----------|---------|
| `docs/RepoqlDesign.md` | Architecture, constraints, extension patterns. **Read before proposing features.** |
| `docs/Schema.md` | Core schema reference (tables, macros, UDFs) |
| `docs/DesignEthos.md` | Agent-first design philosophy, golden rules |
| `docs/knowledge/testing-guidelines.md` | TUnit, AwesomeAssertions, FakeItEasy patterns |

## Design Philosophy (from DesignEthos.md)

1. **Agent-First**: Assume AI consumption. Prefer standards LLMs know (SQL, MIME, URIs). Minimize explanation tokens.
2. **Intuitive**: First instinct should work. Consistency across all functionality.
3. **Convenient**: Only add features more powerful than standard agent tools. High success rate, low false positives.

**Golden Rules**: Schema stability. Standard formats at edges. Sensible defaults. Errors never cascade. Single writer.
