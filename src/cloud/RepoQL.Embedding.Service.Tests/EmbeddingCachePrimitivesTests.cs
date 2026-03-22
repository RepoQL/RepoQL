using AwesomeAssertions;
using RepoQL.Embedding;
using RepoQL.Embedding.Service;
using RepoQL.Embedding.Service.Cache;

namespace RepoQL.Embedding.Service.Tests;

public sealed class EmbeddingCachePrimitivesTests
{
    [Test]
    [Arguments("", "hello", "8a2a5c9b768827de5a9552c38a044c66959c68f6d2f21b5260af54d2f87db827")]
    [Arguments("ctx", "chunk", "9c01fd9eed6083c05fd43177d78009d601cfb00782458d73e2e811b5727a8c5e")]
    public async Task ComputeChunkFingerprint_ReturnsExpectedSha256(string context, string chunk, string expected)
    {
        var hash = EmbeddingCachePrimitives.ComputeChunkFingerprint(context, chunk);

        await Assert.That(hash).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildFingerprints_FlattensGroupsAndPreservesContext()
    {
        var request = new EmbedChunksRequest
        {
            Groups =
            {
                new ChunkGroup
                {
                    Context = "doc-1",
                    Chunks = { "alpha", "beta" }
                },
                new ChunkGroup
                {
                    Context = "",
                    Chunks = { "gamma" }
                },
                new ChunkGroup
                {
                    Context = "ignored-empty-group"
                }
            }
        };

        var fingerprints = EmbeddingServiceImpl.BuildFingerprints(request);

        await Assert.That(fingerprints.Select(static fingerprint => fingerprint.OriginalIndex).ToArray())
            .IsEquivalentTo(new[] { 0, 1, 2 });
        await Assert.That(fingerprints.Select(static fingerprint => fingerprint.Context).ToArray())
            .IsEquivalentTo(new[] { "doc-1", "doc-1", "" });
        await Assert.That(fingerprints.Select(static fingerprint => fingerprint.Text).ToArray())
            .IsEquivalentTo(new[] { "alpha", "beta", "gamma" });
        await Assert.That(fingerprints[0].Sha256)
            .IsEqualTo(EmbeddingCachePrimitives.ComputeChunkFingerprint("doc-1", "alpha"));
        await Assert.That(fingerprints[2].Sha256)
            .IsEqualTo(EmbeddingCachePrimitives.ComputeChunkFingerprint("", "gamma"));
    }

    [Test]
    public async Task NarrowVectorToBytes_AllowsInt8BoundaryValues()
    {
        var narrowed = EmbeddingCachePrimitives.NarrowVectorToBytes(new[] { -128f, -1f, 0f, 127f });
        var widened = EmbeddingCachePrimitives.WidenVectorToFloats(narrowed);

        await Assert.That(widened).IsEquivalentTo(new[] { -128f, -1f, 0f, 127f });
    }

    [Test]
    [Arguments(128f)]
    [Arguments(-129f)]
    [Arguments(12.5f)]
    public async Task NarrowVectorToBytes_RejectsInvalidValues(float value)
    {
        Action action = () => EmbeddingCachePrimitives.NarrowVectorToBytes(new[] { value });

        await Assert.That(action).Throws<InvalidOperationException>();
    }
}
