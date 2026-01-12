---
description: Self-contained guide for LLMs contributing design ideas to RepoQL - architecture, philosophy, constraints, and design patterns
tags: ["design-contribution", "architecture-overview", "design-philosophy", "extension-patterns", "token-economics"]
audience: ["LLMs"]
categories: ["Documentation[100%]", "How-To[95%]"]
---

# Contributing Design Ideas to RepoQL

**For**: Intelligent agents proposing features, optimizations, or architectural improvements

**Purpose**: Enable meaningful design contributions by encoding RepoQL's architecture, constraints, philosophy, and extension patterns.

**Scope**: Design-level thinking, not implementation details.

---

## 🚨 Critical Constraints

**These define the design space. All proposals must respect them.**

1. **Single-writer architecture**: DuckDB connections support one writer. Parallel writes = corruption. This is architectural.
2. **Core schema stability**: Five tables (`artifact`, `node`, `edge`, `span`, `annotation`) are frozen. Extension via views/macros/UDFs only.
3. **Agent-first design**: If explanation exceeds 50 tokens, the design is likely wrong. LLMs must intuit usage from training data + minimal docs.
4. **Token economics**: Every design decision optimizes for LLM token budgets. Structure > bytes. Summaries > full content.
5. **Just works**: No complex configuration. Sensible defaults. Errors never cascade—one bad file doesn't block indexing.
6. **Query-first**: Information accessed via SQL, not specialized APIs. Policy as queries, not code.

---

## Core Value Proposition

### Capsule: RepoQLVision

**Invariant**
Query repository structure without reading files, saving 100x-1000x tokens.

**Example**
`SELECT * FROM Files` → 10,000 file summaries (100KB) vs reading raw files (500MB+)

**Depth**
- Distinction: Traditional tools require file reading; RepoQL pre-indexes structure
- Trade-off: Upfront indexing cost for massive query-time savings
- Why: Token budgets are precious; x-ray summaries + semantic search = efficient exploration

### What Problems Does RepoQL Solve?

1. **Token explosion**: Reading files to understand structure wastes tokens
2. **Search inadequacy**: Text search misses semantic intent; need hybrid (lexical + fuzzy + semantic)
3. **Format diversity**: Each file type needs custom tooling; need universal schema
4. **Incremental cost**: Re-indexing entire repo on change is expensive; need digest-based delta
5. **Agent friction**: Complex APIs require explanation; need intuitive SQL over standard schema

---

## System Architecture

### High-Level Model

```
Repository Files
    ↓
Indexing Pipeline (hot path: concurrent; writer: serial; idle: concurrent)
    ↓
Graph Database (DuckDB)
    ↓
Query Interface (SQL + MCP tools)
    ↓
Agent Consumption (xray/query/import)
```

### Capsule: FlowObject

**Invariant**
IndexItem accumulates state through stages rather than being transformed.

**Example**
Single object: `{uri, digest}` → `+ mediaType` → `+ structure` → `+ annotations` → committed

**Depth**
- Distinction: Functional pipelines transform immutably; flow objects mutate one object
- Trade-off: Mutable state requires care, but entire journey visible in one place
- Why: Debugging easier (inspect one object); processors see full context; testing simpler (check state at any point)
- SeeAlso: EventSourcing (alternative pattern we chose against)

### Capsule: EpochTracking

**Invariant**
Files arriving together share epoch number; when batch completes, post-processing runs once.

**Example**
Git pull: 20 files → epoch=42 → last file commits → prune + vector + multi-file analysis run once for batch

**Depth**
- Distinction: Per-file processing vs batch processing
- Trade-off: Adds latency (wait for batch) but 10x-100x efficiency (one DB round-trip, not N)
- Why: Vector embeddings, pruning, cross-file analysis are batch-native operations
- NotThis: Not message queue batching (synchronous within process, not distributed)

### Capsule: IdleDetection

**Invariant**
Event-driven idle detection triggers batch operations when hot path drains.

**Example**
Last item in epoch completes + all stage queues empty → `HotPathIdle(epoch)` event fires

**Depth**
- Distinction: Polling (periodic checks) vs event-driven (immediate)
- Trade-off: More complex (event subscription) but zero overhead and immediate response
- Why: Polling wastes CPU; events fire exactly when needed; testable via TaskCompletionSource

### Threading Model

| Component | Concurrency | Constraint | Why |
|-----------|-------------|------------|-----|
| Classification/Parsing | Parallel (N workers) | CPU-bound | Parse many files simultaneously |
| Database writer | **Serial (1 worker)** | **DuckDB write safety** | No concurrent writes possible |
| Idle processing | Parallel (N workers) | I/O-bound | Batch spawns many items |

**Critical insight**: Serial writer isn't a limitation—it's a design strength. No locks, deterministic order, simple reasoning.

---

