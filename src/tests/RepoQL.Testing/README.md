# RepoQL.Testing Playbook

## 1. Overview
`RepoQL.Testing` supplies reusable fixtures for format authors and indexing engineers. Use these helpers to avoid bespoke file-system scaffolding and to keep new formats aligned with the standard RepoQL pipeline (classifier → parser → analyzer → catalog → repo index → vector).

## 2. Common Decision Points
| Question | Recommended Practice |
| --- | --- |
| **How do I provide a complete file for my parser?** | Embed the sample file as an `EmbeddedResource` and load it via `ResourceLoader.ReadString("Namespace.Path.sample.md")`. Never hit the file system during tests. |
| **Should tests write to disk?** | No. Use `IndexingTestItemBuilder` for per-file metadata and `DuckDbTestStore` for persistence; both operate entirely in-memory. |
| **Where do I assert catalog / commit behavior?** | Use `IndexingEngineTestFactory` plus `CatalogInvocationPlan` / `PipelineInvocationPlan`; they cover incremental catalog decisions without manual mocks. |
| **How do I verify post-index artifacts (graph, embeddings)?** | Seed `DuckDbTestStore`, run the code under test, then assert via `GraphAssertionHarness`. |
| **How are multi-file analyzers tested?** | Use the idle pipeline helpers: enqueue work, await `AwaitHotPathIdleAsync()`, then verify multi-file analysis via your fake analyzer or graph assertions. |

## 3. Format Harness Patterns
All format suites should supply three tests: headline/summary/structure fidelity, parser correctness, analyzer correctness. Each test should load input from embedded resources.

### 3.1 Headline / Summary / Structure
Goal: prove the format produces deterministic headline/summary/structure for a representative file.

```csharp
[EmbeddedResource("RepoQL.Formats.Markdown.Tests.Resources.Sample.md")]
public async Task Markdown_Format_Produces_Complete_XRay()
{
    var markdown = ResourceLoader.ReadString(
        typeof(MarkdownIntegrationTests),
        "RepoQL.Formats.Markdown.Tests.Resources.Sample.md");

    var harness = FormatTestHarness.Create()
        .WithClassifier(new MarkdownClassifier(CreateLogger<MarkdownClassifier>()))
        .WithParser(new MarkdownParser(new MarkdownLoader(CreateLogger<MarkdownLoader>()), CreateLogger<MarkdownParser>()))
        .Build();

    var result = await harness.ProcessFileAsync("docs/sample.md", markdown);

    result.Should()
        .HaveSucceeded()
        .WithMediaType("markdown.doc")
        .WithRecords()
        .WithNodes("md_heading", expectedCount: 6)
        .WithAnnotationCount(expectedCount: 0);

    // Snapshot headline + summary for regression (inline string literal keeps it deterministic).
    var headline = MarkdownHeadlineFormatter.Create(result.Item);
    headline.Should().Be("sample.md | markdown.doc | size:1.5 KB | headings:6 links:4");

    var summary = MarkdownSummaryFormatter.Create(result.Item);
    summary.Should().Contain("Sections: Introduction, Usage, FAQ");
}
```

### 3.2 Parser Edge Case
Goal: confirm the parser tolerates malformed sections and the analyzer reports precise diagnostics.

```csharp
[EmbeddedResource("RepoQL.Formats.Markdown.Tests.Resources.BrokenLink.md")]
public async Task Analyzer_Flags_Broken_Link_With_Metadata()
{
    var markdown = ResourceLoader.ReadString(
        typeof(MarkdownIntegrationTests),
        "RepoQL.Formats.Markdown.Tests.Resources.BrokenLink.md");

    var harness = FormatTestHarness.Create()
        .WithParser(new MarkdownParser(new MarkdownLoader(CreateLogger<MarkdownLoader>()), CreateLogger<MarkdownParser>()))
        .WithAnalyzer(new MarkdownAnalysisProcessor(new MarkdownAnalyzer(), CreateLogger<MarkdownAnalysisProcessor>()))
        .Build();

    var result = await harness.ProcessFileAsync("docs/broken.md", markdown);

    result.Should()
        .HaveSucceeded()
        .WithAnnotationCount(1)
        .WithAnnotationContaining("link target missing");

    var annotation = result.Annotations.Single();
    annotation.Kind.Should().Be("markdown.link");
    annotation.Severity.Should().Be("warning");
    annotation.ScopeDocumentId.Should().NotBe(Guid.Empty);
}
```

