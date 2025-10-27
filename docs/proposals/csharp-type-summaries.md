# Proposal: Enhanced C# Type Summaries with Headlines and Structure

## Summary

Extend C# format support to generate per-type headline summaries and detailed structure outlines that include XML documentation comments. This enhancement transforms the `csharp_types` view from a metadata catalog into a rich, searchable API documentation source by exposing actual member names in headlines and incorporating doc comments into structure outlines.

## Background

### Current State

The existing C# format support (implemented in `RepoQL.Formats.DotNet`) successfully indexes C# types and members into the graph database with the following capabilities:

**What works well:**
- Type and member metadata (accessibility, modifiers, signatures)
- Symbol tracking and cross-references (USES_SYMBOL edges)
- Semantic analysis with Roslyn compilation
- File-level summaries (headline, summary, structure in `artifact` table)

**What's missing:**
- **Per-type summaries**: Currently only file-level summaries exist
- **Member name discoverability**: Headlines show counts but not actual method/property names
- **Documentation integration**: XML doc comments are parsed but not exposed
- **Large class exploration**: Viewing a 1,000+ line class requires reading the entire file

### The Problem

**Scenario 1: API Discovery**
```sql
-- Current: What can I call on RepositoryIndexer?
SELECT qualified_name FROM csharp_types WHERE qualified_name = 'RepoQL.Core.RepositoryIndexer'
-- Returns: Just the name, no API information

-- Desired: See the public API at a glance
SELECT headline FROM csharp_types WHERE qualified_name = 'RepoQL.Core.RepositoryIndexer'
-- Should return: "StartAsync(), StopAsync(), Subscribe(), WaitForIdle(), ... +8 more"
```

**Scenario 2: Understanding Without Reading Code**
```sql
-- Current: Get class structure
SELECT structure FROM artifact WHERE uri = 'file:///RepositoryIndexer.cs'
-- Returns: File-level structure for ALL types in the file

-- Desired: Per-type structure with documentation
SELECT structure FROM csharp_types WHERE qualified_name = 'RepoQL.Core.RepositoryIndexer'
-- Should return: Class outline with XML doc comments explaining each method
```

**Scenario 3: Search by API Surface**
```sql
-- Impossible currently: Find types with a "Subscribe" method
-- Desired:
SELECT qualified_name FROM csharp_types WHERE headline LIKE '%Subscribe()%'
```

## Goals

1. **Generate per-type headlines** that list actual member names, making the API surface searchable
2. **Include XML documentation comments** in structure outlines for self-documenting APIs
3. **Support large class exploration** by providing overview before deep-diving
4. **Enable semantic search** on public API surface via headline indexing
5. **Maintain format-contained implementation** with no core schema changes

## Non-Goals

- Building IntelliSense or IDE features (read-only documentation)
- Generating rendered HTML/Markdown documentation
- Supporting languages other than C# in this iteration
- Real-time updates (batch indexing only)

## Design

### Headline Format

**Purpose:** Single-line summary listing actual public member names for discoverability and search.

**Format:**
```
"{accessibility} {modifiers} {kind} {name} {inheritance} | {member_names} | {traits}"
```

**Examples:**

```
Small interface:
"public interface IFormatLoader | CanLoadAsync(), LoadAsync(), GetSchemaScripts(), DiscoverEmbedsAsync()"

Medium class:
"public static class ContentDigest | FromBytes()"

Large class with truncation:
"public class RepositoryIndexer : IRepositoryIndexer | StartAsync(), StopAsync(), Subscribe(), WaitForIdle(), GetPipelineSnapshot(), ... +7 more methods | ClassificationQueueDepth, ParsingQueueDepth, IsReindexing (4 properties) | 20 async, 7 nested"

Record type:
"public record DocumentSpan | Uri, StartLine, EndLine, StartByte, EndByte"

Enum:
"public enum ResultFormat | Json, Plain, Markdown, Color"
```

**Member Name Listing Rules:**

1. **Methods**: Name with `()` suffix → `StartAsync()`
2. **Properties**: Name only → `Count` (optionally `Count {get}` for readonly)
3. **Fields**: Name only → `MaxRetries`
4. **Events**: Name with `(event)` suffix → `StatusChanged (event)`
5. **Enum values**: Name only → `Json, Plain, Markdown`

