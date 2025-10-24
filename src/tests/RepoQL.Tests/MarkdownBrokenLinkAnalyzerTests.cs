using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Data.DuckDB;
using RepoQL.Formats.Markdown;

namespace RepoQL.Tests;

internal class MarkdownBrokenLinkAnalyzerTests
{
    [Test]
    public async Task Analyzer_FlagsMissingAnchor_InSameDocument()
    {
        const string content = "# Heading\n\nSee [broken](#missing-anchor).\n";
        var uri = RepoUri.Parse("file:///repo/doc.md");

        using var store = new DuckDbGraphStore(":memory:");
        store.EnsureSchema();

        var markdownLoader = new MarkdownLoader();
        var markdownAnalyzer = new MarkdownAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));
        var registry = new FormatRegistry([
            new FormatDescriptor(SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"), markdownLoader, markdownAnalyzer, markdownLoader,
                ["markdown"]),
            new FormatDescriptor(SemanticMediaType.Create("text", "plain").WithKind("plain.document"), plainLoader, plainAnalyzer, plainLoader)
        ]);
        var documents = new Dictionary<string, DocumentModel>(StringComparer.OrdinalIgnoreCase);

        var document = await IndexMarkdownAsync(store, markdownLoader, uri, content);
        documents[uri.AbsoluteUri.ToLowerInvariant()] = document;

        var workspace = new DictionaryWorkspace(documents, registry);

        var context = new AnalyzerContext(new AnalyzerSettings(new Dictionary<string, AnalyzerRuleSettings>
        {
            ["markdown/broken-link"] = new() { RuleId = "markdown/broken-link", Severity = AnalysisSeverity.Warning }
        }), "/repo", registry, workspace);

        var results = new List<AnalysisResult>();
        await foreach (var result in markdownAnalyzer.AnalyzeAsync(document, context, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].Message.Should().Contain("not found");
    }

    [Test]
    public async Task Analyzer_FlagsMissingAnchor_InTargetDocument()
    {
        const string source = "# Source\n\nSee [link](other.md#missing).\n";
        const string target = "# Target\n";

        var sourceUri = RepoUri.Parse("file:///repo/source.md");
        var targetUri = RepoUri.Parse("file:///repo/other.md");

        using var store = new DuckDbGraphStore(":memory:");
        store.EnsureSchema();

        var markdownLoader = new MarkdownLoader();
        var markdownAnalyzer = new MarkdownAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));
        var registry = new FormatRegistry([
            new FormatDescriptor(SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"), markdownLoader, markdownAnalyzer, markdownLoader,
                ["markdown"]),
            new FormatDescriptor(SemanticMediaType.Create("text", "plain").WithKind("plain.document"), plainLoader, plainAnalyzer, plainLoader)
        ]);
        var documents = new Dictionary<string, DocumentModel>(StringComparer.OrdinalIgnoreCase);

        var sourceDoc = await IndexMarkdownAsync(store, markdownLoader, sourceUri, source);
        documents[sourceUri.AbsoluteUri.ToLowerInvariant()] = sourceDoc;
        var targetDoc = await IndexMarkdownAsync(store, markdownLoader, targetUri, target);
        documents[targetUri.AbsoluteUri.ToLowerInvariant()] = targetDoc;

        var workspace = new DictionaryWorkspace(documents, registry);

        var context = new AnalyzerContext(new AnalyzerSettings(new Dictionary<string, AnalyzerRuleSettings>
        {
            ["markdown/broken-link"] = new() { RuleId = "markdown/broken-link", Severity = AnalysisSeverity.Warning }
        }), "/repo", registry, workspace);

        var results = new List<AnalysisResult>();
        await foreach (var result in markdownAnalyzer.AnalyzeAsync(sourceDoc, context, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].Message.Should().Contain("other.md");
    }

    private static async Task<DocumentModel> IndexMarkdownAsync(DuckDbGraphStore store, MarkdownLoader loader, RepoUri uri, string content)
    {
        var fileName = Path.GetFileName(uri.AbsolutePath);
        var fileInfo = new StringFileInfo(fileName, content);
        var hasher = new XxHasher();
        var hash = await hasher.HashAsync(fileInfo, CancellationToken.None);
        var artifact = new DiscoveredArtifact
        {
            File = fileInfo,
            RepoUri = uri,
            Hash = hash,
            MediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc")
        };

        if (!await loader.CanLoadAsync(artifact, CancellationToken.None))
            throw new InvalidOperationException("Parser rejected markdown content");
        var document = await loader.LoadAsync(artifact, CancellationToken.None);
        var records = loader.Materialize(document);

        var artifactIdMap = new Dictionary<Guid, Guid>();
        foreach (var a in records.Artifacts)
        {
            var saved = store.UpsertArtifact(a);
            artifactIdMap[a.Id] = saved.Id;
        }

        var docRec = records.Nodes.Single(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        var doc = new Node
        {
            Id = docRec.Id,
            Kind = docRec.Kind,
            Uri = uri,
            ArtifactId = artifactIdMap.TryGetValue(docRec.ArtifactId ?? Guid.Empty, out var mapped) ? mapped : docRec.ArtifactId,
            Props = docRec.Props,
            CreatedAt = docRec.CreatedAt,
            UpdatedAt = docRec.UpdatedAt
        };
        doc = store.UpsertDocumentByUri(uri, doc);

        var children = new List<Node>();
        foreach (var n in records.Nodes.Where(n => n.Kind != "document"))
        {
            if (n.ArtifactId is { } aid && artifactIdMap.TryGetValue(aid, out var mappedId))
            {
                children.Add(new Node
                {
                    Id = n.Id,
                    Kind = n.Kind,
                    Uri = n.Uri,
                    ArtifactId = mappedId,
                    SpanId = n.SpanId,
                    Props = n.Props,
                    CreatedAt = n.CreatedAt,
                    UpdatedAt = n.UpdatedAt
                });
            }
            else
            {
                children.Add(n);
            }
        }

        var spans = records.Spans.Select(s => new Span
        {
            Id = s.Id,
            DocumentId = doc.Id,
            StartByte = s.StartByte,
            EndByte = s.EndByte,
            StartLine = s.StartLine,
            StartColumn = s.StartColumn,
            EndLine = s.EndLine,
            EndColumn = s.EndColumn
        }).ToArray();

        var edges = records.Edges.Select(e => new Edge
        {
            Id = e.Id,
            SrcId = e.SrcId,
            DstId = e.DstId,
            Type = e.Type,
            IsComposition = e.IsComposition,
            Ordinal = e.Ordinal,
            ScopeDocumentId = doc.Id,
            EdgeKey = e.EdgeKey,
            SrcSpanId = e.SrcSpanId,
            DstSpanId = e.DstSpanId,
            Props = e.Props,
            CreatedAt = e.CreatedAt
        }).ToArray();

        store.ReplaceDocumentContent(doc.Id, children, spans, edges);
        return document;
    }

    private sealed class DictionaryWorkspace(IReadOnlyDictionary<string, DocumentModel> documents, IFormatRegistry registry) : IAnalysisWorkspace
    {
        private readonly IReadOnlyDictionary<string, DocumentModel> _documents = documents;
        private readonly IFormatRegistry _registry = registry;

        public Task<DocumentModel?> LoadAsync(RepoUri uri, CancellationToken cancellationToken = default)
        {
            var key = uri.AbsoluteUri.ToLowerInvariant();
            _documents.TryGetValue(key, out var doc);
            return Task.FromResult<DocumentModel?>(doc);
        }

        public async Task<IReadOnlyList<EmbeddedFragment>> DiscoverEmbedsAsync(DocumentModel document, CancellationToken cancellationToken = default)
        {
            if (!_registry.TryResolveByMedia(document.MediaType, out var descriptor))
                return [];

            var list = new List<EmbeddedFragment>();
            await foreach (var fragment in descriptor.Loader.DiscoverEmbedsAsync(document, cancellationToken).ConfigureAwait(false))
            {
                list.Add(fragment);
            }
            return list;
        }
    }
}
