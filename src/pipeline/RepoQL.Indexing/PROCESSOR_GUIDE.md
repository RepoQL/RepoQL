# Processor Development Guide

This guide provides **step-by-step instructions** for implementing new processors in the RepoQL.Indexing pipeline. Follow this as a checklist to ensure your processor is correct, testable, and safe.

---

## Quick Start Checklist

Before you begin:
- [ ] Read [README.md](README.md) - Understand the pipeline architecture
- [ ] Identify which stage your processor belongs to (Classification, Parsing, or Analysis)
- [ ] Understand the input/output contracts for that stage
- [ ] Have test data ready (sample files your processor should handle)

---

## Stage 1: Classification Processors

**Purpose**: Refine the provisional media type or add semantic parameters (like `kind`) to distinguish format variants.

### Understanding Provisional Media Type

**Key Concept**: `RawArtifact.ProvisionalMediaType` is already computed from file extensions using naming conventions (e.g., `.md` → `text/markdown`, `.yaml` → `application/yaml`).

**Classifiers exist to**:
1. **Refine** the provisional type by adding parameters (e.g., `text/markdown;kind=markdown.doc`)
2. **Validate** ambiguous extensions through content inspection (e.g., distinguish JSON from JSONC)
3. **Detect** format when extension is generic (e.g., `.txt` files with shebangs)

**Most files don't need classifiers** - the provisional media type is often sufficient for parsing!

### When to Create a Classification Processor

Create a classifier when:
- ✅ You need to add semantic `kind` parameters to distinguish variants (e.g., `markdown.doc` vs `markdown.fragment`)
- ✅ You need content-based detection for ambiguous extensions (e.g., `.json` could be JSON, JSONC, JSON5)
- ✅ You need to detect format from content when extension is generic (e.g., shebang lines in `.txt` files)
- ❌ **NOT** needed just to support a new extension - that's handled by `GuessMediaTypeFromNamingConvention`

### Template

```csharp
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Classifiers;

/// <summary>
/// Classifies [FILE_TYPE] files by [DETECTION_METHOD].
/// </summary>
public class [FormatName]Classifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public async Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        // Step 1: Check provisional media type (already computed from file extension/naming)
        // This is more accurate than re-checking extensions yourself
        if (!ShouldHandle(item.RawArtifact.ProvisionalMediaType.Value))
            return await next(item);

        // Step 2: (Optional) Content-based validation
        // Only if provisional type match isn't sufficient (e.g., ambiguous extensions)
        // Most classifiers can skip this step
        try
        {
            using var stream = item.CreateReadStream();
            if (!await ValidateContent(stream, token))
                return await next(item);
        }
        catch (Exception ex)
        {
            // Log error but don't crash pipeline
            // TODO: Add logging
            return (null, PipelineResult.Error);
        }

        // Step 3: Return refined semantic media type
        // Add parameters like 'kind' to distinguish variants
        var mediaType = SemanticMediaType.Parse("[MEDIA_TYPE_STRING];kind=[specific_kind]");
        return (mediaType, PipelineResult.Success);
    }

    private static bool ShouldHandle(SemanticMediaType? provisionalType)
    {
        // Check the provisional media type that was computed from naming conventions
        // This is already done by the file system layer
        return provisionalType?.Type == "[expected/base-type]";
    }

    private static async Task<bool> ValidateContent(Stream stream, CancellationToken token)
    {
        // Optional: Read file header to confirm format
        // Example: Check for magic bytes, shebang, YAML vs JSON disambiguation, etc.
        // Most classifiers don't need this
        return true;
    }
}
```

### Example: YAML Classifier

```csharp
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Classifiers;

/// <summary>
/// Classifies YAML files (.yaml, .yml) as application/yaml.
/// Refines the provisional media type by adding the 'kind' parameter.
/// </summary>
public class YamlClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        // Check provisional media type instead of file extension
        // The file system layer already computed this from .yaml/.yml extensions
        var provisionalType = item.RawArtifact.ProvisionalMediaType.Value;
        if (provisionalType?.Type != "application/yaml")
        {
            return next(item);
        }

        // Refine by adding 'kind' parameter to distinguish YAML variants
        var mediaType = SemanticMediaType.Parse("application/yaml;kind=yaml.doc");
        return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
    }
}
```

### Classification Test Template