**Truncation:**
- ≤ 8 members: List all
- 9-15 members: List first 8, then `... +N more {kind}`
- \> 15 members: List first 6, then `... +N more`

**Multiple member kinds:**
Separate with `|` delimiter, ordered by importance (methods, properties, events, fields).

### Structure Format

**Purpose:** Multi-line outline showing type declaration, member signatures, and XML documentation comments.

**Format:**
```csharp
/// <XML doc comment for type>
{accessibility} {modifiers} {kind} {name}{generic_params} {inheritance}
{
  // {Visibility} {Kind} ({count})

  /// <XML doc comment for member>
  {signature1}

  /// <XML doc comment for member>
  {signature2}

  ...

  // {Visibility} {Kind} ({count})
  ...

  // Private: X methods, Y fields, Z properties

  // Nested Types ({count})
  {nested_type_name} : {nested_type_kind}
  ...
}
```

**Example:**
```csharp
/// Primary repository indexer that watches files and updates the graph database.
/// Supports incremental updates, file watching, and background embedding.
public class RepositoryIndexer : IRepositoryIndexer
{
  // Public Methods (12)

  /// Starts the background indexing service.
  /// Returns: Task that completes when service is started
  public Task StartAsync(CancellationToken cancellationToken)

  /// Stops the indexing service gracefully.
  /// Returns: Task that completes when service is stopped
  public Task StopAsync(CancellationToken cancellationToken)

  /// Subscribes an observer to receive indexing events.
  /// Parameters:
  ///   - observer: The observer that will receive events
  /// Returns: Disposable subscription
  public IDisposable Subscribe(IObserver<IndexerEvent> observer)

  ... 9 more methods

  // Public Properties (4)

  /// Number of items waiting in classification queue.
  public int ClassificationQueueDepth { get; }

  /// Number of items waiting in parsing queue.
  public int ParsingQueueDepth { get; }

  ... 2 more properties

  // Private: 33 methods, 35 fields, 1 property

  // Nested Types (7)
  public class RepositoryIndexerDebugView { ... }
  private class Unsubscriber { ... }
  ...
}
```

**Comment Extraction Rules:**

From XML doc comments (`/// <summary>`):
- Extract `<summary>` content (most important)
- Extract `<returns>` → format as "Returns: {text}"
- Extract `<param>` → format as "Parameters:\n  - {name}: {text}"
- Extract `<remarks>` for important notes
- Strip XML tags, preserve text
- Max 3 lines per member (truncate with "...")
- Preserve markers like `[Obsolete]`, `[Deprecated]`

From regular comments (`//`):
- Include comments immediately before member declaration
- Max 2 lines
- Exclude trivial comments like "// Constructor"

**Member Grouping:**
1. Public members first (with full signatures and comments)
2. Protected members (signatures only, optional)
3. Private members (just counts by kind)
4. Nested types (names and kinds)

**Within each visibility:**
- Methods
- Properties
- Events
- Fields

**Truncation:**
- Show all public members if ≤ 20
- Show first 15 public members if > 20, then "... N more"
- Private members: always show counts only

## Implementation

### Architecture

**Existing Components (reuse):**
- `CSharpInventoryWalker`: Already walks syntax tree with `SyntaxWalkerDepth.StructuredTrivia`
- `CSharpLoader.Materialize()`: Already generates file-level headline/structure
- `CSharpDocumentSurface`: Contains Types and Members lists

**New Components:**

1. **Comment Extraction** (in `CSharpInventoryWalker`)
```csharp
// Extend CSharpTypeInfo record
record CSharpTypeInfo(
    // ... existing fields
    string? XmlDocComment,      // Full XML doc
    string? SummaryText         // Parsed <summary> only
);

// Extend CSharpMemberInfo record
record CSharpMemberInfo(
    // ... existing fields
    string? XmlDocComment,
    string? SummaryText,
    string? ReturnsText,
    IReadOnlyList<(string Name, string Description)> ParameterDocs
);
```

