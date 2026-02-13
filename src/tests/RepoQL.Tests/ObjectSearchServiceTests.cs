using System.Text.Json.Nodes;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Search;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

internal sealed class ObjectSearchServiceTests
{
    [Test]
    public async Task SearchInDocumentsAsync_WithFragmentScopedUris_FiltersByResolvedDocumentIds()
    {
        using var context = new ObjectSearchTestContext();

        const string alphaDoc = "file:///src/Alpha.cs";
        const string betaDoc = "file:///src/Beta.cs";

        context.SeedDocumentWithObjects(
            alphaDoc,
            "text/plain;kind=code.csharp",
            ("Namespace.AlphaService.Configure", 20, 30),
            ("Namespace.AlphaService.Run", 5, 12));

        context.SeedDocumentWithObjects(
            betaDoc,
            "text/plain;kind=code.csharp",
            ("Namespace.BetaService.Run", 7, 10));

        var service = new ObjectSearchService(context.Store);
        var result = await service.SearchInDocumentsAsync(
            [alphaDoc + "#line=1,200", betaDoc + "#symbol=Namespace.BetaService.Run"],
            question: null,
            objectsPerDocument: 1,
            CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(match =>
            match.DocumentUri == alphaDoc || match.DocumentUri == betaDoc);

        var alphaMatch = result.Single(match => match.DocumentUri == alphaDoc);
        var betaMatch = result.Single(match => match.DocumentUri == betaDoc);

        alphaMatch.LineStart.Should().Be(5);
        betaMatch.LineStart.Should().Be(7);
    }

    private sealed class ObjectSearchTestContext : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public ObjectSearchTestContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new RepositoryConfiguration { Path = "/repo" });
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());
            services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
            services.AddSingleton<IMcpToolCaller?>(_ => null);

            _serviceProvider = services.BuildServiceProvider();
            Store = new DuckDbDataStore(":memory:", serviceProvider: _serviceProvider);
        }

        public DuckDbDataStore Store { get; }

        public void SeedDocumentWithObjects(
            string uri,
            string mediaType,
            params (string Symbol, int StartLine, int EndLine)[] objects)
        {
            var documentUri = RepoUri.Parse(uri) ?? throw new InvalidOperationException("Failed to parse document URI.");
            var artifactId = Guid.NewGuid();
            var documentId = Guid.NewGuid();
            var children = new List<Node>();
            var spans = new List<Span>();
            var edges = new List<Edge>();

            for (var i = 0; i < objects.Length; i++)
            {
                var (symbol, startLine, endLine) = objects[i];
                var spanId = Guid.NewGuid();
                var childId = Guid.NewGuid();
                var symbolName = GetSymbolName(symbol);

                children.Add(new Node
                {
                    Id = childId,
                    Kind = "csharp.member",
                    Uri = RepoUri.FromSymbol(documentUri.Container, symbol, startLine, endLine),
                    SpanId = spanId,
                    Props = new JsonObject
                    {
                        ["name"] = symbolName,
                        ["kind"] = "method",
                        ["qualified_name"] = symbol
                    },
                    Headline = symbolName
                });

                spans.Add(new Span
                {
                    Id = spanId,
                    DocumentId = documentId,
                    StartLine = startLine,
                    EndLine = endLine,
                    StartColumn = 1,
                    EndColumn = 1
                });

                edges.Add(new Edge
                {
                    SrcId = documentId,
                    DstId = childId,
                    Type = "HAS_PART",
                    IsComposition = true,
                    Ordinal = i
                });
            }

            Store.IndexArtifact(new ParsedArtifact
            {
                Artifact = new ArtifactModel
                {
                    Id = artifactId,
                    Digest = Guid.NewGuid().ToString("N"),
                    Size = 256,
                    MediaType = SemanticMediaType.Parse(mediaType),
                    Text = string.Join(Environment.NewLine, objects.Select(o => o.Symbol)),
                    Headline = GetSymbolName(objects[0].Symbol),
                    Summary = "Object search test document",
                    Structure = "Object search test document"
                },
                DocumentNode = new Node
                {
                    Id = documentId,
                    Kind = "document",
                    Uri = documentUri,
                    ArtifactId = artifactId,
                    Props = new JsonObject()
                },
                Children = children,
                Spans = spans,
                Edges = edges
            });
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }

        private static string GetSymbolName(string symbol)
        {
            var dotIndex = symbol.LastIndexOf(".", StringComparison.Ordinal);
            return dotIndex >= 0 && dotIndex < symbol.Length - 1
                ? symbol[(dotIndex + 1)..]
                : symbol;
        }

        private sealed class DisabledEmbeddingProvider : IEmbeddingProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";
            public int Dimension => 384;

            public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        }

        private sealed class DisabledLlmProvider : ILlmProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";

            public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult(new LlmSummaryResult("LLM disabled in tests"));

            public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
        }
    }
}