```csharp
using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using TUnit.Core;

namespace RepoQL.Indexing.Tests.Classifiers;

public class [FormatName]ClassifierTests
{
    [Test]
    [DisplayName("Classifies [.ext] files as [format_name]")]
    public async Task Given_[Format]Extension_When_Classify_Then_Returns_[Format]MediaType()
    {
        // Arrange
        var classifier = new [FormatName]Classifier();
        var item = CreateFakeItem("sample.[ext]");

        // Act
        var (result, status) = await classifier.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result.Value.Type.Should().Be("[expected/media-type]");
    }

    [Test]
    [DisplayName("Passes non-[format] files to next processor")]
    public async Task Given_Non[Format]Extension_When_Classify_Then_CallsNext()
    {
        // Arrange
        var classifier = new [FormatName]Classifier();
        var item = CreateFakeItem("other.txt");
        var nextCalled = false;

        // Act
        await classifier.ProcessAsync(
            item,
            _ => {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        // Assert
        nextCalled.Should().BeTrue("classifier should delegate to next processor");
    }

    [Test]
    [DisplayName("Returns error status when file cannot be read")]
    public async Task Given_UnreadableFile_When_Classify_Then_Returns_Error()
    {
        // Arrange
        var classifier = new [FormatName]Classifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("test.[ext]");
        A.CallTo(() => item.CreateReadStream()).Throws<IOException>();

        // Act
        var (result, status) = await classifier.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Error);
        result.Should().BeNull();
    }

    // Helper methods
    private static IDiscoveredArtifact CreateFakeItem(string name)
    {
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns(name);
        A.CallTo(() => item.Exists).Returns(true);
        return item;
    }

    private static Task<(SemanticMediaType?, PipelineResult)> FakeNext(IDiscoveredArtifact _)
        => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
}
```

---

## Stage 2: Parsing Processors

**Purpose**: Load a file and materialize it into graph structures (Artifacts, Nodes, Spans, Edges).

### When to Create a Parsing Processor

Create a parser when:
- ✅ You've added a classifier and need to extract structure
- ✅ You want to make file contents queryable (headings, functions, types, etc.)
- ✅ You need x-ray summaries (headline, summary, structure)

### Template

```csharp
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Indexing.Parsers;

/// <summary>
/// Parses [FILE_TYPE] files and materializes structure into graph records.
/// </summary>
public class [FormatName]Parser : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        // Step 1: Check if this parser handles the media type
        if (!ShouldHandle(item.MediaType))
            return await next(item);

        try
        {
            // Step 2: Read file content
            using var stream = item.CreateReadStream();
            var content = await ReadContent(stream, token);

            // Step 3: Parse content into document model
            var document = Parse(content);

            // Step 4: Materialize into graph records
            var records = Materialize(document, item.Uri);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            // Log error but don't crash pipeline
            // TODO: Add logging
            return (null, PipelineResult.Error);
        }
    }

    private static bool ShouldHandle(SemanticMediaType? mediaType)
    {
        return mediaType?.Type == "[expected/media-type]";
    }

    private static async Task<string> ReadContent(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }

    private static [DocumentModel] Parse(string content)
    {
        // TODO: Implement parsing logic
        // Use a library or write custom parser
        throw new NotImplementedException();
    }

    private static Records Materialize([DocumentModel] document, RepoUri uri)
    {
        var records = new Records();

        // Create artifact (file-level)
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "[will be set by engine]",
            MediaType = SemanticMediaType.Parse("[media_type]"),
            Headline = $"[Headline format]",
            Summary = "[Summary from document]",
            Structure = "[Hierarchical structure]"
        };
        records.Artifacts.Add(artifact);

        // Create document node
        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri.ToString(),
            ArtifactId = artifact.Id
        };
        records.Nodes.Add(docNode);

        // TODO: Add child nodes (headings, types, functions, etc.)
        // TODO: Add spans (line/byte positions)
        // TODO: Add edges (parent-child, references)

        return records;
    }
}
```

### Example: Simple Text Parser

```csharp
using System.Text;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Indexing.Parsers;

/// <summary>
/// Parses plain text files and creates a simple artifact with line count.
/// </summary>
public class TextParser : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (item.MediaType?.Type != "text/plain")
            return await next(item);

        try
        {
            using var stream = item.CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(token);
            var lines = content.Split('\n').Length;

            var records = new Records();

            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                MediaType = SemanticMediaType.Parse("text/plain"),
                Headline = $"{item.Name} | {lines} lines",
                Summary = content.Length > 200 ? content[..200] + "..." : content
            };
            records.Artifacts.Add(artifact);

            var docNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = item.Uri.ToString(),
                ArtifactId = artifact.Id
            };
            records.Nodes.Add(docNode);

            return (records, PipelineResult.Success);
        }
        catch (Exception)
        {
            return (null, PipelineResult.Error);
        }
    }
}
```

### Parsing Test Template