2. **Per-Type Summary Generation** (new methods in `CSharpLoader`)
```csharp
private static string BuildTypeHeadline(
    CSharpTypeInfo type,
    IEnumerable<CSharpMemberInfo> members)
{
    // Implementation: Format member names according to spec
}

private static string BuildTypeStructure(
    CSharpTypeInfo type,
    IEnumerable<CSharpMemberInfo> members,
    IEnumerable<CSharpTypeInfo> nestedTypes)
{
    // Implementation: Format outline with comments
}
```

3. **Storage** (in `CSharpLoader.Materialize()`)
```csharp
// Add to typeProps JSON when creating type nodes
typeProps["headline"] = BuildTypeHeadline(type, typeMembers);
typeProps["structure"] = BuildTypeStructure(type, typeMembers, nestedTypes);
typeProps["member_count"] = typeMembers.Count;
typeProps["public_count"] = typeMembers.Count(m => m.Accessibility == "public");
```

4. **View Update** (in `Schema/csharp_views.sql`)
```sql
CREATE OR REPLACE VIEW csharp_types AS
SELECT
  -- Existing columns
  n.id as type_id,
  doc.uri as document_uri,
  json_extract_string(n.properties, '$.qualified_name') as qualified_name,
  json_extract_string(n.properties, '$.name') as name,
  json_extract_string(n.properties, '$.kind') as kind,
  json_extract_string(n.properties, '$.namespace') as namespace,
  json_extract_string(n.properties, '$.accessibility') as accessibility,
  json_extract_string(n.properties, '$.base_type') as base_type,
  n.properties->'interfaces' as interfaces,
  CAST(json_extract(n.properties, '$.is_partial') AS BOOLEAN) as is_partial,
  CAST(json_extract(n.properties, '$.is_static') AS BOOLEAN) as is_static,
  CAST(json_extract(n.properties, '$.is_record') AS BOOLEAN) as is_record,
  n.span_id,
  n.properties,

  -- NEW: Per-type summaries
  json_extract_string(n.properties, '$.headline') as headline,
  json_extract_string(n.properties, '$.structure') as structure,
  CAST(json_extract(n.properties, '$.member_count') AS INTEGER) as member_count,
  CAST(json_extract(n.properties, '$.public_count') AS INTEGER) as public_count,
  CAST(json_extract(n.properties, '$.nested_count') AS INTEGER) as nested_count

FROM node n
JOIN edge e ON e.destination_node_id = n.id AND e.type = 'HAS_PART'
JOIN node doc ON e.source_node_id = doc.id AND doc.kind = 'document'
WHERE n.kind = 'csharp.type';
```

### Comment Extraction Implementation

**Using Roslyn APIs:**

```csharp
private static CommentInfo ExtractComments(SyntaxNode node)
{
    var trivia = node.GetLeadingTrivia();

    // Find XML doc comment trivia
    var xmlTrivia = trivia
        .Where(t => t.Kind() == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                    t.Kind() == SyntaxKind.MultiLineDocumentationCommentTrivia)
        .FirstOrDefault();

    if (xmlTrivia == default)
        return CommentInfo.Empty;

    // Parse structured XML
    var structured = (DocumentationCommentTriviaSyntax)xmlTrivia.GetStructure()!;

    // Extract <summary>
    var summary = structured.Content
        .OfType<XmlElementSyntax>()
        .FirstOrDefault(e => e.StartTag.Name.LocalName.ValueText == "summary");
    var summaryText = summary?.Content.ToString().Trim();

    // Extract <returns>
    var returns = structured.Content
        .OfType<XmlElementSyntax>()
        .FirstOrDefault(e => e.StartTag.Name.LocalName.ValueText == "returns");
    var returnsText = returns?.Content.ToString().Trim();

    // Extract <param> tags
    var parameters = structured.Content
        .OfType<XmlElementSyntax>()
        .Where(e => e.StartTag.Name.LocalName.ValueText == "param")
        .Select(e => (
            Name: e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>()
                .FirstOrDefault()?.Identifier.ToString() ?? "",
            Description: e.Content.ToString().Trim()
        ))
        .ToList();

    return new CommentInfo(
        FullXml: xmlTrivia.ToString(),
        Summary: summaryText,
        Returns: returnsText,
        Parameters: parameters
    );
}

private record CommentInfo(
    string? FullXml,
    string? Summary,
    string? Returns,
    IReadOnlyList<(string Name, string Description)> Parameters)
{
    public static readonly CommentInfo Empty = new(null, null, null, Array.Empty<(string, string)>());
}
```

