using AwesomeAssertions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class EmbeddingRefresherContextualGroupTests
{
    #region BuildContextualGroups

    [Test]
    public void SmallDocument_SingleGroup_NoSplitting()
    {
        var docs = new[]
        {
            MakeDoc("file:///a.cs", "context", ["chunk1", "chunk2", "chunk3"])
        };

        var (groups, meta) = EmbeddingRefresher.BuildContextualGroups(docs, EmbeddingRefresher.MaxContextualGroupChars);

        groups.Should().HaveCount(1);
        groups[0].Chunks.Should().HaveCount(3);
        groups[0].Context.Should().Be("context");
        meta.Should().HaveCount(1);
        meta[0].Should().Be((0, 0)); // docIndex=0, chunkOffset=0
    }

    [Test]
    public void OversizedDocument_SplitsIntoMultipleGroups()
    {
        // Context is 100 chars, each chunk is 500 chars.
        // Max group chars = 1600: context(100) + chunks can fit at most 1500 chars = 3 chunks per group.
        var context = new string('C', 100);
        var chunks = Enumerable.Range(0, 10).Select(i => new string((char)('A' + i), 500)).ToList();
        var docs = new[] { MakeDoc("file:///big.cs", context, chunks) };

        var (groups, meta) = EmbeddingRefresher.BuildContextualGroups(docs, 1600);

        // 10 chunks at 500 chars each, 3 per group → ceil(10/3) = 4 groups
        groups.Should().HaveCount(4);
        meta.Should().HaveCount(4);

        // All groups share the same context
        foreach (var g in groups)
            g.Context.Should().Be(context);

        // Verify chunk counts: 3, 3, 3, 1
        groups[0].Chunks.Should().HaveCount(3);
        groups[1].Chunks.Should().HaveCount(3);
        groups[2].Chunks.Should().HaveCount(3);
        groups[3].Chunks.Should().HaveCount(1);

        // Verify metadata tracks chunk offsets
        meta[0].Should().Be((0, 0));
        meta[1].Should().Be((0, 3));
        meta[2].Should().Be((0, 6));
        meta[3].Should().Be((0, 9));
    }

    [Test]
    public void MultipleDocuments_MixedSizes()
    {
        var smallDoc = MakeDoc("file:///small.cs", "ctx", ["a", "b"]);
        var bigDoc = MakeDoc("file:///big.cs", new string('X', 100),
            Enumerable.Range(0, 6).Select(i => new string('Z', 500)).ToList());
        var tinyDoc = MakeDoc("file:///tiny.cs", null, ["single"]);

        var docs = new[] { smallDoc, bigDoc, tinyDoc };

        // maxGroupChars = 1600 → big doc splits: context(100) + 3*500 = 1600 per group → 2 groups
        var (groups, meta) = EmbeddingRefresher.BuildContextualGroups(docs, 1600);

        // small(1) + big(2 split groups) + tiny(1) = 4 groups
        groups.Should().HaveCount(4);
        meta.Should().HaveCount(4);

        // small doc: docIndex=0, chunkOffset=0
        meta[0].Should().Be((0, 0));
        groups[0].Chunks.Should().HaveCount(2);

        // big doc split group 1: docIndex=1, chunkOffset=0
        meta[1].Should().Be((1, 0));
        groups[1].Chunks.Should().HaveCount(3);

        // big doc split group 2: docIndex=1, chunkOffset=3
        meta[2].Should().Be((1, 3));
        groups[2].Chunks.Should().HaveCount(3);

        // tiny doc: docIndex=2, chunkOffset=0
        meta[3].Should().Be((2, 0));
        groups[3].Chunks.Should().HaveCount(1);
    }

    [Test]
    public void NullContext_StillWorks()
    {
        var docs = new[] { MakeDoc("file:///a.cs", null, ["chunk1", "chunk2"]) };

        var (groups, meta) = EmbeddingRefresher.BuildContextualGroups(docs, EmbeddingRefresher.MaxContextualGroupChars);

        groups.Should().HaveCount(1);
        groups[0].Context.Should().BeNull();
        groups[0].Chunks.Should().HaveCount(2);
    }

    [Test]
    public void SingleOversizedChunk_AlwaysIncludedInGroup()
    {
        // One chunk that exceeds the group limit by itself.
        // Should still be included (at least one chunk per group guarantee).
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", [new string('X', 5000)]) };

        var (groups, _) = EmbeddingRefresher.BuildContextualGroups(docs, 1000);

        groups.Should().HaveCount(1);
        groups[0].Chunks.Should().HaveCount(1);
    }

    #endregion

    #region MapContextualResults

    [Test]
    public void SingleDocument_DirectMapping()
    {
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", ["c1", "c2", "c3"]) };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)> { (0, 0) };

        var vectors = new List<ContextualChunkVector>
        {
            new(0, 0, [1f, 2f], null),
            new(0, 1, [3f, 4f], null),
            new(0, 2, [5f, 6f], null),
        };
        var result = new ContextualEmbeddingResult(vectors, 100);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 3);

        mapped.Should().HaveCount(3);
        mapped[0].Should().Equal(1f, 2f);
        mapped[1].Should().Equal(3f, 4f);
        mapped[2].Should().Equal(5f, 6f);
    }

    [Test]
    public void SplitDocument_MapsChunkOffsetsCorrectly()
    {
        // One doc with 6 items, split into 2 groups of 3
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", ["c1", "c2", "c3", "c4", "c5", "c6"]) };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)> { (0, 0), (0, 3) };

        var vectors = new List<ContextualChunkVector>
        {
            // Group 0: chunks 0-2
            new(0, 0, [1f], null),
            new(0, 1, [2f], null),
            new(0, 2, [3f], null),
            // Group 1: chunks 3-5 (but ChunkIndex is 0-based within the split group)
            new(1, 0, [4f], null),
            new(1, 1, [5f], null),
            new(1, 2, [6f], null),
        };
        var result = new ContextualEmbeddingResult(vectors, 200);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 6);

        mapped.Should().HaveCount(6);
        mapped[0].Should().Equal(1f);
        mapped[1].Should().Equal(2f);
        mapped[2].Should().Equal(3f);
        mapped[3].Should().Equal(4f);
        mapped[4].Should().Equal(5f);
        mapped[5].Should().Equal(6f);
    }

    [Test]
    public void MultipleDocuments_WithSplitGroup_MapsCorrectly()
    {
        // Doc 0: 2 items (1 group)
        // Doc 1: 4 items, split into 2 groups of 2
        // Doc 2: 1 item (1 group)
        // Total: 7 items, 4 groups
        var docs = new[]
        {
            MakeDoc("file:///a.cs", "ctx", ["a1", "a2"]),
            MakeDoc("file:///b.cs", "ctx", ["b1", "b2", "b3", "b4"]),
            MakeDoc("file:///c.cs", null, ["c1"]),
        };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)>
        {
            (0, 0), // group 0 → doc 0, chunks starting at 0
            (1, 0), // group 1 → doc 1, chunks starting at 0
            (1, 2), // group 2 → doc 1, chunks starting at 2
            (2, 0), // group 3 → doc 2, chunks starting at 0
        };

        var vectors = new List<ContextualChunkVector>
        {
            new(0, 0, [10f], null), new(0, 1, [11f], null),     // doc 0
            new(1, 0, [20f], null), new(1, 1, [21f], null),     // doc 1, first half
            new(2, 0, [22f], null), new(2, 1, [23f], null),     // doc 1, second half
            new(3, 0, [30f], null),                               // doc 2
        };
        var result = new ContextualEmbeddingResult(vectors, 300);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 7);

        mapped.Should().HaveCount(7);
        // Doc 0: items 0-1
        mapped[0].Should().Equal(10f);
        mapped[1].Should().Equal(11f);
        // Doc 1: items 2-5
        mapped[2].Should().Equal(20f);
        mapped[3].Should().Equal(21f);
        mapped[4].Should().Equal(22f);
        mapped[5].Should().Equal(23f);
        // Doc 2: item 6
        mapped[6].Should().Equal(30f);
    }

    [Test]
    public void NullVectors_PreservedInMapping()
    {
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", ["c1", "c2"]) };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)> { (0, 0) };

        var vectors = new List<ContextualChunkVector>
        {
            new(0, 0, [1f, 2f], null),
            new(0, 1, null, "token limit exceeded"),
        };
        var result = new ContextualEmbeddingResult(vectors, 50);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 2);

        mapped.Should().HaveCount(2);
        mapped[0].Should().Equal(1f, 2f);
        mapped[1].Should().BeNull();
    }

    [Test]
    public void OutOfBoundsGroupIndex_Ignored()
    {
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", ["c1"]) };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)> { (0, 0) };

        var vectors = new List<ContextualChunkVector>
        {
            new(0, 0, [1f], null),
            new(99, 0, [2f], null), // out of bounds group index
        };
        var result = new ContextualEmbeddingResult(vectors, 50);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 1);

        mapped.Should().HaveCount(1);
        mapped[0].Should().Equal(1f);
    }

    [Test]
    public void EmptyResult_AllNull()
    {
        var docs = new[] { MakeDoc("file:///a.cs", "ctx", ["c1", "c2"]) };
        var groupMeta = new List<(int DocIndex, int ChunkOffset)> { (0, 0) };
        var result = new ContextualEmbeddingResult([], 0);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 2);

        mapped.Should().HaveCount(2);
        mapped[0].Should().BeNull();
        mapped[1].Should().BeNull();
    }

    #endregion

    #region Roundtrip: BuildContextualGroups → MapContextualResults

    [Test]
    public void Roundtrip_SplitDocument_VectorsMapBackCorrectly()
    {
        // Simulate a document that gets split, then results come back
        var context = new string('C', 100);
        var chunks = Enumerable.Range(0, 8).Select(i => new string((char)('A' + i), 500)).ToList();
        var doc = MakeDoc("file:///big.cs", context, chunks);
        var docs = new[] { doc };

        var (groups, groupMeta) = EmbeddingRefresher.BuildContextualGroups(docs, 1600);
        // 8 chunks at 500 chars, context 100, max 1600 → 3 chunks per group → 3 groups (3+3+2)

        groups.Should().HaveCount(3);

        // Simulate embedding results: each vector is [chunkGlobalIndex]
        var vectors = new List<ContextualChunkVector>();
        for (var g = 0; g < groups.Count; g++)
        {
            for (var c = 0; c < groups[g].Chunks.Count; c++)
            {
                var globalIdx = groupMeta[g].ChunkOffset + c;
                vectors.Add(new ContextualChunkVector(g, c, [globalIdx], null));
            }
        }
        var result = new ContextualEmbeddingResult(vectors, 500);

        var mapped = EmbeddingRefresher.MapContextualResults(result, docs, groupMeta, totalItems: 8);

        mapped.Should().HaveCount(8);
        for (var i = 0; i < 8; i++)
        {
            mapped[i].Should().NotBeNull();
            mapped[i]![0].Should().Be(i);
        }
    }

    #endregion

    #region Helpers

    private static EmbeddingRefresher.PendingDocument MakeDoc(string uri, string? context, IReadOnlyList<string> chunks)
    {
        var items = chunks.Select((_, i) => new EmbeddingRefresher.EmbeddingWorkItem(
            Guid.NewGuid(), Guid.NewGuid(), i, "full", uri, "document", null, null)).ToList();
        return new EmbeddingRefresher.PendingDocument(uri, context, chunks, items);
    }

    #endregion
}