## Schema Design Philosophy

### Core Principle: Everything Is a Graph

```
artifact → stores bytes + x-ray summaries
node → entities (documents, classes, functions, headings)
edge → relationships (composition trees, reference graphs)
span → precise locations (line/column/byte ranges)
annotation → facts (lint, metrics, outlines, traces)
```

### Capsule: RepoURI

**Invariant**
Universal locator addressing files, ranges, symbols, fragments. One format everywhere.

**Syntax**
```
file:///path/file.cs#symbol=Foo.Bar&line=12,20
file:///README.md#line=10,20
file:///api.yaml#/components/schemas/User
jar:file:///trace.zip!/entry#line=1
```

**Depth**
- Line: 1-based inclusive (line=10,20 means lines 10 through 20)
- Char: 0-based half-open (char=100,150 means bytes [100,150))
- Fragment precedence: JSON Pointer > params > line/char > anchor
- NotThis: Not a primary key (identity is `node.id` + digest)
- Why: Archives, JSON Pointers, ranges all addressed uniformly

### Capsule: SemanticMediaType

**Invariant**
Encode wire format (MIME) and semantic role (kind) in single string.

**Example**
```
text/markdown;kind=markdown.doc;charset=utf-8
application/json;kind=openapi.spec;version=3.0
text/x-csharp;kind=code.csharp
```

**Depth**
- Wire format: standard MIME type/subtype
- Semantic role: ;kind= parameter routes to parser
- Why: Single string answers "how to parse" and "what it represents"
- Normalized: lowercase, sorted params, deterministic

### Capsule: VirtualFileSystem

**Invariant**
Abstract content by URI scheme; multiple sources (disk, embedded, remote, archives) unified under single interface.

**Architecture**
```
IVirtualFileSystem (per scheme)
    ↓
IMultiFileSystem (composite router)
    ↓
RepoURI resolution
```

**Example Schemes**
- `file://` → PhysicalFileSystem (disk files)
- `docs://` → DocumentationFileSystem (embedded resources)
- `embed://` → DocumentationFileSystem (same content, alternate scheme)
- `github://` → GitHubFileSystem (imported repos via `import` tool)
- `jar:file:///` → ArchiveFileSystem (future: zip/tar contents)

**Why This Matters**

This abstraction enables RepoQL's most powerful capabilities:

1. **Universal addressing**: RepoURI works across any mounted filesystem
2. **Embedded documentation**: docs:// serves built-in guides without external files
3. **Import tool**: Mount external repos by adding new filesystem
4. **Archive support**: Index zip/jar contents as first-class citizens
5. **Testing**: MemoryFileSystem for fast in-memory tests
6. **Extensibility**: Add new schemes without core changes

**Interface Contract**

Every `IVirtualFileSystem` provides:
- `Scheme`: URI scheme it handles (e.g., "file", "docs", "github")
- `EnumerateAsync()`: List all resources lazily
- `GetFile(RepoUri)`: Resolve URI to file content
- `GetUri(IFileInfo)`: Convert file back to canonical RepoURI
- `Watch()`: Observe changes (new/modified/deleted)

**Depth**
- Distinction: Traditional tools assume filesystem = disk; RepoQL treats disk as one source among many
- Trade-off: Abstraction complexity for massive flexibility (embedded docs, archives, remote repos)
- Why: RepoURI universality requires scheme-based routing; enables features impossible with disk-only model
- NotThis: Not a virtual file system in OS sense (like FUSE); purely application-level abstraction
- SeeAlso: RepoURI (the addressing), Import tool (dynamic mounting)

**Composition Pattern**

```
CompositeFileSystem
├─ PrimaryMount: PhysicalFileSystem (file://)
│   └─ Indexes: /path/to/repo/**
├─ Mount: DocumentationFileSystem (docs://, embed://)
│   └─ Serves: Built-in guides, tutorials, references
└─ Mount: GitHubFileSystem (github://owner/repo)
    └─ Cached: Imported external repositories
```

**Change Propagation**

Each virtual filesystem emits change events:
- PhysicalFileSystem: Watches disk via FileSystemWatcher
- DocumentationFileSystem: Static (never changes)
- GitHubFileSystem: Polls or webhook-triggered updates
- CompositeFileSystem: Merges all watchers, fans-in events

**Result**: Single change stream for IndexingEngine, regardless of source.

**Design Implications**

1. **RepoURI is locator, not path**: Same URI format works across schemes
2. **Import = mount**: Adding `github://` source just mounts new filesystem
3. **Cross-filesystem queries**: SQL joins work across file:// and docs:// seamlessly
4. **Testability**: Swap PhysicalFileSystem for MemoryFileSystem in tests
5. **Future extensibility**: Add http://, s3://, git:// schemes trivially

**Real-World Example**

