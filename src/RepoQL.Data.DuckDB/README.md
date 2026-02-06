# RepoQL.Data.DuckDB

DuckDB-backed property graph store with SQL-first query surface. Single-writer architecture ensures consistency; UDF framework enables extensible querying.

## Core Invariant

All repository data flows through `DuckDbDataStore`. It enforces thread safety via `ReaderWriterLockSlim`. Parallel writes corrupt the database.

```csharp
// ONLY way to access DuckDB
var store = serviceProvider.GetRequiredService<DuckDbDataStore>();
store.Write(records);  // Thread-safe
var results = store.Read("SELECT * FROM node", r => ...);  // Thread-safe
```

---

## Schema: Five Frozen Tables

The core schema never changes. Extend via views, macros, and UDFs only.

| Table | Purpose | Key Columns |
|-------|---------|-------------|
| `artifact` | Content container (file bytes + x-ray summaries) | `id`, `digest`, `text_content`, `headline`, `summary`, `structure` |
| `node` | Entities (documents and things inside them) | `id`, `kind`, `uri`, `artifact_id`, `span_id` |
| `edge` | Relationships (composition tree + references) | `source_node_id`, `destination_node_id`, `type`, `is_composition` |
| `span` | Precise locations (line/char ranges) | `document_id`, `start_line`, `end_line` |
| `annotation` | Diagnostics & facts (lint, metrics, outlines) | `kind`, `severity`, `message`, `target_node_id` |

**Addressing**: Everything uses RepoURIs: `file:///src/Foo.cs#symbol=Bar&line=42`

---

## UDF Framework

Attribute-based UDF registration with constructor DI. Auto-generates SQL macros.

### Capsule: UdfRegistration

**Invariant**
Mark class with `[UdfClass]`, methods with `[ScalarUdf]` or `[StructuredUdf]`. Framework discovers, registers, and generates macros automatically.

**Example**
```csharp
[UdfClass]
public class MyUdf(IMyService service)  // Constructor DI
{
    [ScalarUdf("_my_internal", MacroName = "my_macro", Description = "Does something")]
    public string Execute(
        string input,
        [UdfDefault("100")] int limit)  // SQL default value
    {
        return service.Process(input, limit);
    }
}
```

Generated macro: `my_macro(input, limit := 100)` → calls `_my_internal(input::VARCHAR, json_object('limit', limit))`

**Depth**
- **Attributes**: `UdfFramework/Attributes.cs`
- **Registry**: `UdfFramework/UdfRegistry.cs` - discovery, registration, macro generation
- **IL Trimming**: `ILLink.Descriptors.xml` preserves UDF classes during AOT compilation
- **Parameter Limit**: DuckDB.NET supports max 3 type params; framework packs 3rd+ params into JSON

### Adding a UDF

1. Create class in `UdfImplementations/` with `[UdfClass]`
2. Add constructor params for dependencies (resolved from DI)
3. Mark methods with `[ScalarUdf("internal_name", MacroName = "public_name")]`
4. Use `[UdfDefault("sql_literal")]` for optional params
5. Return `string` (scalar) or `IEnumerable<T>` (structured → JSON array)

No additional registration needed. IL Linker config preserves the class automatically.

### Existing UDFs

| Macro | UDF Class | Purpose |
|-------|-----------|---------|
| `_explore_internal(...)` | `ExploreUdf` | Token-budgeted codebase exploration (called by explore tool) |
| `ask(...)` | `LlmUdf` | Ask questions about query results using LLM |
| `llm_extract(...)` | `LlmUdf` | LLM-powered code extraction |
| `embed_status()` | `EmbedUdf` | Embedding provider diagnostics |
| `embed_text(...)` | `EmbedUdf` | Text → embedding vector |
| `indexing_diagnostics()` | `DiagnosticsUdf` | Indexer status |
| `indexing_queue()` | `DiagnosticsUdf` | Pending indexing items |
| `mcp_call(...)` | `McpCallUdf` | Call external MCP tools |

---

## Key SQL Macros

Beyond UDFs, embedded SQL scripts define core macros:

| Macro | Location | Purpose |
|-------|----------|---------|
| `search(keywords, k)` | `Schema/Macros/search.sql` | Semantic + lexical search |
| `snippet(uri, context)` | `Schema/Macros/snippet.sql` | Code preview with context |
| `Files` (view) | `Schema/Views/Files.sql` | Document inventory |

---

## Extension Points

### Format Schema Providers

Formats add views/macros via `IFormatSchemaProvider`:

```csharp
public class MyLoader : IFormatSchemaProvider
{
    public IEnumerable<FormatSqlScript> GetSchemaScripts() =>
    [
        new("MyFormat/views.sql", GetEmbeddedSql("Views/my_views.sql"))
    ];
}
```

Scripts execute after core schema during `DuckDbDataStore` initialization.

### Embedded SQL Scripts

Core scripts in `Schema/`:
- `Tables/` - Core table definitions
- `Views/` - Computed views (types, functions, files)
- `Macros/` - SQL macros

Scripts are embedded resources (see `.csproj` `<EmbeddedResource Include="Schema/**/*.sql" />`).

---

## Testing

Uses TUnit (not xUnit). In-memory store for isolation:

```csharp
public class MyTests : IDisposable
{
    private readonly DuckDbDataStore _store;

    public MyTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new TestLlmProvider());
        _store = new DuckDbDataStore(serviceProvider: services.BuildServiceProvider());
    }

    [Test]
    public void MyTest()
    {
        var results = _store.Read("SELECT my_macro('test')", r => r.GetString(0));
        results.Should().HaveCount(1);
    }

    public void Dispose() => _store.Dispose();
}
```

Run tests:
```bash
cd src/tests/RepoQL.Data.DuckDB.Tests
dotnet run -- --treenode-filter "/*/*/MyTests/*"
```

---

## Error Handling

UDF exceptions surface as SQL errors with context:

```
UDF '_my_internal': Original error message
```

The framework catches all exceptions including DI resolution failures. Never causes hangs.

---

## Files

```
RepoQL.Data.DuckDB/
├── DuckDbDataStore.cs          # Single-writer store (all DB access here)
├── UdfFramework/
│   ├── Attributes.cs           # [UdfClass], [ScalarUdf], [StructuredUdf], [UdfDefault]
│   ├── UdfRegistry.cs          # Discovery, registration, macro generation
│   └── UdfHelpers.cs           # JSON serialization utilities
├── UdfImplementations/
│   ├── ExploreUdf.cs            # explore search (called by explore tool)
│   ├── LlmUdf.cs               # ask(), llm_extract()
│   ├── EmbedUdf.cs             # embed_status(), embed_text()
│   ├── DiagnosticsUdf.cs       # indexing_diagnostics(), indexing_queue()
│   └── McpCallUdf.cs           # mcp_call()
├── Schema/
│   ├── Tables/                 # Core schema DDL
│   ├── Views/                  # Computed views
│   └── Macros/                 # SQL macro definitions
├── ILLink.Descriptors.xml      # IL trimmer preservation config
└── README.md                   # This file
```

---

## See Also

- `docs/Schema.md` - Complete schema reference
- `docs/RepoqlDesign.md` - Architecture and constraints
- `CLAUDE.md` - Build, test, and contribution guidelines
