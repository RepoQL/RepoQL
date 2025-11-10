using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DuckDB.NET.Data;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class DuckDbGraphStoreTests : IDisposable
{
    private readonly DuckDBConnection connection;
    private readonly DuckDbGraphStore store;
    private readonly IndexingMetrics metrics;

    public DuckDbGraphStoreTests()
    {
        // Use in-memory database for tests
        connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        metrics = new IndexingMetrics();
        // Create store and use production schema
        store = new DuckDbGraphStore(connection, metrics);
        // Use production schema - this ensures tests match production behavior
        store.EnsureSchema();
    }

    public void Dispose()
    {
        store.Dispose();
        metrics.Dispose();
        connection.Dispose();
    }

    // ========== Schema Tests ==========

    [Test]
    public Task EnsureSchema_CreatesAllTables()
    {
        // Act - schema already created in constructor

        // Assert - verify tables exist
        var tables = GetTableNames();

        tables.Should().Contain("artifact");
        tables.Should().Contain("node");
        tables.Should().Contain("span");
        tables.Should().Contain("edge");
        return Task.CompletedTask;
    }

    [Test]
    public Task Schema_IsIdempotent()
    {
        // Act - call EnsureSchema multiple times
        store.EnsureSchema();
        store.EnsureSchema();

        // Assert - should not throw and tables should still exist
        var tables = GetTableNames();
        tables.Count.Should().BeGreaterThanOrEqualTo(4);
        return Task.CompletedTask;
    }

    [Test]
    public Task Schema_CreatesIndexes()
    {
        // Assert - verify key indexes exist
        var indexes = GetIndexNames();

        // Check for our test schema indexes
        indexes.Should().Contain("node_container_uri_lowercase_unique");
        indexes.Should().Contain("edge_semantic_key_unique");
        indexes.Should().Contain("edge_composition_single_parent");
        indexes.Should().Contain("edge_source_idx");
        indexes.Should().Contain("edge_destination_idx");
        return Task.CompletedTask;
    }

    // ========== Artifact Tests ==========

    [Test]
    public Task UpsertArtifact_InsertsNewArtifact()
    {
        // Arrange
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:abc123",
            Size = 1024,
            MediaType = SemanticMediaType.Parse("text/plain; charset=utf-8"),
            Text = "Hello World",
            StoreUri = "file:///data/abc123"
        };

        // Act
        var result = store.UpsertArtifact(artifact);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(artifact.Id);
        result.Digest.Should().Be(artifact.Digest);
        result.Size.Should().Be(artifact.Size);
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertArtifact_ReturnsExistingForDuplicateDigest()
    {
        // Arrange
        var artifact1 = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:duplicate",
            Size = 100,
            Text = "Original"
        };

        var artifact2 = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:duplicate",
            Size = 200,
            Text = "Different"
        };

        // Act
        var result1 = store.UpsertArtifact(artifact1);
        var result2 = store.UpsertArtifact(artifact2);

        // Assert
        result2.Id.Should().Be(result1.Id);
        result2.Text.Should().Be("Original");
        result2.Size.Should().Be(100);
        return Task.CompletedTask;
    }

    [Test]
    public Task GetArtifactByDigest_ReturnsNullForNonExistent()
    {
        // Act
        var result = store.GetArtifactByDigest("sha256:nonexistent");

        // Assert
        result.Should().BeNull();
        return Task.CompletedTask;
    }

    [Test]
    public Task GetArtifactByDigest_ReturnsArtifactWithMediaType()
    {
        // Arrange
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:mediatype",
            Size = 512,
            MediaType = SemanticMediaType.Parse("application/json; kind=config.app"),
            Text = "{}"
        };
        store.UpsertArtifact(artifact);

        // Act
        var result = store.GetArtifactByDigest("sha256:mediatype");

        // Assert
        result.Should().NotBeNull();
        result!.MediaType.Should().NotBeNull();
        result.MediaType!.Type.Should().Be("application");
        result.MediaType.Subtype.Should().Be("json");
        result.MediaType.Kind.Should().Be("config.app");
        return Task.CompletedTask;
    }

    // ========== Node Tests ==========

    [Test]
    public Task UpsertNode_InsertsNewNode()
    {
        // Arrange
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "test",
            Props = new JsonObject { ["name"] = "Test Node" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var result = store.UpsertNode(node);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(node.Id);
        result.Kind.Should().Be("test");
        result.Props["name"]?.GetValue<string>().Should().Be("Test Node");
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertNode_UpdatesExistingNode()
    {
        // Arrange
        var nodeId = Guid.NewGuid();
        var node1 = new Node
        {
            Id = nodeId,
            Kind = "original",
            Props = new JsonObject { ["version"] = 1 },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var node2 = new Node
        {
            Id = nodeId,
            Kind = "updated",
            Props = new JsonObject { ["version"] = 2 },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        // Act
        store.UpsertNode(node1);
        store.UpsertNode(node2);

        // Assert
        var fetched = store.GetNode(nodeId);
        fetched.Should().NotBeNull();
        fetched!.Kind.Should().Be("updated");
        fetched.Props["version"]?.GetValue<int>().Should().Be(2);
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertNode_RequiresUriForDocumentKind()
    {
        // Arrange
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = null, // Missing required URI
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act & Assert
        Action act = () => store.UpsertNode(node);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Document node requires a non-null URI*");
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertNode_WithDocumentUri()
    {
        // Arrange
        RepoUri.TryParse("file:///src/main.cs", out var uri);
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var result = store.UpsertNode(node);

        // Assert
        result.Should().NotBeNull();
        result.Uri.Should().NotBeNull();
        result.Uri!.Container.AbsoluteUri.Should().Be("file:///src/main.cs");
        return Task.CompletedTask;
    }

    [Test]
    public Task GetDocumentByUri_FindsDocumentNode()
    {
        // Arrange
        RepoUri.TryParse("file:///src/test.cs", out var uri);
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            Props = new JsonObject { ["lang"] = "csharp" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertNode(node);

        // Act
        var result = store.GetDocumentByUri(uri!);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(node.Id);
        result.Kind.Should().Be("document");
        result.Props["lang"]?.GetValue<string>().Should().Be("csharp");
        return Task.CompletedTask;
    }

    [Test]
    public Task GetDocumentByUri_IsCaseInsensitive()
    {
        // Arrange
        RepoUri.TryParse("file:///SRC/Test.CS", out var uri);
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertNode(node);

        // Act
        RepoUri.TryParse("file:///src/test.cs", out var parsedUri);
        var result = store.GetDocumentByUri(parsedUri!);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(node.Id);
        return Task.CompletedTask;
    }

    [Test]
    public Task DeleteNode_RemovesNode()
    {
        // Arrange
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "deletable",
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertNode(node);

        // Act
        var deleted = store.DeleteNode(node.Id);

        // Assert
        deleted.Should().BeTrue();
        store.GetNode(node.Id).Should().BeNull();
        return Task.CompletedTask;
    }

    // ========== Span Tests ==========

    [Test]
    public Task InsertSpan_CreatesSpan()
    {
        // Arrange
        var docNode = CreateAndInsertDocumentNode();
        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = docNode.Id,
            StartByte = 0,
            EndByte = 100,
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10
        };

        // Act
        var result = store.InsertSpan(span);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(span.Id);
        return Task.CompletedTask;
    }

    [Test]
    public Task GetSpan_ReturnsSpan()
    {
        // Arrange
        var docNode = CreateAndInsertDocumentNode();
        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = docNode.Id,
            StartByte = 50,
            EndByte = 150,
            StartLine = 3,
            StartColumn = 5,
            EndLine = 7,
            EndColumn = 15
        };
        store.InsertSpan(span);

        // Act
        var result = store.GetSpan(span.Id);

        // Assert
        result.Should().NotBeNull();
        result!.StartByte.Should().Be(50);
        result.EndByte.Should().Be(150);
        result.StartLine.Should().Be(3);
        result.EndLine.Should().Be(7);
        return Task.CompletedTask;
    }

    [Test]
    public Task DeleteSpan_RemovesSpan()
    {
        // Arrange
        var docNode = CreateAndInsertDocumentNode();
        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = docNode.Id,
            StartByte = 0,
            EndByte = 10
        };
        store.InsertSpan(span);

        // Act
        var deleted = store.DeleteSpan(span.Id);

        // Assert
        deleted.Should().BeTrue();
        store.GetSpan(span.Id).Should().BeNull();
        return Task.CompletedTask;
    }

    // ========== Edge Tests ==========

    [Test]
    public Task UpsertEdge_InsertsNewEdge()
    {
        // Arrange
        var node1 = CreateAndInsertNode("source");
        var node2 = CreateAndInsertNode("target");
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = node1.Id,
            DstId = node2.Id,
            Type = "REFERS_TO",
            IsComposition = false,
            Props = new JsonObject { ["weight"] = 1.5 },
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var result = store.UpsertEdge(edge);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(edge.Id);
        result.Type.Should().Be("REFERS_TO");
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertEdge_WithSemanticKey_UpdatesExisting()
    {
        // Arrange
        var node1 = CreateAndInsertNode("src");
        var node2 = CreateAndInsertNode("dst1");
        var node3 = CreateAndInsertNode("dst2");

        var edge1 = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = node1.Id,
            DstId = node2.Id,
            Type = "LINK",
            EdgeKey = "unique-key",
            IsComposition = false,
            Props = new JsonObject { ["version"] = 1 },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var edge2 = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = node1.Id,
            DstId = node3.Id, // Different destination
            Type = "UPDATED_LINK",
            EdgeKey = "unique-key", // Same key
            IsComposition = false,
            Props = new JsonObject { ["version"] = 2 },
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        store.UpsertEdge(edge1);
        store.UpsertEdge(edge2);

        // Assert - should have updated the existing edge
        var edges = store.GetEdgesForNode(node1.Id).ToList();
        edges.Count.Should().Be(1);
        edges[0].DstId.Should().Be(node3.Id);
        edges[0].Type.Should().Be("UPDATED_LINK");
        return Task.CompletedTask;
    }

    [Test]
    public Task UpsertEdge_CompositionEnforcesUniqueParent()
    {
        // Arrange
        var parent1 = CreateAndInsertNode("parent1");
        var parent2 = CreateAndInsertNode("parent2");
        var child = CreateAndInsertNode("child");

        var edge1 = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = parent1.Id,
            DstId = child.Id,
            Type = "HAS_PART",
            IsComposition = true,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var edge2 = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = parent2.Id,
            DstId = child.Id,
            Type = "HAS_PART",
            IsComposition = true,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        store.UpsertEdge(edge1);
        Action act = () => store.UpsertEdge(edge2);

        // Assert - should fail due to unique composition constraint
        act.Should().Throw<Exception>();
        return Task.CompletedTask;
    }

    [Test]
    public Task GetEdgesForNode_ReturnsOutgoingEdges()
    {
        // Arrange
        var source = CreateAndInsertNode("source");
        var target1 = CreateAndInsertNode("target1");
        var target2 = CreateAndInsertNode("target2");

        CreateAndInsertEdge(source.Id, target1.Id, "EDGE1");
        CreateAndInsertEdge(source.Id, target2.Id, "EDGE2");

        // Act
        var edges = store.GetEdgesForNode(source.Id, outgoing: true, incoming: false).ToList();

        // Assert
        edges.Count.Should().Be(2);
        edges.Should().AllSatisfy(e => e.SrcId.Should().Be(source.Id));
        return Task.CompletedTask;
    }

    [Test]
    public Task GetEdgesForNode_ReturnsIncomingEdges()
    {
        // Arrange
        var target = CreateAndInsertNode("target");
        var source1 = CreateAndInsertNode("source1");
        var source2 = CreateAndInsertNode("source2");

        CreateAndInsertEdge(source1.Id, target.Id, "EDGE1");
        CreateAndInsertEdge(source2.Id, target.Id, "EDGE2");

        // Act
        var edges = store.GetEdgesForNode(target.Id, outgoing: false, incoming: true).ToList();

        // Assert
        edges.Count.Should().Be(2);
        edges.Should().AllSatisfy(e => e.DstId.Should().Be(target.Id));
        return Task.CompletedTask;
    }

    [Test]
    public Task GetEdge_ReturnsEdgeById()
    {
        // Arrange
        var node1 = CreateAndInsertNode("n1");
        var node2 = CreateAndInsertNode("n2");
        var edge = CreateAndInsertEdge(node1.Id, node2.Id, "TEST_EDGE");

        // Act
        var result = store.GetEdge(edge.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("TEST_EDGE");
        result.SrcId.Should().Be(node1.Id);
        result.DstId.Should().Be(node2.Id);
        return Task.CompletedTask;
    }

    // ========== DeleteSubtree Tests ==========

    [Test]
    public Task DeleteSubtree_RemovesNodeAndCompositionChildren()
    {
        // Arrange - create tree: root -> child1, child2; child1 -> grandchild
        var root = CreateAndInsertNode("root");
        var child1 = CreateAndInsertNode("child1");
        var child2 = CreateAndInsertNode("child2");
        var grandchild = CreateAndInsertNode("grandchild");

        CreateCompositionEdge(root.Id, child1.Id);
        CreateCompositionEdge(root.Id, child2.Id);
        CreateCompositionEdge(child1.Id, grandchild.Id);

        // Act
        var deleted = store.DeleteSubtree(root.Id);

        // Assert
        deleted.Should().Be(4); // All 4 nodes deleted
        store.GetNode(root.Id).Should().BeNull();
        store.GetNode(child1.Id).Should().BeNull();
        store.GetNode(child2.Id).Should().BeNull();
        store.GetNode(grandchild.Id).Should().BeNull();
        return Task.CompletedTask;
    }

    [Test]
    public Task DeleteNode_WithCompositionChildren_RequiresCascade()
    {
        // Arrange
        var parent = CreateAndInsertNode("parent");
        var child = CreateAndInsertNode("child");
        CreateCompositionEdge(parent.Id, child.Id);

        // Act & Assert - should fail without cascade
        Action act = () => store.DeleteNode(parent.Id, cascadeComposition: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*composition children*");

        // Should succeed with cascade
        var deleted = store.DeleteNode(parent.Id, cascadeComposition: true);
        deleted.Should().BeTrue();
        return Task.CompletedTask;
    }

    // ========== Raw Query Tests ==========

    [Test]
    public Task RawQuery_WithMapping_ReturnsResults()
    {
        // Arrange
        var node = CreateAndInsertNode("querytest");

        // Act
        var results = store.RawQuery(
            "SELECT id, kind FROM node WHERE kind = ?",
            r => new { Id = r.GetGuid(0), Kind = r.GetString(1) },
            "querytest"
        ).ToList();

        // Assert
        results.Count.Should().Be(1);
        results[0].Id.Should().Be(node.Id);
        results[0].Kind.Should().Be("querytest");
        return Task.CompletedTask;
    }

    [Test]
    public Task RawQuery_AsDictionary_ReturnsResults()
    {
        // Arrange
        CreateAndInsertNode("dict1");
        CreateAndInsertNode("dict2");

        // Act
        var results = store.RawQuery(
            "SELECT kind, COUNT(*) as cnt FROM node WHERE kind LIKE 'dict%' GROUP BY kind"
        ).ToList();

        // Assert
        results.Count.Should().Be(2);
        results.Should().AllSatisfy(r =>
        {
            r.Should().ContainKey("kind");
            r.Should().ContainKey("cnt");
            r["cnt"].Should().Be(1L);
        });
        return Task.CompletedTask;
    }

    // ========== Embedding Tests ==========

    [Test]
    public Task RefreshDocumentEmbeddings_WritesDocumentAndObjectScopes()
    {
        using var metrics = new IndexingMetrics();
        using var testConnection = new DuckDBConnection("Data Source=:memory:");
        testConnection.Open();
        using var testStore = new DuckDbGraphStore(testConnection, metrics);
        testStore.EnsureSchema();

        var docUri = RepoUri.Parse("file:///repo/src/Foo.cs");
        var (document, child, _) = InsertDocumentGraph(testStore, docUri);

        var provider = new TestEmbeddingProvider(text => new[] { text.Length, 1f, 2f });
        testStore.RefreshDocumentEmbeddings(provider);

        provider.Payloads.Should().HaveCount(2);

        var rows = ReadEmbeddingRows(testConnection);
        rows.Should().HaveCount(2);

        var docRow = rows.Single(r => r.Scope == "document");
        docRow.DocId.Should().Be(document.Id);
        docRow.NodeId.Should().Be(document.Id);
        docRow.Uri.Should().Be(docUri.ToString());
        docRow.Vector.Length.Should().Be(3);
        docRow.Vector[0].Should().Be(provider.Payloads[0].Length);

        var objectRow = rows.Single(r => r.Scope == "object");
        objectRow.DocId.Should().Be(document.Id);
        objectRow.NodeId.Should().Be(child.Id);
        objectRow.Uri.Should().Contain("#node/cs_function/");
        objectRow.Vector.Length.Should().Be(3);
        objectRow.Vector[0].Should().Be(provider.Payloads[1].Length);

        return Task.CompletedTask;
    }

    // ========== Repo Index Tests ==========

    [Test]
    public Task RepoIndex_ContainsDocumentProjection()
    {
        var docUri = RepoUri.Parse("file:///repo/docs/readme.md");
        var (document, _, _) = InsertDocumentGraph(store, docUri);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
                          SELECT uri, path, scope, headline, structure, body, lang, mime, digest
                          FROM repo_index
                          WHERE scope = 'document' AND uri = ?
                          """;
        var p = cmd.CreateParameter();
        p.Value = docUri.ToString();
        cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("repo_index should project document rows");

        reader.GetString(0).Should().Be(docUri.ToString());
        reader.GetString(1).Should().Be(docUri.ToString());
        reader.GetString(2).Should().Be("document");
        reader.GetString(3).Should().Be(document.Headline);
        reader.GetString(4).Should().Be(document.Structure);
        reader.GetString(5).Should().Contain("Foo summary");
        reader.GetString(6).Should().Be("docs.code");
        reader.GetString(7).Should().Be("text/markdown");

        var artifact = store.GetArtifact(document.ArtifactId!.Value)!;
        reader.GetString(8).Should().Be(artifact.Digest);

        reader.Read().Should().BeFalse("query should return exactly one document row");

        return Task.CompletedTask;
    }

    [Test]
    public Task RepoIndex_ContainsObjectProjection()
    {
        var docUri = RepoUri.Parse("file:///repo/src/Foo.cs");
        var (_, child, span) = InsertDocumentGraph(store, docUri);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
                          SELECT uri, path, scope, headline, structure, body, lang, mime, digest, line_start, line_end, embedding
                          FROM repo_index
                          WHERE scope = 'object' AND headline = ?
                          """;
        var p = cmd.CreateParameter();
        p.Value = child.Headline;
        cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("repo_index should project object rows");

        reader.GetString(0).Should().EndWith("#line=1,4");
        reader.GetString(1).Should().Be(docUri.ToString());
        reader.GetString(2).Should().Be("object");
        reader.GetString(3).Should().Be(child.Headline);
        reader.GetString(4).Should().Be(child.Structure);
        reader.GetString(5).Should().Contain(child.Structure);
        reader.GetString(6).Should().Be("docs.code");
        reader.GetString(7).Should().Be("text/markdown");
        reader.GetString(8).Should().NotBeNullOrEmpty();
        reader.GetInt32(9).Should().Be(span.StartLine);
        reader.GetInt32(10).Should().Be(span.EndLine);
        reader.IsDBNull(11).Should().BeTrue("embedding is null until refresh runs");

        reader.Read().Should().BeFalse("query should return exactly one object row");

        return Task.CompletedTask;
    }

    [Test]
    public Task Search_ReturnsDocumentRow_ForPathKeyword()
    {
        var docUri = RepoUri.Parse("file:///repo/docs/readme.md");
        InsertDocumentGraph(store, docUri);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT scope, uri FROM search('readme', k := 5)";

        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("search should return document rows for basename hits");
        reader.GetString(0).Should().Be("document");
        reader.GetString(1).Should().Contain("readme.md");

        return Task.CompletedTask;
    }

    [Test]
    public Task Search_ReturnsObjectRow_ForSymbolKeyword()
    {
        var docUri = RepoUri.Parse("file:///repo/src/Foo.cs");
        var (_, child, _) = InsertDocumentGraph(store, docUri);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT scope, kind, headline FROM search('Bar', k := 5)";

        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("search should surface object rows for symbol matches");
        reader.GetString(0).Should().Be("object");
        reader.GetString(1).Should().Be(child.Kind);
        reader.GetString(2).Should().Contain("Bar");

        return Task.CompletedTask;
    }

    [Test]
    public Task Search_HonorsUriGlob()
    {
        var readme = RepoUri.Parse("file:///repo/docs/readme.md");
        var code = RepoUri.Parse("file:///repo/src/Foo.cs");
        InsertDocumentGraph(store, readme);
        InsertDocumentGraph(store, code);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT scope, uri FROM search('repo', uri_glob := '*/docs/*', k := 10)";

        using var reader = cmd.ExecuteReader();
        var uris = new List<string>();
        while (reader.Read())
        {
            uris.Add(reader.GetString(1));
            reader.GetString(0).Should().Be("document");
        }

        uris.Should().NotBeEmpty();
        uris.Should().OnlyContain(u => u.Contains("/docs/"));

        return Task.CompletedTask;
    }

    [Test]
    public Task Related_ReturnsSimilarDocuments()
    {
        var doc1 = RepoUri.Parse("file:///repo/docs/install.md");
        var doc2 = RepoUri.Parse("file:///repo/docs/upgrade.md");
        InsertDocumentGraph(store, doc1);
        InsertDocumentGraph(store, doc2);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT uri, bm25_score FROM related(?, k := 5)";
        var p = cmd.CreateParameter();
        p.Value = doc1.ToString();
        cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("related should return at least one match");
        reader.GetString(0).Should().Contain("upgrade");
        reader.GetDouble(1).Should().BeGreaterThanOrEqualTo(0);

        return Task.CompletedTask;
    }

    [Test]
    public Task RefreshDocumentEmbeddings_UsesNodeProvidedUriWhenAvailable()
    {
        using var metrics = new IndexingMetrics();
        using var testConnection = new DuckDBConnection("Data Source=:memory:");
        testConnection.Open();
        using var testStore = new DuckDbGraphStore(testConnection, metrics);
        testStore.EnsureSchema();

        var docUri = RepoUri.Parse("file:///repo/src/Foo.cs");
        var childUri = RepoUri.Parse($"repoql://symbol/{Guid.NewGuid():N}");
        var (_, child, _) = InsertDocumentGraph(testStore, docUri, childUri);

        var provider = new TestEmbeddingProvider(_ => new[] { 1f, 2f, 3f });
        testStore.RefreshDocumentEmbeddings(provider);

        var rows = ReadEmbeddingRows(testConnection);
        var objectRow = rows.Single(r => r.Scope == "object" && r.NodeId == child.Id);
        objectRow.Uri.Should().Be(childUri.ToString());

        return Task.CompletedTask;
    }

    // ========== EntitiesByUri Tests ==========

    [Test]
    public Task EntitiesByUri_FindsDocumentByUri()
    {
        // Arrange - create a connection with UDFs enabled for this test
        using var testConnection = new DuckDBConnection("Data Source=:memory:");
        testConnection.Open();
        using var testStore = new DuckDbGraphStore(testConnection, new RepoQL.Metrics.IndexingMetrics());
        testStore.EnsureSchema();

        RepoUri.TryParse("file:///test/file.cs", out var uri);
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        testStore.UpsertNode(node);

        // Act
        var entities = testStore.EntitiesByUri("file:///test/file.cs").ToList();

        // Assert
        entities.Count.Should().Be(1);
        entities[0].Kind.Should().Be(ResolvedEntityKind.Document);
        entities[0].EntityId.Should().Be(node.Id);
        entities[0].ResolvedUri.Should().Be("file:///test/file.cs");
        return Task.CompletedTask;
    }

    [Test]
    public Task EntitiesByUri_FindsSpanByLineFragment()
    {
        // Arrange - create a connection with UDFs enabled for this test
        using var testConnection = new DuckDBConnection("Data Source=:memory:");
        testConnection.Open();
        using var testStore = new DuckDbGraphStore(testConnection, new RepoQL.Metrics.IndexingMetrics());
        testStore.EnsureSchema();

        RepoUri.TryParse("file:///doc.md", out var uri);
        var doc = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = uri,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        testStore.UpsertNode(doc);

        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            StartLine = 10,
            EndLine = 20,
            StartByte = 100,
            EndByte = 200
        };
        testStore.InsertSpan(span);

        // Act
        var entities = testStore.EntitiesByUri("file:///doc.md#line=15").ToList();

        // Assert
        entities.Should().ContainSingle(e =>
            e.Kind == ResolvedEntityKind.Span &&
            e.EntityId == span.Id);
        return Task.CompletedTask;
    }

    // ========== Constructor Tests ==========

    [Test]
    public Task Constructor_WithConnection_DoesNotDisposeConnection()
    {
        // Arrange
        using var testConnection = new DuckDBConnection("Data Source=:memory:");
        testConnection.Open();

        // Act
        var testStore = new DuckDbGraphStore(testConnection, new RepoQL.Metrics.IndexingMetrics());
        testStore.Dispose();

        // Assert - connection should still be open
        Action act = () => testConnection.CreateCommand().CommandText = "SELECT 1";
        act.Should().NotThrow();
        return Task.CompletedTask;
    }

    [Test]
    public Task Constructor_WithFilePath_DisposesOwnConnection()
    {
        // Arrange - use temp path without creating file
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        try
        {
            // Act
            var testStore = new DuckDbGraphStore(tempFile, new RepoQL.Metrics.IndexingMetrics());
            // Don't call EnsureSchema as it requires UDFs
            testStore.Dispose();

            // Assert - should be able to open a new connection to the same file
            using var newConnection = new DuckDBConnection($"Data Source={tempFile}");
            Action act = () => newConnection.Open();
            act.Should().NotThrow();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task Constructor_NullConnection_ThrowsArgumentNullException()
    {
        // Act & Assert
        Action act = () => new DuckDbGraphStore((DuckDBConnection)null!, new RepoQL.Metrics.IndexingMetrics());
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connection");
        return Task.CompletedTask;
    }

    private static (Node Document, Node Child, Span Span) InsertDocumentGraph(DuckDbGraphStore targetStore, RepoUri docUri, RepoUri? childUri = null)
    {
        var text = """
                   public class Foo
                   {
                       void Bar() {}
                   }
                   """;
        var bytes = Encoding.UTF8.GetBytes(text);
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = $"sha256:{Guid.NewGuid():N}",
            Size = bytes.Length,
            Text = text,
            MediaType = SemanticMediaType.Parse("text/markdown; kind=docs.code"),
            Headline = "Foo.cs",
            Summary = "Foo summary",
            Structure = "Foo structure"
        };
        targetStore.UpsertArtifact(artifact);

        var document = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = docUri,
            ArtifactId = artifact.Id,
            Headline = "Document headline",
            Structure = "Document structure",
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        targetStore.UpsertNode(document);

        var span = new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            StartByte = 0,
            EndByte = bytes.Length,
            StartLine = 1,
            EndLine = 4
        };
        targetStore.InsertSpan(span);

        var child = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "cs_function",
            Uri = childUri,
            SpanId = span.Id,
            Headline = "void Bar()",
            Structure = "Method Bar body",
            Props = new JsonObject { ["signature"] = "void Bar()" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        targetStore.UpsertNode(child);

        return (document, child, span);
    }

    private static List<(Guid DocId, Guid NodeId, string Uri, string Scope, float[] Vector)> ReadEmbeddingRows(DuckDBConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT doc_id, node_id, uri, scope, embedding FROM document_embedding ORDER BY scope, node_id;";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(Guid, Guid, string, string, float[])>();
        while (reader.Read())
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), ParseEmbedding(reader.GetString(4))));
        }

        return rows;
    }

    private static float[] ParseEmbedding(string json)
    {
        var array = JsonNode.Parse(json)!.AsArray();
        return array.Select(node => node!.GetValue<float>()).ToArray();
    }

    private sealed class TestEmbeddingProvider : IEmbeddingProvider
    {
        private readonly Func<string, float[]?> _factory;

        public TestEmbeddingProvider(Func<string, float[]?> factory, string model = "test-model", int dimension = 3, bool enabled = true)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Model = model;
            Dimension = dimension;
            Enabled = enabled;
        }

        public List<string> Payloads { get; } = new();
        public string Model { get; }
        public int Dimension { get; }
        public bool Enabled { get; }

        public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Payloads.Add(text);
            return Task.FromResult(_factory(text));
        }
    }

    // ========== Helper Methods ==========

    private List<string> GetTableNames()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main'";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private List<string> GetIndexNames()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT index_name FROM duckdb_indexes()";
        using var reader = cmd.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                indexes.Add(reader.GetString(0));
        }
        return indexes;
    }

    private Node CreateAndInsertNode(string kind)
    {
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertNode(node);
        return node;
    }

    private Node CreateAndInsertDocumentNode(string uri = "file:///test.cs")
    {
        RepoUri.TryParse(uri, out var parsedUri);
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = parsedUri,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertNode(node);
        return node;
    }


    private Edge CreateAndInsertEdge(Guid srcId, Guid dstId, string type)
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = type,
            IsComposition = false,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertEdge(edge);
        return edge;
    }

    private Edge CreateCompositionEdge(Guid parentId, Guid childId)
    {
        var edge = new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = parentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = 0,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        store.UpsertEdge(edge);
        return edge;
    }
}