```sql
-- Query spans physical files AND embedded docs uniformly
SELECT uri, headline
FROM Files
WHERE headline LIKE '%authentication%'

-- Returns both:
-- file:///src/AuthService.cs | class AuthService
-- docs:///guides/authentication.md | Authentication Guide
```

**Agent Mental Model**: "RepoQL indexes content, not just disk files. Any URI scheme can be a source."

### Extension Philosophy

**Rule**: Extend via views/macros/UDFs, never new base tables.

**Why**:
- Core schema stability enables tooling
- Views compose naturally via SQL
- Format-specific tables fragment the model
- Annotations unify diagnostics/metrics/outlines

**Pattern**:
```sql
-- ✅ Good: View projects core schema
CREATE VIEW csharp_types AS
SELECT n.id, n.properties->>'qualified_name' AS name, ...
FROM node n WHERE n.kind = 'csharp.type'

-- ❌ Bad: New table fragments model
CREATE TABLE csharp_types_custom (...)
```

---

## Design Philosophy (DesignEthos.md)

### Agent-First Principles

1. **Leverage training data**: Use standards LLMs already know (DuckDB SQL, MIME, URIs, JSON)
2. **Minimal explanation**: Name things so usage is intuitive; avoid requiring tutorials
3. **Discoverable**: If agents won't find it naturally, rethink the design
4. **Consistent**: Understanding one part should translate to understanding others
5. **Single-sentence concepts**: Each capability describable in one sentence

**Test**: Could an LLM with no RepoQL-specific training infer correct usage from function names alone?

### Token Economics

**Principle**: Every token must earn its place through unique information value.

**Why CamelCase?**
```
"CircuitBreaker" → 1-2 tokens (single semantic unit)
"circuit-breaker" → 3-4 tokens (hyphen splits)
"circuit breaker pattern" → 5-6 tokens

3x token efficiency matters at scale
```

**Why X-ray Summaries?**
```
Read 10K files: 500MB text, 1M+ tokens
Files view: 100KB summaries, 20K tokens

50x compression, instant queries
```

**Why Structure Over Bytes?**
```
Question: "What classes are in this file?"
Traditional: Read file (1000 tokens) → parse → extract → answer
RepoQL: Query view (10 tokens) → answer

100x efficiency
```

### Convenience Through Depth

Features must justify cognitive cost:

| Question | Evaluation Criteria |
|----------|---------------------|
| Understanding needed? | Implicit (happens automatically) > Explicit (must invoke) |
| Explanation tokens? | <10 tokens = good; <50 = acceptable; >50 = reconsider |
| Token savings? | Must save >10x what explanation costs |
| New capability? | Must enable tasks impossible before |
| Success rate? | >95% accuracy; <5% false positives (rework is expensive) |

**Example evaluation**:
- Feature: Semantic search
- Tokens to explain: ~30 ("BM25 + fuzzy + embeddings, query-adaptive weights")
- Tokens saved per use: 100-1000+ (find relevant code without reading files)
- New capability: Intent-based code discovery
- Verdict: ✅ High value, acceptable cost

---

## How Agents Consume RepoQL

### Three MCP Tools

#### 1. `query` - Direct SQL Access

```sql
-- Inventory: What exists?
SELECT * FROM Files WHERE lang = 'csharp'

-- Search: Find relevant code
SELECT uri, symbol, score FROM search('authentication', k := 10)

-- Structure: Understand without reading
SELECT t.qualified_name, m.name
FROM csharp_types t
JOIN csharp_members m ON m.declaring_type_id = t.type_id
WHERE t.document_uri = 'file:///UserService.cs'
```

#### 2. `xray` - The Primary Discovery Tool

**This is the main interface for 75% of repository exploration.**

**Capsule: XrayTool**

**Invariant**
Scan repository structure efficiently through pre-indexed summaries and linting, with multi-axis filtering and progressive detail levels.

**Why**: Combines glob patterns, semantic search, media type filtering, and progressive disclosure in single ergonomic interface. Avoids requiring SQL knowledge while remaining extremely flexible.

**Core insight**: Most exploration follows pattern "find files → understand structure → read specific parts". Xray handles all three with minimal cognitive load.

##### Progressive Detail Levels

Match information depth to need—massive token efficiency:

| Level | Capacity | Use Case | Output | Tokens/File |
|-------|----------|----------|--------|-------------|
| **headline** | 1000 files | Scan at scale | One-line: name + symbols + lint badges | ~10-20 |
| **summary** | 100 files | Understand structure | Outline (classes/methods) + key features + lint | ~50-100 |
| **snippet** | 10 files | Read code | Full source with context + inline diagnostics | ~200-2000 |

