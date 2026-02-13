using System.Text.Json.Nodes;
using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public class UriFragmentDocIdFilteringTests
{
    [Test]
    public void HybridObjectCandidates_WithFragmentUris_FiltersByResolvedDocIds()
    {
        using var store = TestServiceCollectionExtensions.CreateTestDataStore();

        SeedDocumentWithObjects(
            store,
            "file:///src/Alpha.cs",
            ("Namespace.AlphaService.Run", 5, 12));

        SeedDocumentWithObjects(
            store,
            "file:///tests/Beta.cs",
            ("Namespace.BetaService.Run", 7, 10));

        var rows = store.Read(
            """
            SELECT document_uri, uri
            FROM hybrid_object_candidates(
                ['file:///src/Alpha.cs#symbol=Namespace.AlphaService.Run', 'file:///tests/Beta.cs#line=1,20']::VARCHAR[],
                keywords := 'service',
                max_per_doc := 10
            )
            ORDER BY document_uri, uri
            """,
            r => (DocumentUri: r.GetString(0), Uri: r.GetString(1)));

        rows.Should().HaveCount(2);
        rows.Select(r => r.DocumentUri).Should().Equal("file:///src/Alpha.cs", "file:///tests/Beta.cs");
    }

    [Test]
    public void SearchSymbol_WithFragmentUriList_FiltersByResolvedDocIds()
    {
        using var store = TestServiceCollectionExtensions.CreateTestDataStore();

        SeedDocumentWithObjects(
            store,
            "file:///src/Alpha.cs",
            ("Namespace.AlphaService.Run", 5, 12));

        SeedDocumentWithObjects(
            store,
            "file:///tests/Beta.cs",
            ("Namespace.BetaService.Run", 7, 10));

        var rows = store.Read(
            """
            SELECT uri
            FROM search_symbol(
                '',
                uris := ['file:///src/Alpha.cs#symbol=Namespace.AlphaService.Run']::VARCHAR[],
                k := 20
            )
            ORDER BY uri
            """,
            r => r.GetString(0));

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(uri => uri.StartsWith("file:///src/Alpha.cs#", StringComparison.Ordinal));
    }

    private static void SeedDocumentWithObjects(
        DuckDbDataStore store,
        string documentUri,
        params (string Symbol, int StartLine, int EndLine)[] objects)
    {
        var uri = RepoUri.Parse(documentUri) ?? throw new InvalidOperationException("Failed to parse document URI.");
        var artifactId = Guid.NewGuid();
        var docId = Guid.NewGuid();
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
                Uri = RepoUri.FromSymbol(uri.Container, symbol, startLine, endLine),
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
                DocumentId = docId,
                StartLine = startLine,
                EndLine = endLine,
                StartColumn = 1,
                EndColumn = 1
            });

            edges.Add(new Edge
            {
                SrcId = docId,
                DstId = childId,
                Type = "HAS_PART",
                IsComposition = true,
                Ordinal = i
            });
        }

        store.IndexArtifact(new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = Guid.NewGuid().ToString("N"),
                Size = 128,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = string.Join(Environment.NewLine, objects.Select(o => o.Symbol)),
                Headline = GetSymbolName(objects[0].Symbol),
                Summary = "URI fragment doc-id test document",
                Structure = "URI fragment doc-id test document"
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Props = new JsonObject()
            },
            Children = children,
            Spans = spans,
            Edges = edges
        });
    }

    private static string GetSymbolName(string symbol)
    {
        var dotIndex = symbol.LastIndexOf(".", StringComparison.Ordinal);
        return dotIndex >= 0 && dotIndex < symbol.Length - 1
            ? symbol[(dotIndex + 1)..]
            : symbol;
    }
}
