# Discovery Notes: UDF Author Skill

Research conducted to understand the RepoQL UDF framework.

---

## Sources Explored

### DuckDB Research (`docs/duckdb/`)
- `DuckDB.md` - Architecture overview (columnar, vectorized, morsel-driven)
- `UDFs.md` - Deep dive on UDF implementation across C/C++/Rust/C#
- `Execution.md` - Push-based vectorized execution model
- `Types.md` - Type system including nested types

### Codebase Exploration
- `UdfFramework/Attributes.cs:1-61` - Attribute definitions
- `UdfFramework/UdfRegistry.cs:1-888` - Registration engine
- `UdfFramework/UdfHelpers.cs:1-231` - Serialization utilities
- `UdfImplementations/` - 25+ UDF implementations
- `DuckDbDataStore.cs:875-881` - Initialization call site

---

## Key Findings

### Design Decisions

1. **All-VARCHAR Strategy**: Every parameter is passed as VARCHAR from DuckDB to C#. Framework handles parsing. This simplifies registration (one type signature) and enables JSON for complex objects.

2. **Macro + Internal UDF Pattern**: Internal UDFs use `_` prefix convention. Public macros provide named parameters and defaults. Separation of concerns.

3. **Structured UDFs via JSON**: Return `IEnumerable<T>`, serialize to JSON array, expand via `json_each()` in macro. Elegant solution to table-valued functions.

4. **Constructor DI**: UDF classes can inject services via constructor. Resolved by `ActivatorUtilities.CreateInstance()`.

### Constraints Discovered

1. **DuckDB.NET min 1 param**: Technical limitation of generic registration methods.

2. **Max 4 direct params**: DuckDB.NET provides overloads up to 4. Beyond that, JSON packing.

3. **Exception handling**: Unmanaged callback context. Exceptions must be caught and serialized.

4. **Single-writer**: UDFs are read-only. Writes corrupt data.

### Patterns Observed

Looking at existing UDFs:

- `XrayUdf` - Complex structured UDF with DI, multiple params, JSON options
- `SnippetUdf` - Multiple scalar UDFs in one class
- `EmbedUdf` - Service injection, nullable service pattern
- `TreeUdf` - Pure scalar with default params
- `GlobMatchUdf` - Pattern matching utilities

### What Goes Wrong

1. **Forgetting SQL quotes in defaults**: `[UdfDefault("hello")]` vs `[UdfDefault("'hello'")]`

2. **Using fields instead of properties**: Reflection reads properties only.

3. **Not handling nulls**: Every input can be null from SQL.

4. **Heavy per-row operations**: UDFs called up to 2048 times per chunk.

5. **Lying about purity**: DuckDB caches/reorders pure functions.

---

## Zone Assessment

| Zone | Points | Rationale |
|------|--------|-----------|
| Knowledge | 50 | Framework details, attributes, type mapping, macro generation |
| Process | 15 | Light sequencing (design → implement → test) |
| Constraint | 25 | Hard rules (single-writer, param limits, error handling) |
| Wisdom | 10 | Design principles, when to choose patterns |
| **Total** | **100** | |

This is primarily a **knowledge skill** with significant **constraints**. The framework is complex enough that an agent needs facts they couldn't derive. The constraints are hard enough that violation causes failures.

---

## Skill Design Decisions

1. **SKILL.md as gestalt**: Core capsules for ScalarUdf, StructuredUdf, MacroPattern, DI. Quick reference table. Boundaries.

2. **references/framework.md**: Deep technical knowledge. Registration lifecycle, type mapping, error handling.

3. **references/patterns.md**: Working examples. Each pattern is copy-paste ready.

4. **references/constraints.md**: Hard rules with rationale. No exceptions means no exceptions.

5. **Progressive disclosure**: Agent reads SKILL.md first. Dives into references as needed.

---

## Open Questions

1. **Testing UDFs**: No coverage in this skill. Separate concern? Part of testing guidelines?

2. **Performance tuning**: When to cache, when to batch. Could be a reference.

3. **Manual macros**: When to write `.sql` files vs use generated macros. Edge case.

---

## What Would Claude Get Wrong Without This Skill?

1. Return non-string from scalar UDF → compilation error
2. Use fields in record → missing columns
3. Forget SQL quotes in default → syntax error
4. Let exception propagate → process crash
5. Write to database in UDF → data corruption
6. Not know about macro generation → reinvent it
7. Not know about JSON packing → stuck at 4 params

The skill prevents these failures by encoding the knowledge Claude lacks.

---

*Discovery complete. Skill written based on these findings.*