**Example outputs**:
```
# headline
UserService.cs | class UserService, interface IUserRepository (+3 more)
[ ⚠️ 2 | ❌ 1 ] AuthController.cs | class AuthController (+5 methods)

# summary
UserService.cs | class UserService, interface IUserRepository (+3 more)
namespace Services
  public class UserService : IUserRepository
    public method GetUserAsync(id)
    public method CreateUserAsync(data)
    public method UpdateUserAsync(id, data)
::error file=UserService.cs,line=42::Possible null reference

# snippet
UserService.cs
```csharp
 40:     public async Task<User> GetUserAsync(string id)
 41:     {
>42:         return await _repository.FindAsync(id); // ⚠️ Possible null reference
 43:     }
```
::error file=UserService.cs,line=42::Possible null reference
```

##### Multi-Axis Filtering

Compose filters to narrow scope before detail level applies:

**1. Glob patterns** (file matching):
```
pattern="**/*.cs"              # All C# files
pattern="**/Services/*.cs"     # Services directory
pattern="**/*Test*.cs"         # Test files anywhere
```

**2. Media type filtering** (semantic type):
```
type="*csharp*"               # Any C# content
type="*markdown.doc*"         # Markdown documents
type="*openapi*"              # OpenAPI specs
```

**3. Semantic search** (find relevant files):
```
keywords="authentication"                    # Lexical search
question="How do JWT tokens work?"          # Semantic search
keywords="auth" + question="token refresh"  # Combined
```

**4. Result limiting**:
```
limit=50    # Override default for detail level
```

##### Power Patterns

**Pattern 1: Broad to Narrow Discovery**
```
# Step 1: What exists? (scan 1000s)
xray(pattern="**/*.cs", detail="headline")

# Step 2: Understand relevant subset (100s)
xray(pattern="**/Auth*.cs", detail="summary")

# Step 3: Read specific implementations (10s)
xray(pattern="**/AuthService.cs", detail="snippet")
```

**Pattern 2: Semantic Discovery**
```
# Find relevant files semantically (no need to know structure)
xray(
  pattern="**/*.md",
  question="How do users authenticate?",
  detail="snippet",
  limit=15
)
# Returns 15 most relevant documentation snippets
```

**Pattern 3: Object-Level Targeting**
```
# First: Find the symbol
query: SELECT uri, symbol FROM search('ProcessRequest', k := 10) WHERE scope = 'object'
# Returns: file:///Handler.cs#symbol=ProcessRequest&line=42,67

# Then: Get just that method
xray(
  pattern="file:///Handler.cs#symbol=ProcessRequest",
  detail="snippet"
)
# Shows only the ProcessRequest method with context
```

**Pattern 4: Lint-Focused Exploration**
```
# Headline shows lint badges - scan for problems
xray(pattern="**/*.cs", detail="headline")
# See: [ ⚠️ 5 | ❌ 2 ] PaymentProcessor.cs

# Get details on problematic files
xray(pattern="**/PaymentProcessor.cs", detail="summary")
# Inline diagnostics show exact issues
```

**Pattern 5: Type-Constrained Discovery**
```
# Explore specific file types
xray(type="*graphql*", detail="summary")           # All GraphQL schemas
xray(type="*config*", question="Redis", detail="snippet")  # Config mentioning Redis
```

##### Token Efficiency Breakdown

**Traditional approach** (read files):
```
Task: "Find authentication code"
1. List files → 50 files match "auth"
2. Read all 50 → 250,000 tokens
3. Analyze to find relevant 5 → wasted 95% of tokens
```

**Xray approach**:
```
Task: "Find authentication code"
1. xray(keywords="auth", question="token validation", detail="headline", limit=50)
   → 50 files, 500 tokens (one-liners)
2. Identify 10 relevant → xray with detail="summary"
   → 10 files, 1,000 tokens (outlines)
3. Deep dive on 3 → xray with detail="snippet"
   → 3 files, 2,000 tokens (full code)

Total: 3,500 tokens (98.6% savings)
```

**Why this works**:
- Pre-indexed summaries avoid reading
- Progressive disclosure matches information need
- Semantic search finds relevant files first
- Glob patterns scope appropriately
- Lint badges surface problems immediately

##### Design Brilliance

**Ergonomic wins**:
1. **No SQL required**: Glob patterns + natural language questions
2. **Composable filters**: Pattern + type + semantic + limit
3. **Smart defaults**: Detail level determines result count automatically
4. **Unified output**: Consistent format across all detail levels
5. **Fragment support**: Works with line ranges and symbols from search results

**Cognitive load reduction**:
- Don't need to know schema (query does)
- Don't need to construct joins (automatic)
- Don't need to remember view names (pattern matching)
- Don't need to understand spans (handled automatically)

**Token optimization**:
- Three-tier disclosure prevents over-reading
- Lint badges in headline (visual triage)
- Summary shows structure without full source
- Snippet includes only relevant context (not entire file)

##### When to Use Query Instead

