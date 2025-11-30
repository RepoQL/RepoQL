using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class IndexingFullPipelineTests
{
    [Test]
    [Timeout(120_000)]
    public async Task Reindex_ModifyAndDeleteDocuments_CompletesCleanly(CancellationToken testCancellationToken)
    {
        await using var repo = await CreateRepoAsync();

        var guideUri = repo.AddOrUpdateText("docs/guide.md", """
            # Guide

            Initial content
            """);
        var archiveUri = repo.AddOrUpdateText("docs/archive.md", """
            # Archive

            Things we no longer need
            """);

        await ReindexWithTimeoutAsync(repo, testCancellationToken);

        repo.AddOrUpdateText("docs/guide.md", """
            # Guide

            Updated body
            """);
        repo.Delete("docs/archive.md");

        await ReindexWithTimeoutAsync(repo, testCancellationToken);

        var documents = repo.Store.GetAllNodes()
            .Where(n => n.Kind == "document")
            .ToArray();

        documents.Count(n => n.Uri?.AbsoluteUri == guideUri.AbsoluteUri).Should().Be(1);
        documents.Any(n => n.Uri?.AbsoluteUri == archiveUri.AbsoluteUri).Should().BeFalse();

        var guide = repo.Store.GetDocumentByUri(guideUri);
        guide.Should().NotBeNull();
        guide!.Kind.Should().Be("document");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Reindex_PrunesMultipleDocumentsInOneSweep(CancellationToken testCancellationToken)
    {
        await using var repo = await CreateRepoAsync();

        var survivors = new (string Path, RepoUri Uri)[]
        {
            ("docs/survivor-1.md", repo.AddOrUpdateText("docs/survivor-1.md", "# Keep me")),
            ("docs/survivor-2.md", repo.AddOrUpdateText("docs/survivor-2.md", "# Keep me too")),
        };

        var doomed = new (string Path, RepoUri Uri)[]
        {
            ("docs/remove-1.md", repo.AddOrUpdateText("docs/remove-1.md", "# Remove me")),
            ("docs/remove-2.md", repo.AddOrUpdateText("docs/remove-2.md", "# Remove me as well")),
            ("docs/remove-3.md", repo.AddOrUpdateText("docs/remove-3.md", "# Remove me last"))
        };

        await ReindexWithTimeoutAsync(repo, testCancellationToken);

        foreach (var doc in doomed)
        {
            repo.Delete(doc.Path);
        }

        await ReindexWithTimeoutAsync(repo, testCancellationToken);

        var docs = repo.Store.GetAllNodes()
            .Where(n => n.Kind == "document")
            .Select(n => n.Uri?.AbsoluteUri)
            .ToArray();

        foreach (var doc in survivors)
        {
            docs.Should().Contain(doc.Uri.AbsoluteUri);
        }

        foreach (var doc in doomed)
        {
            docs.Should().NotContain(doc.Uri.AbsoluteUri);
        }

        docs.Length.Should().Be(survivors.Length);
    }

    [Test]
    [Skip("PlainTextParser is internal - test fallback behavior via integration tests with full indexer")]
    public async Task PlainTextDocuments_AreIngestedByFallbackFormat()
    {
        await using var repo = await CreateRepoAsync();

        var notes = repo.AddOrUpdateText("notes/todo.txt", "first item\nsecond item");
        await repo.IndexAsync();

        var document = repo.Store.GetDocumentByUri(notes);
        document.Should().NotBeNull();
        document!.ArtifactId.Should().NotBeNull();

        var artifact = repo.Store.GetArtifact(document.ArtifactId!.Value);
        artifact.Should().NotBeNull();
        artifact!.Headline.Should().NotBeNull();
        artifact.MediaType.Should().NotBeNull();
        artifact.MediaType!.Type.Should().Be("text");
        artifact.MediaType!.Subtype.Should().Be("plain");
    }

    private static Task<IndexedRepoBuilder> CreateRepoAsync()
        => IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.IndexingFullPipeline";
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
        });

    private static void ConfigureFormats(IndexedRepoOptions options)
    {
        options.AddMarkdownFormat();
    }

    private static async Task ReindexWithTimeoutAsync(IndexedRepoBuilder repo, CancellationToken testCancellationToken, int timeoutSeconds = 30)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await repo.ReindexAsync(cancellationToken: cts.Token);
    }
}