```csharp
using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using System.Text;
using TUnit.Core;

namespace RepoQL.Indexing.Tests.Parsers;

public class [FormatName]ParserTests
{
    [Test]
    [DisplayName("Parses [format] file and creates graph records")]
    public async Task Given_[Format]File_When_Parse_Then_CreatesRecords()
    {
        // Arrange
        var parser = new [FormatName]Parser();
        var content = "[sample file content]";
        var item = CreateFakeItem("test.[ext]", content, "[media/type]");

        // Act
        var (result, status) = await parser.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Artifacts.Should().HaveCountGreaterThan(0);
        result.Nodes.Should().HaveCountGreaterThan(0, "at minimum a document node");

        var docNode = result.Nodes.FirstOrDefault(n => n.Kind == "document");
        docNode.Should().NotBeNull();
        docNode!.Uri.Should().NotBeNullOrEmpty();
    }

    [Test]
    [DisplayName("Passes non-[format] files to next processor")]
    public async Task Given_WrongMediaType_When_Parse_Then_CallsNext()
    {
        // Arrange
        var parser = new [FormatName]Parser();
        var item = CreateFakeItem("other.txt", "content", "text/plain");
        var nextCalled = false;

        // Act
        await parser.ProcessAsync(
            item,
            _ => {
                nextCalled = true;
                return Task.FromResult<(Records?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        // Assert
        nextCalled.Should().BeTrue();
    }

    [Test]
    [DisplayName("Returns error for malformed [format] content")]
    public async Task Given_MalformedContent_When_Parse_Then_Returns_Error()
    {
        // Arrange
        var parser = new [FormatName]Parser();
        var item = CreateFakeItem("bad.[ext]", "[malformed content]", "[media/type]");

        // Act
        var (result, status) = await parser.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Error);
        result.Should().BeNull();
    }

    // Helper methods
    private static IClassifiedArtifact CreateFakeItem(string name, string content, string mediaType)
    {
        var item = A.Fake<IClassifiedArtifact>();
        A.CallTo(() => item.Name).Returns(name);
        A.CallTo(() => item.Uri).Returns(RepoUri.Parse($"file:///{name}"));
        A.CallTo(() => item.MediaType).Returns(SemanticMediaType.Parse(mediaType));
        A.CallTo(() => item.CreateReadStream()).Returns(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return item;
    }

    private static Task<(Records?, PipelineResult)> FakeNext(IClassifiedArtifact _)
        => Task.FromResult<(Records?, PipelineResult)>((null, PipelineResult.Success));
}
```

---

## Stage 3: Single-File Analysis Processors

**Purpose**: Validate, lint, or annotate a single file without requiring cross-file context.

### When to Create an Analysis Processor

Create an analyzer when:
- ✅ You need to validate file structure (e.g., markdown link checker)
- ✅ You need to emit lint warnings (e.g., style violations)
- ✅ You need single-file metrics (e.g., cyclomatic complexity)

### Template

```csharp
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Analyzers;

/// <summary>
/// Analyzes [FILE_TYPE] files for [WHAT_IT_CHECKS].
/// </summary>
public class [FormatName]Analyzer : IAsyncPipeline<IParsedArtifact, Annotation[]>
{
    public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IParsedArtifact item,
        CallNextPipeline<IParsedArtifact, Annotation[]> next,
        CancellationToken token)
    {
        // Step 1: Check if this analyzer handles the media type
        if (!ShouldHandle(item.MediaType))
            return await next(item);

        var annotations = new List<Annotation>();

        try
        {
            // Step 2: Validate/analyze the parsed records
            if (item.Records != null)
            {
                foreach (var issue in AnalyzeRecords(item.Records))
                {
                    annotations.Add(issue);
                }
            }

            return (annotations.ToArray(), PipelineResult.Success);
        }
        catch (Exception ex)
        {
            // Log error but don't crash pipeline
            // TODO: Add logging
            return (null, PipelineResult.Error);
        }
    }

    private static bool ShouldHandle(SemanticMediaType? mediaType)
    {
        return mediaType?.Type == "[expected/media-type]";
    }

    private static IEnumerable<Annotation> AnalyzeRecords(Records records)
    {
        // TODO: Implement analysis logic
        // Example: Check for missing required nodes, validate structure, etc.
        yield break;
    }
}
```

### Analysis Test Template