**Xray handles**: 75% of needs
- File discovery (glob + semantic)
- Structure understanding (progressive detail)
- Code reading (snippet with context)
- Problem identification (lint badges)
- Documentation exploration

**Query handles**: 25% of needs (complex analysis)
- Multi-table joins (combining types with members)
- Aggregations (count methods per file)
- Graph traversal (find all callers)
- Custom filtering (complex WHERE clauses)
- Cross-file analysis (references, implementations)

**Rule of thumb**: Start with xray. Move to query when you need SQL's power (joins, aggregations, complex predicates).

##### Xray + Query Composition

```
# Xray: Find relevant files quickly
xray(question="JWT validation", detail="headline", limit=10)
→ Identifies: AuthService.cs, TokenValidator.cs, JwtMiddleware.cs

# Query: Deep structural analysis
SELECT t.qualified_name,
       COUNT(m.id) as method_count,
       AVG(JSON_ARRAY_LENGTH(m.parameters)) as avg_params
FROM csharp_types t
JOIN csharp_members m ON m.declaring_type_id = t.type_id
WHERE t.document_uri IN (
  'file:///AuthService.cs',
  'file:///TokenValidator.cs',
  'file:///JwtMiddleware.cs'
)
GROUP BY t.qualified_name

# Xray: Read implementations
xray(pattern="**/AuthService.cs#symbol=ValidateToken", detail="snippet")
```

#### 3. `import` - External Repositories

**Capsule: ImportTool**

**Invariant**
Mount external repositories as new virtual filesystems, making them queryable alongside local files.

**Example**
```
import(uri="github://owner/repo@main")
```

**What Happens**:
1. Downloads/clones repository to local cache
2. Creates new `IVirtualFileSystem` with `github://` scheme
3. Mounts filesystem into `CompositeFileSystem`
4. Triggers indexing of all files under `github://owner/repo`
5. Files immediately queryable: `xray(pattern="github://owner/repo/**/*.cs")`

**Design Insight**: Import isn't a special operation—it's just mounting a new `IVirtualFileSystem`. The abstraction makes this trivial.

**Use Cases**:
- Compare local code against external library
- Search across multiple repos simultaneously
- Reference external documentation
- Analyze dependencies in context

**Cross-Repo Queries**:
```sql
-- Find authentication patterns across local + imported repos
SELECT uri, headline
FROM Files
WHERE headline LIKE '%authentication%'
  AND (uri LIKE 'file:///%' OR uri LIKE 'github://%')
```

**Agent Mental Model**: "Import adds another source to search—same tools, more content."

### Search Design

**Capsule: HybridSearch**

**Invariant**
Combine lexical (BM25), fuzzy (subsequence), and semantic (embeddings) with query-adaptive routing.

**Scoring Components**:
- **BM25**: Symbol exact match (4.0), substring (3.2), basename (3.0)
- **Fuzzy**: Subsequence matching via edit distance
- **Semantic**: Cosine similarity of embeddings (ONNX-based, local)

**Routing Logic**:
| Query Pattern | Example | Weight Distribution |
|---------------|---------|---------------------|
| Symbol-like | `Foo::Bar`, `ProcessRequest` | 90% lexical, 10% semantic |
| Natural language | "how do JWTs work" | 20% lexical, 80% semantic |
| Empty query | "" | 70% semantic (recency) |
| Heavy (>160 chars) | Long question | 120% semantic (boosted) |

**Default**: 45% BM25, 35% fuzzy, 20% semantic

**Why This Design**:
- Symbol queries need exact match priority
- Natural language needs semantic understanding
- Query analysis happens automatically (no explicit mode selection)
- Weights are query-adaptive, not fixed

---

## Extension Patterns

### Format Support Pattern

**Concept**: Add new file type support through four components:

1. **Classifier**: Maps file → SemanticMediaType
2. **Parser**: Reads content → Nodes + Edges + Spans
3. **View**: Projects nodes → Domain-specific schema
4. **Analyzer** (optional): Emits annotations (lint/metrics/outline)

**Example: Supporting GraphQL**
- Classifier: `.graphql` → `text/graphql;kind=graphql.schema`
- Parser: Extract types, fields, directives → nodes with spans
- View: `graphql_types`, `graphql_fields` project from nodes
- Analyzer: Validate schema, emit deprecation warnings

**Design principles**:
- Emit core schema (nodes/edges), not custom tables
- Build composition tree first (document → items)
- Add reference edges second (field → type, etc.)
- Use annotations for diagnostics

### Query Capability Pattern

**Concept**: Add new query surface via table macros or UDFs.

**Table macros** (return rows):
```sql
CREATE OR REPLACE MACRO my_search(pattern, k) AS TABLE (
  WITH ... SELECT ... -- Complex query logic
)
```

