using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;
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
    public async Task FetchGlobAsync_LineFragmentSingleLine_ReturnsRequestedLine()
    {
        using var context = new ReadContentProviderTestContext();

        context.SeedDocument(
            "file:///src/App.cs",
            """
            line one
            line two
            line three
            """);

        var documents = await context.Provider.FetchGlobAsync(
            "file:///src/App.cs#line=2",
            CancellationToken.None);

        documents.Should().HaveCount(1);
        documents[0].Uri.Should().Be("file:///src/App.cs#line=2,2");
        documents[0].TextContent.Should().Be("line two");
    }

    [Test]
    public async Task FetchGlobAsync_LineFragmentRange_ReturnsRequestedRange()
    {
        using var context = new ReadContentProviderTestContext();

        context.SeedDocument(
            "file:///src/App.cs",
            """
            line one
            line two
            line three
            line four
            """);

        var documents = await context.Provider.FetchGlobAsync(
            "file:///src/App.cs#line=2,3",
            CancellationToken.None);

        documents.Should().HaveCount(1);
        documents[0].Uri.Should().Be("file:///src/App.cs#line=2,3");
        documents[0].TextContent.Should().Be("line two\nline three");
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
            services.AddSingleton<IInferenceProvider?>(_ => null);
            services.AddSingleton<IMcpToolCaller?>(_ => null);

            _serviceProvider = services.BuildServiceProvider();
            Registry = _serviceProvider.GetRequiredService<UriRegistry>();
            Store = new DuckDbDataStore(":memory:", serviceProvider: _serviceProvider);
            Provider = new DatabaseReadContentProvider(Store);
        }

        public UriRegistry Registry { get; }
        public DuckDbDataStore Store { get; }
        public DatabaseReadContentProvider Provider { get; }

        public void SeedDocument(string uri, string text = "seed text")
        {
            var documentUri = RepoUri.Parse(uri);
            var artifact = new ArtifactModel
            {
                Id = Guid.NewGuid(),
                Digest = Guid.NewGuid().ToString("N"),
                Size = text.Length,
                MediaType = SemanticMediaType.Parse("text/plain"),
                Text = text,
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

            var lineCount = text.Count(c => c == '\n') + 1;
            Registry.SetIndexed(documentUri, lineCount, new Dictionary<RepoUri, SymbolEntry>());
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }
    }
}