**Integration into CSharpInventoryWalker:**

```csharp
private IDisposable EnterType(BaseTypeDeclarationSyntax node, string kind)
{
    // ... existing code ...

    var comments = ExtractComments(node);  // NEW

    var typeInfo = new CSharpTypeInfo(
        // ... existing parameters ...
        XmlDocComment: comments.FullXml,
        SummaryText: comments.Summary
    );

    // ... rest of method ...
}

private void AddMember(SyntaxNode node, string kind, ...)
{
    // ... existing code ...

    var comments = ExtractComments(node);  // NEW

    var memberInfo = new CSharpMemberInfo(
        // ... existing parameters ...
        XmlDocComment: comments.FullXml,
        SummaryText: comments.Summary,
        ReturnsText: comments.Returns,
        ParameterDocs: comments.Parameters
    );

    // ... rest of method ...
}
```

### Edge Cases & Special Handling

**Generic Types:**
```csharp
// Headline
"public class WorkQueue<T> | EnqueueAsync(), DequeueAsync(), ... | generic"

// Structure
public class WorkQueue<T> where T : class
{
  /// Enqueues an item of type T.
  public Task EnqueueAsync(T item)
  ...
}
```

**Extension Methods:**
```csharp
// Headline
"public static class Extensions | ToList() (extension), Select() (extension), ..."

// Structure
public static class Extensions
{
  /// Converts sequence to list.
  public static List<T> ToList<T>(this IEnumerable<T> source)
}
```

**Partial Types:**
```csharp
// Headline: Merge all parts
"public partial class RepositoryIndexer | [methods from all parts] | partial"

// Structure: Show merged view with comment from primary part
```

**Operators:**
```csharp
// Headline
"public struct Vector | op_Addition(), op_Equality(), ..."

// Or formatted:
"public struct Vector | operator+(), operator==(), ..."
```

**Indexers:**
```csharp
// Structure
public T this[int index] { get; set; }
```

**Nullable Reference Types:**
```csharp
// Preserve in signatures
public Task<DocumentModel?> LoadAsync(string? filePath)
```

## Testing Strategy

### Unit Tests

**Comment Extraction:**
```csharp
[Fact]
public void ExtractComments_WithXmlDoc_ExtractsSummary()
{
    var code = """
        /// <summary>
        /// This is a test class.
        /// </summary>
        public class Test { }
        """;
    var tree = CSharpSyntaxTree.ParseText(code);
    var classNode = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
    var comments = ExtractComments(classNode);
    Assert.Equal("This is a test class.", comments.Summary);
}

[Fact]
public void ExtractComments_WithParamTags_ExtractsParameters()
{
    var code = """
        /// <summary>Test method</summary>
        /// <param name="x">The X value</param>
        /// <param name="y">The Y value</param>
        public void Test(int x, int y) { }
        """;
    // ... assert parameter extraction
}
```

**Headline Generation:**
```csharp
[Fact]
public void BuildTypeHeadline_SmallInterface_ListsAllMethods()
{
    var type = CreateTypeInfo("IFormatLoader", "interface", "public");
    var members = new[]
    {
        CreateMemberInfo("CanLoadAsync", "method", "public"),
        CreateMemberInfo("LoadAsync", "method", "public"),
        CreateMemberInfo("GetSchemaScripts", "method", "public")
    };
    var headline = BuildTypeHeadline(type, members);
    Assert.Contains("CanLoadAsync()", headline);
    Assert.Contains("LoadAsync()", headline);
    Assert.Contains("GetSchemaScripts()", headline);
}

[Fact]
public void BuildTypeHeadline_LargeClass_TruncatesAfter8Members()
{
    var members = Enumerable.Range(1, 20)
        .Select(i => CreateMemberInfo($"Method{i}", "method", "public"))
        .ToArray();
    var headline = BuildTypeHeadline(type, members);
    Assert.Contains("... +12 more", headline);
}
```