**Scalar UDFs** (return values):
```sql
CREATE OR REPLACE FUNCTION my_helper(input VARCHAR) AS ...
```

**When to use each**:
- Macro: Complex query composers (search, aggregations, joins)
- UDF: Data transformers (parse URIs, format strings, compute scores)

### Analysis Pattern

**Concept**: Emit annotations for any findings (lint, metrics, outlines, traces).

**Annotation structure**:
```
kind: "lint" | "metric" | "outline" | "trace" | "change" | ...
severity: "hint" | "info" | "warning" | "error"
source: "analyzer-name"
message: Human-readable description
data: JSON with structured details
target: node_id | edge_id | span_id | uri
```

**Why annotations vs tables**:
- Uniform interface for all findings
- SQL-based policy gates (no special APIs)
- Standard export (SARIF, GitHub Actions)
- Easy to add new kinds

**Example use cases**:
- Lint: Deprecation warnings, style violations
- Metrics: Complexity scores, coverage percentages
- Outlines: Document structure summaries
- Traces: Performance spans, dependency graphs
- Changes: Git blame, authorship

---

## Design Patterns

### Pattern: Composition vs Reference

**Concept**: Edges have two modes with different semantics.

**Composition** (`is_composition=true`):
- Forms trees (single parent per node)
- Represents ownership/containment
- Natural file order via `ordinal`
- Example: document → class → method

**Reference** (`is_composition=false`):
- Forms graphs (multiple edges allowed)
- Represents relationships/dependencies
- Can be cyclic, cross-document
- Example: method → calls → method, class → implements → interface

**Design rule**: Build composition tree first (enables x-ray), add references second (enables analysis).

### Pattern: Incremental Indexing

**Concept**: Digest-based change detection skips unchanged files.

**Three states**:
1. **SkipUpToDate**: Digest matches → no work
2. **Reindex**: Digest differs → full pipeline
3. **Unknown**: New file → full pipeline

**Additional optimization**:
- Pending digest tracking prevents duplicate work (file queued twice with same digest)
- Catalog hydrated from database on startup
- Updates only on commit (never before)

**Why**: 10K file repo with 10 changes → index 10, not 10K.

### Pattern: Batch Operations

**Concept**: Expensive operations deferred until epoch completes, run once per batch.

**Batched operations**:
1. **Pruning**: Find deleted files (compare catalog to pending)
2. **Vector refresh**: Compute embeddings (batch inference)
3. **Multi-file analysis**: Cross-reference resolution (graph complete)
4. **Index rebuild**: Secondary indexes (batch updates)

**Why**: N files with individual ops = N round-trips; batch = 1 round-trip.

### Pattern: X-ray Summaries

**Concept**: Pre-compute three levels of file understanding.

**Three fields** (independent, compose as needed):
- **headline** (1 line): Essential identity (file name, types, symbol count)
- **summary** (~5 lines): Key information (outline, technologies, metrics)
- **structure** (~15 lines): Detailed outline (full hierarchy, signatures)

**Why independent**: Agent composes based on need (not "summary includes headline").

**Token efficiency**:
```
headline: 10-20 tokens
summary: 50-100 tokens
structure: 150-250 tokens
full file: 1000-10000+ tokens

10x-100x savings for understanding
```

---

## Design Anti-Patterns

### Anti-Pattern: New Tables for Features

**Problem**: Core schema fragments, tooling breaks, queries don't compose.

**Instead**: Views project core schema with domain names.

**Example**:
```sql
-- ❌ Bad: New table
CREATE TABLE openapi_operations (id, method, path, ...)

-- ✅ Good: View over core schema
CREATE VIEW openapi_operations AS
SELECT n.id, n.properties->>'method' AS method, ...
FROM node n WHERE n.kind = 'openapi.operation'
```

**Why**: Views compose via joins; tables require special handling.

### Anti-Pattern: API Proliferation

**Problem**: Each feature needs custom API; agents must learn many interfaces.

**Instead**: SQL as universal interface; query macros for complex operations.

**Example**:
```sql
-- ✅ Good: Query-based
SELECT * FROM annotations WHERE severity = 'error'

-- ❌ Bad: Custom API
POST /api/diagnostics/errors
```

**Why**: LLMs already understand SQL; no per-feature API docs needed.

### Anti-Pattern: Path as Identity

**Problem**: Files move/rename; paths not stable across repos; archives have multiple paths.

**Instead**: Content-based identity (digest) + node IDs.

**Why**: URIs are locators, not identifiers; identity must be stable.

### Anti-Pattern: Whole-File Reads in UX

**Problem**: Wastes tokens when structure query would suffice.

**Instead**: `Files` view + `snippet()` + format-specific views.

**Example**:
```sql
-- ❌ Bad: Read entire file
Read "UserService.cs" → 5000 tokens

-- ✅ Good: Query structure
SELECT t.qualified_name, m.name FROM csharp_types t ...
→ 50 tokens
```