```csharp
using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using TUnit.Core;

namespace RepoQL.Indexing.Tests.Analyzers;

public class [FormatName]AnalyzerTests
{
    [Test]
    [DisplayName("Detects [specific_issue] in [format] files")]
    public async Task Given_FileWith[Issue]_When_Analyze_Then_ReturnsAnnotation()
    {
        // Arrange
        var analyzer = new [FormatName]Analyzer();
        var item = CreateFakeItemWithRecords(/* create records with issue */);

        // Act
        var (result, status) = await analyzer.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Should().HaveCountGreaterThan(0);

        var annotation = result.First();
        annotation.Severity.Should().Be("warning");
        annotation.Message.Should().Contain("[issue description]");
    }

    [Test]
    [DisplayName("Returns no annotations for valid [format] files")]
    public async Task Given_ValidFile_When_Analyze_Then_ReturnsEmptyArray()
    {
        // Arrange
        var analyzer = new [FormatName]Analyzer();
        var item = CreateFakeItemWithRecords(/* create valid records */);

        // Act
        var (result, status) = await analyzer.ProcessAsync(item, FakeNext, CancellationToken.None);

        // Assert
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }

    private static IParsedArtifact CreateFakeItemWithRecords(Records? records = null)
    {
        var item = A.Fake<IParsedArtifact>();
        A.CallTo(() => item.MediaType).Returns(SemanticMediaType.Parse("[media/type]"));
        A.CallTo(() => item.Records).Returns(records ?? new Records());
        return item;
    }

    private static Task<(Annotation[]?, PipelineResult)> FakeNext(IParsedArtifact _)
        => Task.FromResult<(Annotation[]?, PipelineResult)>((null, PipelineResult.Success));
}
```

---

## Common Pitfalls

### ❌ Don't Query the Database in Processors

```csharp
// BAD
public async Task ProcessAsync(...)
{
    var existingDoc = await _db.QueryAsync(...); // NO!
}
```

**Why**: Makes processor untestable, slow, and dependent on external state.

### ❌ Don't Modify Input Item Directly

```csharp
// BAD
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    ((IndexItem)item).MediaType = "something"; // NO!
}
```

**Why**: Violates stage isolation. Return results instead.

### ❌ Don't Throw Unhandled Exceptions

```csharp
// BAD
public async Task ProcessAsync(...)
{
    var result = DangerousOperation(); // May throw
    return (result, PipelineResult.Success);
}

// GOOD
public async Task ProcessAsync(...)
{
    try
    {
        var result = DangerousOperation();
        return (result, PipelineResult.Success);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed processing {Uri}", item.Uri);
        return (null, PipelineResult.Error);
    }
}
```

### ❌ Don't Forget to Call `next()`

```csharp
// BAD - File is silently skipped
public async Task ProcessAsync(...)
{
    if (!ShouldHandle(item))
        return (null, PipelineResult.Success); // NO!
}

// GOOD - Pass to next processor
public async Task ProcessAsync(...)
{
    if (!ShouldHandle(item))
        return await next(item);
}
```

---

## Registration

Once your processor is implemented and tested:

1. **Add to pipeline constructor** (in `IndexingEngine.cs`):

```csharp
// Classification
Classifier = classifier ?? new ClassificationPipeline([
    new MarkdownClassifier(),
    new YamlClassifier(),  // Your new classifier
]);

// Parsing
Parser = parser ?? new ParsingPipeline([
    new MarkdownParser(),
    new YamlParser(),  // Your new parser
]);

// Analysis
SingleFileAnalyzer = singleFileAnalyzer ?? new SingleFileAnalysisPipeline([
    new MarkdownLinkAnalyzer(),
    new YamlSchemaAnalyzer(),  // Your new analyzer
]);
```

2. **Run all tests**:
```bash
dotnet test
```

3. **Check coverage** (should be ≥80% for new code)

---

## Checklist Before Committing

- [ ] Processor implements correct interface (`IAsyncPipeline<TInput, TResult>`)
- [ ] Processor calls `next()` for unhandled files
- [ ] Processor catches exceptions and returns `PipelineResult.Error`
- [ ] Processor has at least 3 tests (happy path, delegation, error)
- [ ] Tests use `DisplayName` attribute with clear descriptions
- [ ] Tests assert on both `result` and `status`
- [ ] No database queries in processor code
- [ ] No modifications to input item
- [ ] Processor is registered in `IndexingEngine`
- [ ] All tests pass (`dotnet test`)

---

## Getting Help

**Stuck?** Check these resources:
- [README.md](README.md) - Overall architecture
- [Testing Guidelines](../../docs/knowledge/testing-guidelines.md) - Test patterns
- [Pipeline Architecture](../../docs/proposals/indexer-redesign/02-pipeline-architecture.md) - Detailed flow

**Still stuck?** Look at existing processors:
- `MarkdownClassifier` - Simple extension-based classification
- `MarkdownParser` - Full parsing with graph materialization
- `MarkdownLinkAnalyzer` - Single-file analysis example