**Structure Generation:**
```csharp
[Fact]
public void BuildTypeStructure_IncludesXmlDocComments()
{
    var type = CreateTypeInfo("Test", "class", "public",
        summaryText: "This is a test class.");
    var members = new[]
    {
        CreateMemberInfo("Method1", "method", "public",
            summaryText: "Does something.")
    };
    var structure = BuildTypeStructure(type, members);
    Assert.Contains("/// This is a test class.", structure);
    Assert.Contains("/// Does something.", structure);
}
```

### Integration Tests

**End-to-End Indexing:**
```csharp
[Fact]
public async Task LoadAndMaterialize_GeneratesHeadlineAndStructure()
{
    // Given: A C# file with XML doc comments
    var code = """
        namespace Test;

        /// <summary>
        /// Repository for managing users.
        /// </summary>
        public class UserRepository
        {
            /// <summary>
            /// Gets user by ID.
            /// </summary>
            /// <param name="id">User identifier</param>
            /// <returns>User or null</returns>
            public Task<User?> GetByIdAsync(int id) => throw new NotImplementedException();
        }
        """;

    // When: Load and materialize
    var artifact = CreateTestArtifact(code);
    var loader = new CSharpLoader();
    var document = await loader.LoadAsync(artifact);
    var records = loader.Materialize(document);

    // Then: Type node has headline and structure
    var typeNode = records.Nodes.First(n => n.Kind == "csharp.type");
    var headline = JsonSerializer.Deserialize<JsonElement>(typeNode.Props)
        .GetProperty("headline").GetString();
    var structure = JsonSerializer.Deserialize<JsonElement>(typeNode.Props)
        .GetProperty("structure").GetString();

    Assert.Contains("GetByIdAsync()", headline);
    Assert.Contains("/// Repository for managing users.", structure);
    Assert.Contains("/// Gets user by ID.", structure);
    Assert.Contains("/// Parameters:", structure);
}
```

**View Query Tests:**
```csharp
[Fact]
public async Task CSharpTypesView_ExposesHeadlineAndStructure()
{
    // Given: Database with indexed C# types
    await IndexTestFile("UserRepository.cs");

    // When: Query csharp_types view
    var result = await ExecuteQuery("""
        SELECT qualified_name, headline, structure
        FROM csharp_types
        WHERE name = 'UserRepository'
        """);

    // Then: Returns headline and structure
    Assert.Single(result);
    Assert.Contains("GetByIdAsync()", result[0].Headline);
    Assert.Contains("/// Repository for managing users.", result[0].Structure);
}
```

### Validation Tests

**Against Real Codebase:**
```csharp
[Theory]
[InlineData("RepoQL.Core.RepositoryIndexer")]
[InlineData("RepoQL.Contracts.IFormatLoader")]
[InlineData("RepoQL.Data.DuckDB.DuckDbGraphStore")]
public async Task RealTypes_HaveNonNullHeadlineAndStructure(string qualifiedName)
{
    // Index the actual RepoQL codebase
    var result = await ExecuteQuery($"""
        SELECT headline, structure, member_count, public_count
        FROM csharp_types
        WHERE qualified_name = '{qualifiedName}'
        """);

    Assert.Single(result);
    Assert.NotNull(result[0].Headline);
    Assert.NotNull(result[0].Structure);
    Assert.True(result[0].MemberCount > 0);
}
```

**Search Functionality:**
```csharp
[Fact]
public async Task Search_ByMethodName_FindsTypesInHeadline()
{
    await IndexTestFile("TestClasses.cs");

    var result = await ExecuteQuery("""
        SELECT qualified_name
        FROM csharp_types
        WHERE headline LIKE '%Subscribe()%'
        ORDER BY qualified_name
        """);

    Assert.Contains("RepoQL.Core.RepositoryIndexer", result.Select(r => r.QualifiedName));
}
```

### Performance Tests