### Anti-Pattern: Tutorial Chattiness

**Problem**: Documentation wastes tokens on preambles, transitions, motivations.

**Instead**: Capsule format (Invariant → Example → Depth).

**Example**:
```
❌ Bad: "Now that we understand indexing, let's explore how search works..."
✅ Good: "Capsule: HybridSearch | Invariant: Combine BM25 + fuzzy + semantic..."

Saved: 15 tokens per concept × 100 concepts = 1500 tokens
```

### Anti-Pattern: Configuration Complexity

**Problem**: Requires explanation; agents must learn settings.

**Instead**: Sensible defaults; auto-detection; convention over configuration.

**Example**:
```
❌ Bad: Require config for file type detection
✅ Good: Provisional media type from extension + content sniffing

Agent mental model: "It just works"
```

---

## Performance Characteristics

### Bottlenecks (By Design)

| Component | Limit | Why | Mitigation |
|-----------|-------|-----|------------|
| Writer throughput | Serial | DuckDB write safety | Hot path saturates it (good—CPU bound, not I/O) |
| Embedding batch | 128-160 | DirectML/CoreML stability | Batch efficiently, GPU acceleration |
| Vector search | O(n) scan | No HNSW (incremental complexity) | DuckDB fast enough; consider future |
| Cold start | Minutes (100K files) | Full index build | Incremental updates are fast (<10s) |

### Scalability Profile

| Repo Size | Cold Start | Incremental Update | Query Latency |
|-----------|------------|-------------------|---------------|
| 1K files | ~5s | <1s | <100ms |
| 10K files | ~30s | <5s | <200ms |
| 100K files | ~5min | <10s | <500ms |

**Limiting factor**: Embedding computation (GPU helps 10x)
**Not limiting**: Query performance (DuckDB very fast)

### Trade-off Analysis

| Design Choice | Benefit | Cost | Justification |
|---------------|---------|------|---------------|
| Single writer | Simple, deterministic, no locks | Serial bottleneck | Hot path CPU-bound anyway |
| No HNSW index | Incremental updates simple | Linear scan search | DuckDB fast enough; HNSW adds complexity |
| Flow object | Easy debug, full context | Mutable state | Debugging value > immutability purity |
| Epoch batching | 10x-100x batch efficiency | Latency for completion | Efficiency gain worth wait |
| X-ray pre-compute | 100x-1000x query efficiency | Upfront index cost | Pays for itself immediately |

---

## Evaluation Framework

### Evaluating New Features

**Questions to answer**:

1. **Value**: What tokens saved or capability enabled?
2. **Cost**: How many tokens to explain? How much complexity?
3. **Fit**: Extends via views/macros (Green), or requires core changes (Red)?
4. **Intuitiveness**: Can agent infer usage from name + training data?
5. **Consistency**: Does it follow existing patterns?
6. **Success rate**: >95% accuracy? <5% false positives?

**Scoring rubric**:
- Token savings: >100x = excellent; >10x = good; <10x = reconsider
- Explanation cost: <10 tokens = excellent; <50 = acceptable; >50 = reconsider
- Extension tier: Green = good; Yellow = caution; Red = requires strong justification
- Intuitiveness: Zero explanation needed = excellent; examples sufficient = good; tutorial needed = reconsider

**Example: Semantic search**
- Value: 100x token savings (find without reading)
- Cost: ~30 tokens ("BM25 + fuzzy + embeddings")
- Fit: Green (implemented as SQL macro)
- Intuitiveness: "search(query)" intuitive from SQL training
- Consistency: Follows `file_search()` pattern
- Success rate: >90% relevant results
- **Verdict**: ✅ Excellent feature

**Example: Custom file type table**
- Value: Domain-specific queries easier
- Cost: ~20 tokens ("use xyz_table instead of views")
- Fit: Red (new base table)
- Intuitiveness: Breaks universal schema model
- Consistency: Violates "views not tables" principle
- Success rate: N/A
- **Verdict**: ❌ Rejected—use view instead

### Design Quality Checklist

Before proposing a design:

- [ ] **Agent-first**: Explainable in <50 tokens? Intuitive from training data?
- [ ] **Token-efficient**: Saves >10x what explanation costs?
- [ ] **Extends cleanly**: Uses views/macros/UDFs, not new base tables?
- [ ] **Just works**: Sensible defaults? No configuration required?
- [ ] **High reliability**: >95% success rate? <5% false positives?
- [ ] **Consistent**: Follows existing patterns? Reuses concepts?
- [ ] **Isolated errors**: One bad input never breaks system?
- [ ] **Query-first**: Accessed via SQL, not special API?

If any answer is "no", reconsider or justify why exception warranted.

---

## Common Design Questions

