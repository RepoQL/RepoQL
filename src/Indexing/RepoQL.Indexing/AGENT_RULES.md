# Agent Rules for RepoQL.Indexing

**Required Reading**: If you are an AI agent about to modify code in this directory, **you MUST read this entire document first**. These are non-negotiable rules that ensure system correctness and prevent catastrophic bugs.

---

## Rule Enforcement Summary

The system uses **defense in depth** - multiple layers prevent bugs:

| Rule | Enforcement | What Happens If Violated |
|------|-------------|--------------------------|
| **Stage boundaries** | ✅ Type system (won't compile) | Compilation error |
| **Single-threaded writer** | ✅ Architecture (1 worker queue) | Automatic - can't violate |
| **Pure functions** | ⚠️ Convention (no compiler check) | Tests fail, performance degrades |
| **Exception handling** | ⚠️ Convention (catch required) | Pipeline stalls, data loss |
| **Call `next()`** | ⚠️ Convention (must delegate) | Files silently skipped |
| **Test-first** | ⚠️ Process (PR rejected if no tests) | Technical debt, bugs in production |

**Key insight**: Most safety comes from **architecture and types**, not vigilance. The remaining rules require discipline.

---

## Golden Rules (NEVER VIOLATE)

### Rule 1: Test Before You Code

❌ **Do NOT write implementation code before tests**

```
BAD workflow:
1. Write processor
2. Test it manually
3. Maybe write tests later

REQUIRED workflow:
1. Write test for expected behavior
2. Run test (should fail)
3. Implement processor
4. Run test (should pass)
5. Add edge case tests
```

**Why**: Tests document intent. Writing them first ensures you understand requirements. Tests written after implementation tend to test what the code does, not what it should do.

**Enforcement**: Pull requests without tests will be rejected.

---

### Rule 2: Processors Are Pure Functions

❌ **NEVER access external state in processors**

```csharp
// ❌ FORBIDDEN - Database query
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    var doc = await _database.GetDocumentAsync(item.Uri); // NO
}

// ❌ FORBIDDEN - File system access (except via item)
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    var content = File.ReadAllText(item.PhysicalPath); // NO
}

// ❌ FORBIDDEN - HTTP requests
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    var schema = await _httpClient.GetAsync(schemaUrl); // NO
}

// ✅ ALLOWED - Read from item
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    using var stream = item.CreateReadStream(); // YES
    var content = await ReadStream(stream);
}
```

**Why**:
- External dependencies make testing impossible (no mocking points)
- Performance degrades (N files = N database queries)
- Race conditions with writer stage

**Exception**: Analysis stage receives `IAnalysisContext` which may provide controlled database access. This is the ONLY exception.

---

### Rule 3: Never Break the Pipeline

❌ **NEVER throw unhandled exceptions**

```csharp
// ❌ FORBIDDEN - Unhandled exception
public async Task ProcessAsync(...)
{
    var result = JsonSerializer.Deserialize<T>(content); // May throw
    return (result, PipelineResult.Success);
}

// ✅ REQUIRED - Catch and return error status
public async Task ProcessAsync(...)
{
    try
    {
        var result = JsonSerializer.Deserialize<T>(content);
        return (result, PipelineResult.Success);
    }
    catch (JsonException ex)
    {
        // Log error (TODO: add logging)
        return (null, PipelineResult.Error);
    }
}
```

**Why**: One malformed file should not block indexing of 10,000 others. Errors must isolate to individual items.

**Consequence**: Unhandled exceptions kill worker threads, causing entire pipeline to stall.

---

### Rule 4: Respect Stage Boundaries (Enforced by Type System)

✅ **Type system prevents modifying previous stage results**

```csharp
// ✅ ENFORCED - Compilation error if you try to modify
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    // item.MediaType is { get; } only - cannot set
    var descriptor = _registry.ResolveByMedia(item.MediaType); // Read works
    // item.MediaType = "different-type"; // Won't compile

    var records = Materialize(descriptor);
    return (records, PipelineResult.Success);
}

// ❌ Even casting won't work in processor - wrong interface type
// ((IndexItem)item).MediaType = "x"; // Still can't compile
```

**Why enforced**:
- Interfaces expose `{ get; }` only properties
- Processors receive interfaces, not concrete IndexItem
- C# type system prevents mutation

**Exception**: Dictionary (`item[key]`) is mutable for "Bag" pattern. This is intentional for stage-specific scratchpad data.

---

### Rule 5: Call `next()` or Document Why Not

❌ **NEVER silently skip files**

```csharp
// ❌ FORBIDDEN - File is skipped silently
public async Task ProcessAsync(...)
{
    if (!ShouldHandle(item))
        return (null, PipelineResult.Success); // NO - file is lost
}

// ✅ REQUIRED - Delegate to next processor
public async Task ProcessAsync(...)
{
    if (!ShouldHandle(item))
        return await next(item); // Pass to next
}

// ✅ ALLOWED - Explicit filtering with documentation
public async Task ProcessAsync(...)
{
    if (IsExcludedPattern(item))
    {
        // Explicitly filtered: .gitignore files are not indexed
        return (null, PipelineResult.Filtered);
    }
}
```

**Why**: Files that fall through the chain become invisible. Debugging "why wasn't this file indexed?" is nightmare.

**Rule**: Every `return` statement must either:
- Call `next(item)`, OR
- Return `PipelineResult.Filtered` with comment explaining why, OR
- Return `PipelineResult.Error` with logged exception

---

### Rule 6: Tests Must Be Self-Explanatory

❌ **NEVER write tests that require reading code to understand**

```csharp
// ❌ BAD - What is this testing?
[Test]
public void Test1()
{
    var p = new Parser();
    var r = p.Process(GetItem()).Result;
    Assert.True(r.Item1 != null);
}

// ✅ GOOD - Clear intent
[Test]
[DisplayName("Parses markdown headings and creates hierarchy")]
public async Task Given_MarkdownWithHeadings_When_Parse_Then_CreatesNestedNodes()
{
    // Arrange
    var parser = new MarkdownParser();
    var content = "# Title\n## Section\n### Subsection";
    var item = CreateFakeItem(content);

    // Act
    var (result, status) = await parser.ProcessAsync(item, FakeNext, ct);

    // Assert
    status.Should().Be(PipelineResult.Success);
    result.Should().NotBeNull();

    var headings = result!.Nodes.Where(n => n.Kind == "md_heading");
    headings.Should().HaveCount(3, "document has three headings");

    var edges = result.Edges.Where(e => e.Type == "CONTAINS");
    edges.Should().HaveCount(3, "each heading is child of document/parent heading");
}
```

**Why**: Tests are documentation. Future maintainers (human and AI) will read them to understand behavior. Failure messages in CI should be self-explanatory.

**Requirements**:
- ✅ Use `[DisplayName]` attribute
- ✅ Use Arrange/Act/Assert pattern
- ✅ Add `because` parameter when assertion isn't obvious
- ✅ Use meaningful variable names

---

### Rule 7: Don't Touch the Red Zone

🚫 **These components are PROTECTED - do not modify without human approval:**

- `PipelinePhase<TInput, TResult>` - Core orchestration logic
- `WorkQueue<T>` - Concurrency primitive
- `IndexingEngine` - Main orchestrator
- Interface contracts:
  - `IAsyncPipeline<TInput, TResult>`
  - `IDiscoveredArtifact`
  - `IClassifiedArtifact`
  - `IParsedArtifact`
  - `IAnnotatedArtifact`
- Database writer integration

**Why**: Changes here affect ALL processors. Bugs cascade. Very hard to test comprehensively.

**If you think you need to modify these**: You probably don't. Ask yourself:
- Can I solve this with a new processor instead?
- Can I use the IndexItem `Bag` property for stage-specific data?
- Is this a missing feature request (discuss with human first)?

---

## Safety Tiers

### 🟢 Green Tier - Safe for Agents

You can freely create/modify these:

1. **New Processors**
   - Classification: `IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>`
   - Parsing: `IAsyncPipeline<IClassifiedArtifact, Records?>`
   - Analysis: `IAsyncPipeline<IParsedArtifact, Annotation[]>`

2. **Tests for Processors**
   - Unit tests for your processor
   - Integration tests if needed

3. **Documentation**
   - Update README.md with new format support
   - Add examples
   - Improve clarity

4. **Helper Utilities**
   - Extension methods
   - Static utility classes
   - Data structures (non-pipeline)

**Permission**: Create freely, but follow rules 1-6.

---

### 🟡 Yellow Tier - Proceed with Caution

These require understanding of system design:

1. **IndexItem Properties**
   - ✅ Can add NEW properties for new stages
   - ❌ Cannot remove or rename existing properties
   - ❌ Cannot change property types (breaking change)

2. **Pipeline Registration**
   - ✅ Can add processors to pipeline collections
   - ⚠️ Order matters (first match wins in classification)
   - ❌ Don't reorder without understanding impact

3. **Error Handling**
   - ✅ Can improve error messages
   - ✅ Can add logging (when infrastructure exists)
   - ❌ Don't change error propagation behavior

**Permission**: Modify cautiously. Write extra tests. Document changes.

---

### 🔴 Red Tier - Requires Human Approval

These are critical infrastructure:

1. **Core Pipeline Classes**
   - `PipelinePhase<TInput, TResult>`
   - `WorkQueue<T>`
   - `IndexingEngine`

2. **Interface Contracts**
   - Changing signatures breaks all processors
   - Changing semantics breaks assumptions

3. **Concurrency Primitives**
   - Worker allocation
   - Queue capacities
   - Deduplication logic

4. **Database Writer**
   - Single-threaded constraint
   - Transaction structure

**Permission**: DENIED for agents. Flag for human review.

---

## Pre-Commit Checklist

Before you commit code, verify ALL of these:

- [ ] **Tests exist and pass**
  - `dotnet test` runs successfully
  - Coverage is ≥80% for new code
  - At least 3 tests per processor (happy, delegation, error)

- [ ] **No forbidden patterns**
  - No database queries in processors
  - No file system access (except via `item.CreateReadStream()`)
  - No HTTP requests
  - No unhandled exceptions
  - Always call `next()` or return `Filtered`/`Error`

- [ ] **Tests are clear**
  - Every test has `[DisplayName]` attribute
  - Arrange/Act/Assert sections are obvious
  - Failure messages would be understandable in CI log

- [ ] **Documentation updated**
  - README.md mentions new format if applicable
  - PROCESSOR_GUIDE.md examples accurate
  - Inline comments explain non-obvious logic

- [ ] **No Red Zone modifications**
  - Core pipeline classes unchanged
  - Interface contracts unchanged
  - Concurrency primitives unchanged

- [ ] **Code compiles without warnings**
  - `dotnet build` succeeds
  - No new compiler warnings introduced

---

## Violation Consequences

**Automated Checks**: CI will reject PRs that:
- Have no tests
- Have failing tests
- Have coverage <80%
- Have compiler errors/warnings

**Human Review**: PRs will be rejected if:
- Tests are unclear or poorly named
- Forbidden patterns detected (database queries, unhandled exceptions)
- Red Zone modifications without approval
- Missing documentation

**Emergency Rollback**: Code will be reverted if:
- Pipeline stops processing files (unhandled exception)
- Data corruption (parallel database writes)
- Performance regression >50% (database queries in hot path)

---

## Common Questions

### Q: Why should I use `ProvisionalMediaType` instead of checking file extensions?

**A**: `RawArtifact.ProvisionalMediaType` is already computed from file naming conventions by the file system layer. Benefits:
- **Accuracy**: Handles multiple extensions automatically (`.yaml`, `.yml`)
- **Consistency**: All files use same detection logic
- **Performance**: Computed once, not per-classifier
- **Maintainability**: Extension mappings centralized

**Example**:
```csharp
// ❌ BAD - Duplicate logic
if (!item.Name.EndsWith(".md") && !item.Name.EndsWith(".markdown"))
    return await next(item);

// ✅ GOOD - Use provisional type
if (item.RawArtifact.ProvisionalMediaType.Value?.Type != "text/markdown")
    return await next(item);
```

**When to inspect content**: Only when provisional type is ambiguous (e.g., `.json` could be JSON, JSONC, JSON5).

---

### Q: Why can't I access the database in my processor?

**A**: Processors must be pure functions to enable:
- **Testing**: Can't mock database calls easily
- **Performance**: 10,000 files = 10,000 queries = slow
- **Correctness**: Race conditions with writer stage

**Solution**: Store data in `item.Bag`, retrieve in analysis stage via `IAnalysisContext`.

---

### Q: My processor needs data from another file. How?

**A**: That's cross-file analysis, which belongs in **MultiFileAnalysisPipeline** (Stage 4), not earlier stages.

**Example**: "Find unused functions" requires call graph = multi-file analysis.

**Solution**:
1. In parsing stage: Extract function definitions, store in graph
2. In multi-file analysis: Query graph, build call graph, emit annotations

---

### Q: Can I change the order of processors?

**A**: Yes, but be careful:
- **Classification**: First match wins. Order by specificity (most specific first)
- **Parsing**: Same rule
- **Analysis**: Order doesn't usually matter (all run)

**Example**:
```csharp
// GOOD - specific to general
new MarkdownClassifier(),  // Matches .md, .markdown
new TextClassifier(),      // Matches any text/* (fallback)

// BAD - general first (markdown never reached)
new TextClassifier(),      // Matches all text files first
new MarkdownClassifier(),  // Never called for .md files
```

---

### Q: How do I debug a processor?

**A**: Use tests, not full pipeline:

```csharp
[Test]
public async Task Debug_MyIssue()
{
    var processor = new MyProcessor();
    var item = CreateFakeItem("problematic-file.md");

    // Set breakpoint here ↓
    var (result, status) = await processor.ProcessAsync(item, FakeNext, ct);

    // Inspect result, status
}
```

**Run single test**: `dotnet test --filter "FullyQualifiedName~Debug_MyIssue"`

---

### Q: What if I found a bug in core pipeline code (Red Zone)?

**A**:
1. Write a test that reproduces the bug
2. Document the expected behavior
3. Flag for human review - do NOT fix it yourself
4. Create an issue with:
   - Reproduction test
   - Expected vs actual behavior
   - Impact assessment (how many files affected)

---

## Emergency Contacts

If you're an agent and you:
- Encountered an ambiguous situation not covered here
- Think you need to violate a rule
- Found a bug in these rules themselves

**STOP** and flag for human review. Include:
- What you were trying to do
- Why existing guidelines don't cover it
- Your proposed solution
- Impact if you're wrong

**Do not proceed without approval.** Better to ask than break production.

---

## Final Reminder

These rules exist because the indexing pipeline is **mission-critical**:
- Malformed index = broken queries = unusable system
- Pipeline hang = no updates = stale data
- Data corruption = requires full rebuild = hours of downtime

**When in doubt, be conservative.** It's always safer to:
- Write more tests
- Ask for human review
- Leave code unchanged

The goal is **hundreds of processors written by agents, with zero production incidents**. These rules make that possible.
