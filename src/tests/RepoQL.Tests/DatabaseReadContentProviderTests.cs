using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

internal sealed class DatabaseReadContentProviderTests
{
    [Test]
    public async Task FetchGlobAsync_DoesNotTruncateRepositoryTreeInputs()
    {
        using var context = new ReadContentProviderTestContext();

        for (var i = 0; i < 120; i++)
        {
            context.SeedDocument($"file:///.github/workflows/workflow-{i:D3}.yml");
        }

        context.SeedDocument("file:///src/Program.cs");
        context.SeedDocument("file:///docs/README.md");
        context.SeedDocument("file:///integrations/mcp.json");

        var documents = await context.Provider.FetchGlobAsync("file://**", CancellationToken.None);

        documents.Should().HaveCount(123);
        documents.Select(d => d.Uri).Should().Contain("file:///src/Program.cs");
        documents.Select(d => d.Uri).Should().Contain("file:///docs/README.md");
        documents.Select(d => d.Uri).Should().Contain("file:///integrations/mcp.json");

        var foldersTree = await context.Provider.FormatAsTreeAsync(
            documents.Select(d => d.Uri).ToList(),
            foldersOnly: true,
            includeHeadlines: false,
            CancellationToken.None);

        foldersTree.Should().NotBeNull();
        foldersTree!.Should().Contain(".github/");
        foldersTree.Should().Contain("src/");
        foldersTree.Should().Contain("docs/");
        foldersTree.Should().Contain("integrations/");
    }

    [Test]
    public async Task FetchGlobAsync_PlainAnchor_ResolvesMarkdownHeadingSectionWithoutHeadingUri()
    {
        using var context = new ReadContentProviderTestContext();

        context.SeedMarkdownHeadingWithoutUri(
            documentUri: "file:///docs/north-star/formats.md",
            slug: "legibility",
            headingText: "Legibility",
            startLine: 2,
            endLine: 4,
            text: """
                Intro
                ## Legibility
                Keep it simple
                Durable by default
                ## Other
                Not this section
                """);

        var documents = await context.Provider.FetchGlobAsync(
            "file:///docs/north-star/formats.md#legibility",
            CancellationToken.None);

        documents.Should().HaveCount(1);
        documents[0].Uri.Should().Be("file:///docs/north-star/formats.md#legibility");
        documents[0].TextContent.Should().Be(
            "## Legibility\nKeep it simple\nDurable by default");
    }

    private sealed class ReadContentProviderTestContext : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public ReadContentProviderTestContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new RepositoryConfiguration { Path = "/repo" });
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IEmbeddingProvider?>(_ => null);
            services.AddSingleton<ILlmProvider?>(_ => null);
            services.AddSingleton<IMcpToolCaller?>(_ => null);

            _serviceProvider = services.BuildServiceProvider();
            Registry = _serviceProvider.GetRequiredService<UriRegistry>();
            Store = new DuckDbDataStore(":memory:", serviceProvider: _serviceProvider);
            Provider = new DatabaseReadContentProvider(Store);
        }

        public UriRegistry Registry { get; }
        public DuckDbDataStore Store { get; }
        public DatabaseReadContentProvider Provider { get; }

        public void SeedDocument(string uri)
        {
            var documentUri = RepoUri.Parse(uri);
            var artifact = new ArtifactModel
            {
                Id = Guid.NewGuid(),
                Digest = Guid.NewGuid().ToString("N"),
                Size = 16,
                MediaType = SemanticMediaType.Parse("text/plain"),
                Text = "seed text",
                Headline = "seed"
            };

            var documentNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = documentUri,
                ArtifactId = artifact.Id,
                Props = new JsonObject(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            Store.IndexArtifact(new ParsedArtifact
            {
                Artifact = artifact,
                DocumentNode = documentNode
            });

            Registry.SetIndexed(documentUri, lineCount: 1, new Dictionary<RepoUri, SymbolEntry>());
        }

        public void SeedMarkdownHeadingWithoutUri(
            string documentUri,
            string slug,
            string headingText,
            int startLine,
            int endLine,
            string text)
        {
            var docUri = RepoUri.Parse(documentUri);
            var now = DateTimeOffset.UtcNow;

            var artifact = new ArtifactModel
            {
                Id = Guid.NewGuid(),
                Digest = Guid.NewGuid().ToString("N"),
                Size = text.Length,
                MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc"),
                Text = text,
                Headline = "seed",
                Summary = "seed",
                Structure = "seed"
            };

            var documentNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = docUri,
                ArtifactId = artifact.Id,
                Props = new JsonObject(),
                CreatedAt = now,
                UpdatedAt = now
            };

            var headingSpan = new Span
            {
                Id = Guid.NewGuid(),
                DocumentId = documentNode.Id,
                StartLine = startLine,
                EndLine = endLine,
                StartColumn = 1,
                EndColumn = 1
            };

            var headingNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "md_heading",
                Uri = null, // Simulates legacy indexed markdown rows without heading URIs
                SpanId = headingSpan.Id,
                Props = new JsonObject
                {
                    ["slug"] = slug,
                    ["text"] = headingText,
                    ["level"] = 2
                },
                CreatedAt = now,
                UpdatedAt = now
            };

            var hasPart = new Edge
            {
                Id = Guid.NewGuid(),
                SrcId = documentNode.Id,
                DstId = headingNode.Id,
                Type = "HAS_PART",
                IsComposition = true,
                Ordinal = 0,
                ScopeDocumentId = documentNode.Id,
                CreatedAt = now
            };

            Store.IndexArtifact(new ParsedArtifact
            {
                Artifact = artifact,
                DocumentNode = documentNode,
                Children = [headingNode],
                Spans = [headingSpan],
                Edges = [hasPart]
            });

            // Intentionally omit heading symbols to replicate "slug exists in markdown data,
            // but URI registry cannot resolve #slug" scenarios.
            var lineCount = text.Count(c => c == '\n') + 1;
            Registry.SetIndexed(docUri, lineCount, new Dictionary<RepoUri, SymbolEntry>());
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }
    }
}