### Q: Should this be a new table or a view?

**Answer**: Almost always a view.

**Rule**: If it can be computed from core schema, it's a view.

**Exception**: Only add table if data can't be derived from existing tables AND has independent lifecycle.

**Test**: Could this be queried via `SELECT ... FROM node/edge WHERE ...`? If yes → view.

### Q: Should this be a macro or a UDF?

**Answer**:
- Returns rows (table-valued) → Macro
- Returns single value (scalar) → UDF
- Complex multi-step query logic → Macro
- Simple data transformation → UDF

### Q: Where should this data live?

**Decision tree**:
1. Structural (classes, functions) → `node` + `edge`
2. Findings (lint, metrics) → `annotation`
3. Content (file bytes) → `artifact`
4. Location (ranges) → `span`
5. Derived (aggregations) → View (don't store)

### Q: How should this feature be discovered?

**Options**:
1. **Intuitive naming**: Feature name implies usage (best)
2. **Pattern reuse**: Similar to existing feature (good)
3. **Documentation reference**: Mentioned in quickstart (acceptable)
4. **Tutorial required**: Multi-step explanation (reconsider)

**Target**: Level 1 or 2. Avoid level 4.

### Q: Should this run in hot path or idle processing?

**Hot path**: Per-file operations, required for basic indexing
**Idle**: Batch operations, cross-file analysis, optimization

**Rule**: If operation needs complete set of files or benefits from batching → idle.

### Q: How should errors be handled?

**Principle**: Isolate errors to individual files; never cascade.

**Pattern**: Catch all exceptions → log → emit error annotation → continue processing.

**Anti-pattern**: Unhandled exception breaks entire pipeline.

---

## Evolution Strategy

### How RepoQL Grows

**Tier 1: Format support**
- Add classifier (file type detection)
- Add parser (structure extraction)
- Add view (domain-specific queries)
- Add analyzer (optional diagnostics)

**Tier 2: Query capabilities**
- Add macros (complex query composers)
- Add UDFs (data transformers)
- Optimize routing (query analysis)

**Tier 3: Analysis depth**
- Add analyzers (new annotation kinds)
- Add cross-file analysis (reference resolution)
- Add metrics (quality gates)

**Never**: New base tables, breaking changes to interfaces, multiple writers.

### Proposal Template

When suggesting a feature:

```markdown
## Feature: [Name]

### Problem
[What capability is missing or inefficient?]

### Proposal
[Design at conceptual level]

### Value
- Token savings: [X]x
- New capability: [Yes/No - describe]
- Use cases: [3-5 examples]

### Design
- Extension tier: [Green/Yellow/Red]
- Explanation cost: [N] tokens
- Consistency: [How it follows existing patterns]

### Trade-offs
- Benefit: [What is gained]
- Cost: [What is lost or complicated]
- Justification: [Why worth it]

### Alternatives Considered
- [Option A]: [Why not chosen]
- [Option B]: [Why not chosen]
```

---

## Summary: Design Contribution Essentials

### The Core Model

RepoQL = **queryable graph database** for repositories, optimized for **LLM token efficiency** via **x-ray summaries** + **semantic search** + **universal schema**.

### The Constraints

1. Single-writer architecture (DuckDB limitation)
2. Core schema frozen (five tables, extend via views)
3. Agent-first design (<50 token explanations)
4. Token economics drive every decision
5. Just works (no config, high reliability)
6. Query-first (SQL, not specialized APIs)

### The Extension Philosophy

- **Views/macros/UDFs**: Yes (Green tier)
- **New base tables**: No (violates core stability)
- **Format support**: Always (via classifier + parser + view)
- **Query capabilities**: Always (via macros/UDFs)
- **Analysis**: Always (via annotations)

### The Evaluation Lens

Every design proposal answers:
1. What tokens saved or capability enabled? (Value)
2. How many tokens to explain? (Cost)
3. Green/Yellow/Red tier? (Fit)
4. Intuitive from training data? (Agent-first)
5. >95% success rate? (Reliability)

### The Decision Framework

```
Problem identified
    ↓
Can views/macros/UDFs solve it? → Yes → Green tier proposal
    ↓ No
Does it justify core change? → Yes → Yellow/Red tier (strong justification needed)
    ↓ No
Reconsider problem framing
```

### The Design Quality Test

Great RepoQL designs:
- ✅ Explainable in <50 tokens
- ✅ Save >10x explanation cost
- ✅ Extend via views/macros
- ✅ Intuitive from LLM training data
- ✅ Follow existing patterns
- ✅ High success rate, low false positives
- ✅ Query-accessible via SQL

---

**You now understand RepoQL's architecture, constraints, and design philosophy deeply enough to propose meaningful improvements.**

Focus on: What problem? How to solve via views/macros? What tokens saved? How intuitive? What trade-offs?