```csharp
[Fact]
public async Task IndexLargeClass_CompletesIn5Seconds()
{
    // Generate class with 500 members
    var code = GenerateLargeClass(memberCount: 500);
    var artifact = CreateTestArtifact(code);

    var stopwatch = Stopwatch.StartNew();
    var loader = new CSharpLoader();
    var document = await loader.LoadAsync(artifact);
    var records = loader.Materialize(document);
    stopwatch.Stop();

    Assert.True(stopwatch.ElapsedMilliseconds < 5000,
        $"Indexing took {stopwatch.ElapsedMilliseconds}ms");
}

[Fact]
public async Task QueryCSharpTypesView_Returns100TypesIn100ms()
{
    await IndexAllRepoQLTypes();

    var stopwatch = Stopwatch.StartNew();
    var result = await ExecuteQuery("""
        SELECT qualified_name, headline, structure
        FROM csharp_types
        WHERE accessibility = 'public'
        LIMIT 100
        """);
    stopwatch.Stop();

    Assert.Equal(100, result.Count);
    Assert.True(stopwatch.ElapsedMilliseconds < 100);
}
```

## Migration & Compatibility

### Backward Compatibility

**No breaking changes:**
- Existing `csharp_types` view columns unchanged
- New columns (`headline`, `structure`, etc.) are additions
- Existing queries continue to work
- File-level summaries in `artifact` table unchanged

**Reindexing:**
- Existing indexed C# files need reindexing to populate new fields
- During transition, `headline` and `structure` will be NULL for old data
- Views should handle NULL gracefully

### Migration Path

1. **Deploy code** with new comment extraction and summary generation
2. **Update schema** via `GetSchemaScripts()` returning updated view SQL
3. **Reindex incrementally** or trigger full reindex
4. **Validate** that all public types have non-NULL headline/structure

## Examples

### Query Examples

**Discover API Surface:**
```sql
SELECT qualified_name, headline
FROM csharp_types
WHERE namespace = 'RepoQL.Contracts'
  AND accessibility = 'public'
ORDER BY name;

-- Returns:
-- IFormatLoader | CanLoadAsync(), LoadAsync(), GetSchemaScripts(), DiscoverEmbedsAsync()
-- IFormatAnalyzer | AnalyzeAsync(), AnalyzeEmbeddedAsync()
-- ...
```

**Find Types by Method Name:**
```sql
SELECT qualified_name, headline
FROM csharp_types
WHERE headline LIKE '%StartAsync()%';

-- Returns all types with a StartAsync method
```

**Get Class Documentation:**
```sql
SELECT structure
FROM csharp_types
WHERE qualified_name = 'RepoQL.Core.RepositoryIndexer';

-- Returns full outline with doc comments
```

**Count Public APIs:**
```sql
SELECT
  namespace,
  COUNT(*) as public_types,
  SUM(public_count) as total_public_members
FROM csharp_types
WHERE accessibility = 'public'
GROUP BY namespace
ORDER BY total_public_members DESC;
```

**Explore Without Reading Code:**
```sql
-- Quick overview
SELECT qualified_name, headline
FROM csharp_types
WHERE qualified_name LIKE 'RepoQL.Core.%'
  AND accessibility = 'public';

-- Deep dive on specific type
SELECT structure
FROM csharp_types
WHERE qualified_name = 'RepoQL.Core.RepositoryIndexer';
```

## Success Criteria

1. ✅ **All indexed C# types have non-NULL headline and structure**
2. ✅ **Headlines list actual member names** (not just counts)
3. ✅ **Structure includes XML doc comments** from `<summary>` tags
4. ✅ **Public methods are fully documented** in structure
5. ✅ **Private members show counts** only, not full signatures
6. ✅ **Search by method name works** via `headline LIKE '%MethodName()%'`
7. ✅ **View queries return in < 100ms** for single type lookup
8. ✅ **Large classes (100+ members) generate structure in < 2s**
9. ✅ **Generic types preserve type parameters** in signatures
10. ✅ **Partial types show merged view** across all parts

## Future Enhancements

**Phase 2 Possibilities:**
- Extract `<example>` tags for usage examples
- Include `<exception>` documentation
- Generate method signature tooltips (JSON format for IDE integration)
- Support for C# 11+ features (required members, file-scoped types)
- Cross-reference links in doc comments (e.g., `<see cref="OtherType"/>`)
- Markdown rendering of doc comments
- Per-namespace summaries aggregating contained types

## References

- C# Format Support Proposal: `docs/proposals/csharp-format-support.md`
- X-ray Documentation: `docs/XRay.md`
- Roslyn API Documentation: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/
- XML Documentation Comments: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/