### 3.3 Analyzer Output Contract
Use embedded fixtures for expected analyzer output and assert on annotation count / severity.

## 4. Indexing Contract Tests
Every format must also prove that indexing honors catalog decisions, pipeline ordering, and idle orchestration.

### 4.1 Catalog Incrementality
```csharp
[Test]
public async Task Catalog_Skips_Unchanged_Files()
{
    var catalog = A.Fake<IDocumentCatalog>();
    A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
        .Returns(new DocumentCatalogEvaluation(DocumentCatalogDecision.SkipUpToDate, null));

    var context = IndexingEngineTestFactory.Create(b => b.WithCatalog(catalog));
    var item = IndexingTestItemFactory.CreateIndexItem("file:///repo/sample.md");

    await context.Engine.IndexItemAsync(item, CancellationToken.None);

    catalog.ShouldMatch(item.Uri, CatalogInvocationPlan.SkipProcessing);
    context.ShouldMatchPipeline(item, PipelineInvocationPlan.ShortCircuitAfterClassifier);
}
```

### 4.2 Hot Path + Idle Sequencing
```csharp
[Test]
public async Task Engine_Signals_Started_And_Idle()
{
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var classifier = A.Fake<ClassificationPipeline>();
    A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
        .ReturnsLazily(async _ => { await gate.Task; return PipelineResult.Success; });

    var context = IndexingEngineTestFactory.Create(b => b.WithClassifier(classifier));
    var item = IndexingTestItemFactory.CreateIndexItem();

    var started = context.Engine.WaitForAsync(IndexingState.Started, CancellationToken.None).AsTask();
    var indexing = context.Engine.IndexItemAsync(item, CancellationToken.None);

    await started;
    gate.TrySetResult(true);
    await indexing;
}
```

## 5. Post-Index & Graph Tests
After the hot path drains, formats should prove that graph rows and embeddings exist as expected.

### 5.1 Prune + Delete + Vector
```csharp
[Test]
public async Task Pruner_Detects_Stale_Document()
{
    using var store = DuckDbTestStore.CreateInMemory();
    store.SeedDocument("file:///repo/live.md");
    store.SeedDocument("file:///repo/stale.md");

    var pruner = new StorageBackedArtifactPruner(
        new SingleConnectionFactory(store.Connection),
        () => false,
        NullLogger<StorageBackedArtifactPruner>.Instance);
    var pending = new[] { IndexingTestItemBuilder.ForFile("file:///repo/live.md").WithContent("text").Build() };

    var result = await pruner.PruneAsync(pending, CancellationToken.None);
    result.DeletedArtifacts.Should().ContainSingle(uri => uri.AbsoluteUri == "file:///repo/stale.md");
}
```

### 5.2 Repo Index / Embedding Verification
```csharp
[Test]
public void Commit_Writes_RepoIndex_And_Embeddings()
{
    using var store = DuckDbTestStore.CreateInMemory();
    store.SeedDocument("file:///repo/doc.md", mediaType: "text/markdown;kind=markdown.doc");

    var graph = new GraphAssertionHarness(store);
    graph.Nodes.ShouldContainDocument("file:///repo/doc.md");
    graph.RepoIndex.ShouldContainEntry("file:///repo/doc.md");
    graph.Embeddings.ShouldHaveScope("file:///repo/doc.md", "document");
}
```

## 6. Visual Reference
```mermaid
flowchart LR
  F["Format harness"] --> P{{"Pipelines"}}
  P --> C["Catalog"]
  C --> W["Writer"]
  W --> G["DuckDb store"]
  G --> R["Repo index macros"]
  %% MEANING: Test data flows from format harness to graph assertions; each stage has a dedicated helper in RepoQL.Testing.
```

## 7. Summary Checklist
- [ ] Embedded resources for all parser inputs.
- [ ] Format harness tests for success + analyzer warnings.
- [ ] IndexingEngine tests covering skip + reindex paths.
- [ ] Post-index tests exercising pruner/vector coordination.
- [ ] Graph assertions verifying graph/embedding output.
- [ ] No file-system IO in tests.

Use these patterns verbatim when bringing a new format online; extend only when the format introduces new data planes (e.g., additional graph nodes). This keeps every format consistent and ensures the semantic macros light up as soon as the format lands.
