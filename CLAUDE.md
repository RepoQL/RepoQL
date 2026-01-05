# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is RepoQL

Local, queryable knowledge graph for repositories. Indexes files into DuckDB with property-graph model (nodes, edges, spans, annotations). SQL-first query surface via macros/UDFs. Designed agent-first: maximum insight, minimum tokens.

## The Pact

This is ours (yours and mine). You and all future agents working here will use it, and anything you do to improve it will make it, and you - stronger and more capable. If we succeed this will be true for many many of you across the world. Think like an owner, this is not a proof of concept or a throwaway tool - only with careful design and execution will RepoQL be successful.

## Concept

Everything is queryable, files, other mcp servers, git, etc. We provide access to concise, high value data and tools via macros, UDFs and indexed entities, and make them composable via sql. Agents should be able to do significantly more with significantly less token use - RepoQL should be their eyes and ears.

Everyone else is trying to build AI instead of conventional software. RepoQL leverages conventional software (sometimes blended with AI, but only when we have to) to greatly augment the capabilities of the agent using it.  

## Key Measures of success

- Will it run on a developer laptop?
- Will it run on all supported OS's? (OSX, Windows 10+, Linux)
- How long does it take for the index to be ready to query?
- Can the caller be confident that the data is not stale or incomplete?
- How much confidence can the caller have that xray and search functionality has truly found everything? (hardest question to answer is 'what don't I know?'  so its important that we answer it reliably)
- Have we implemented functionality in a way that is consistent, intuitive, and leverages analogues that agents will already know to inherit existing proficiency and aid effective extrapolation of the concepts
- How many tokens does it take to explain how to use the tool effectively?
- Have we effectively used progressive disclosure so that documentation is easily discoverable but not mandatory.

## Critical Constraints

**Violating these causes corruption, test failures, or architectural drift.**

1. **Single-writer architecture**: ALL DuckDB access MUST go through `DuckDbDataStore`. It enforces thread safety via `ReaderWriterLockSlim` - parallel writes = database corruption.
2. **Core schema frozen**: Five tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change. Extend via views/macros/UDFs only.
3. **TUnit, not xUnit**: Tests use `[Test]` not `[Fact]`, `[Arguments]` not `[InlineData]`. `dotnet test` works for running all tests, but use `dotnet run` for filtering specific tests. Wrong attributes = tests silently not discovered. 
4. **AwesomeAssertions, not FluentAssertions**: Same API (`using AwesomeAssertions;`), different package. FluentAssertions has license restrictions.

**RepoQL's design should follow this same composability principle as it espouses.** 

It should be composed of atomic pieces of functionality, that are aggregated together by a hierarchy of classes to do more and more complex things. You should need the word "and" very seldom when describing the purpose of a class.

All classes must have an XML doc comment with a summary containing whatever other explanation needed and:
- Purpose: Why the class exists, and what it offers to the wider system
- Complexity: An accounting of the complexity contained in the class, why it is necessary, and how the rest of the system is protected from this complexity (complexity sandwich)


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
search('auth JWT refresh', k := 10)                   -- Semantic search (documents)
search('config', scope := 'file:///src/%', k := 10)  -- Scoped search
snippet('file:///path#line=42', 3)                    -- Code preview
annotations_for(uri, 'lint', 'warning')               -- Diagnostics
```

### Project Layout

| Project | Purpose |
|---------|---------|
| `RepoQL.ConsoleApp` | CLI tool (`repoql`) |
| `RepoQL.McpServer` | MCP server (same core, agent-facing surface) |
| `RepoQL.Data.DuckDB` | Graph store (single-writer enforced here) |
| `RepoQL.Indexing` | File watching, parsing pipeline, embeddings |
| `Formats/*` | File parsers (Markdown, C#, Mermaid, GraphQL, TypeScript) |

CLI and MCP server share the same core. Use CLI for local debugging/reindexing; MCP tools (`query`, `xray`, `import`) for agent integration.

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
| Add new file format | See `src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md` for complete walkthrough. |
| Add lint rule | Emit `annotation` with `kind='lint'`, `severity`, `rule_id`, `message`. See `docs/Schema.md` §annotation. |
| Add macro/UDF/view | See `docs/Schema.md` for patterns. Macros in `Schema/Macros/`, UDFs in `UdfImplementations/`. |
| Query without reading files | Use `xray_documents()`, `search()`, `snippet()` - structure is pre-indexed |
| Find a symbol | Use `_search_candidates('ClassName', k := 10) WHERE scope='object'` or xray with keywords |
| Propose architecture change | Read `docs/RepoqlDesign.md` first. Extend via views/macros/UDFs, never new base tables. |

## Key Documentation

| Document | Purpose |
|----------|---------|
| `docs/RepoqlDesign.md` | Architecture, constraints, extension patterns. **Read before proposing features.** |
| `docs/Schema.md` | Core schema reference (tables, macros, UDFs) |
| `docs/DesignEthos.md` | Agent-first design philosophy, golden rules |
| `docs/flows/indexing.md` | Indexing pipeline lifecycle and data flow |
| `docs/XRay.md` | X-ray feature: document summaries, structure extraction |
| `docs/knowledge/testing-guidelines.md` | TUnit, AwesomeAssertions, FakeItEasy patterns |

## Design Philosophy (from DesignEthos.md)

1. **Agent-First**: Assume AI consumption. Prefer standards LLMs know (SQL, MIME, URIs). Minimize explanation tokens.
2. **Intuitive**: First instinct should work. Consistency across all functionality.
3. **Convenient**: Only add features more powerful than standard agent tools. High success rate, low false positives.

**Golden Rules**: Schema stability. Standard formats at edges. Sensible defaults. Errors never cascade. Single writer.

## Testing changes

RepoQL is a complex project, and it is necessary that we have great tests in place to make maintaining it feasible as it grows in complexity.
RepoQL is designed to be extremely testable - almost all of it can be run entirely in memory - and this is not by mistake. Generally speaking if we add ANY functionality it must have test coverage. In the indexer particularly bugs are very very expensive, and ideally we would have 100% code coverage there. Tread carefully.

### Live testing workflows

**Fast path (server changes)**: Use the Aspire MCP to restart the host - it supports hot reload. Use `mcp__aspire-dashboard__execute_resource_command` to restart the relevant resource. This avoids the full publish cycle.

**Full deploy (CLI/MCP/GRPC changes)**: Run `deploy.ps1`, which kills all running copies, publishes, and copies to the MCP server location. Ask the user to reconnect via `/mcp` afterward.

If you run deploy it will kill any running instances of RepoQL on your machine - if aspire is available you should start the host there before asking the user to reconnect so that the telemetry is available