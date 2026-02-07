using System.Text.Json.Nodes;
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

internal sealed class DocumentSearchServiceTests
{
    [Test]
    public async Task SearchAsync_WithFileScope_ReturnsOnlyFilesUnderScope()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Program.cs", "text/plain;kind=code.csharp", now.AddMinutes(-2), "Program");
        context.SeedDocument("file:///src/RepoQL.Explore/ExploreOrchestrator.cs", "text/plain;kind=code.csharp", now.AddMinutes(-1), "Explore");
        context.SeedDocument("file:///docs/README.md", "text/markdown;kind=markdown.doc", now, "Docs");
        context.SeedDocument("help:///quickstart.md", "text/markdown;kind=markdown.doc", now, "Help");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync("file:///src/**", question: null, limit: 20, CancellationToken.None);

        result.Documents.Should().HaveCount(2);
        result.Documents.Should().OnlyContain(d =>
            d.Uri.StartsWith("file:///src/", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SearchAsync_ScopeInventory_PrioritizesCodeOverVendorAndDocs()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        // Vendor and docs are newer, but code should still rank first for inventory.
        context.SeedDocument(
            "file:///src/RepoQL.Web/wwwroot/lib/bootstrap/bootstrap.bundle.min.js.map",
            "application/json;kind=json.doc",
            now,
            "Bootstrap map");

        context.SeedDocument(
            "file:///src/RepoQL.Documentation/repoql/tools/query/functions/tree.md",
            "text/markdown;kind=markdown.doc",
            now.AddMinutes(-1),
            "Tree docs");

        context.SeedDocument(
            "file:///src/RepoQL.ConsoleApp/Tools/ExploreTool.cs",
            "text/plain;kind=code.csharp",
            now.AddMinutes(-2),
            "Explore tool");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync("file:///src/**", question: null, limit: 10, CancellationToken.None);

        result.Documents.Should().NotBeEmpty();
        result.Documents[0].Uri.Should().Be("file:///src/RepoQL.ConsoleApp/Tools/ExploreTool.cs");
    }

    private sealed class DocumentSearchTestContext : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public DocumentSearchTestContext()
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
        }

        public UriRegistry Registry { get; }
        public DuckDbDataStore Store { get; }

        public void SeedDocument(string uri, string mediaType, DateTimeOffset updatedAt, string headline)
        {
            var documentUri = RepoUri.Parse(uri);
            var artifact = new ArtifactModel
            {
                Id = Guid.NewGuid(),
                Digest = Guid.NewGuid().ToString("N"),
                Size = headline.Length,
                MediaType = SemanticMediaType.Parse(mediaType),
                Text = headline,
                Headline = headline,
                Summary = headline,
                Structure = headline
            };

            var documentNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = documentUri,
                ArtifactId = artifact.Id,
                Props = new JsonObject(),
                Headline = headline,
                Structure = headline,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            };

            Store.IndexArtifact(new ParsedArtifact
            {
                Artifact = artifact,
                DocumentNode = documentNode
            });

            Registry.SetIndexed(documentUri, lineCount: 1, new Dictionary<RepoUri, SymbolEntry>());
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }
    }
}
