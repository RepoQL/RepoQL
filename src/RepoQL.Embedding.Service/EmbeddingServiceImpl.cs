using Grpc.Core;
using RepoQL.Embedding.Service.Cache;

namespace RepoQL.Embedding.Service;

/// <summary>
/// gRPC service implementation. Thin relay: validate, cache if available, forward misses to Voyage, return.
/// </summary>
internal sealed class EmbeddingServiceImpl : EmbeddingService.EmbeddingServiceBase
{
    private readonly VoyageAiClient _voyage;
    private readonly ILogger<EmbeddingServiceImpl> _logger;
    private readonly EmbeddingCacheLayer? _cache;

    public EmbeddingServiceImpl(
        VoyageAiClient voyage,
        ILogger<EmbeddingServiceImpl> logger,
        EmbeddingCacheLayer? cache = null)
    {
        _voyage = voyage;
        _logger = logger;
        _cache = cache;
    }

    public override async Task<EmbedChunksResponse> EmbedChunks(
        EmbedChunksRequest request,
        ServerCallContext context)
    {
        if (request.Groups.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "At least one chunk group is required"));

        var groups = BuildChunkGroups(request);
        var totalChunks = groups.Sum(static group => group.Chunks.Count);

        _logger.LogInformation("EmbedChunks: {Groups} groups, {Chunks} chunks", groups.Count, totalChunks);

        try
        {
            if (_cache is not null && !string.IsNullOrWhiteSpace(request.Source))
            {
                var fingerprints = BuildFingerprints(request);
                var lookup = await _cache.LookupAsync(request.Source, fingerprints, context.CancellationToken);

                var cacheHitCount = fingerprints.Count - lookup.Misses.Count;
                if (cacheHitCount > 0)
                {
                    var hitTexts = fingerprints
                        .Where(fp => !lookup.Misses.Any(m => m.OriginalIndex == fp.OriginalIndex))
                        .Select(static fp => fp.Text);
                    EmbeddingMetrics.RecordCacheHits(cacheHitCount, hitTexts);
                }

                if (lookup.Misses.Count == 0)
                    return BuildResponse(
                        fingerprints,
                        lookup.Hits,
                        new Dictionary<int, ComputedChunkResult>(),
                        totalTokens: 0);

                var computed = await ComputeMissesAsync(request, lookup.Misses, context.CancellationToken);
                EmbeddingMetrics.RecordVoyageChunks(lookup.Misses.Count, computed.TotalTokens);

                if (computed.CacheEntries.Count > 0)
                    _ = _cache.WriteBackAsync(request.Source, computed.CacheEntries);

                return BuildResponse(fingerprints, lookup.Hits, computed.ByIndex, computed.TotalTokens);
            }

            var result = await ComputeAllAsync(groups, request, context.CancellationToken);
            EmbeddingMetrics.RecordVoyageChunks(totalChunks, result.TotalTokens);
            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("circuit breaker", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                "Voyage API unavailable — retry shortly"));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "Embedding provider rate limited — retry shortly"));
        }
        catch (TaskCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled"));
        }
        catch (TaskCanceledException)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                "Voyage API timed out — retry or reduce batch size"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbedChunks failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    internal static IReadOnlyList<ChunkFingerprint> BuildFingerprints(EmbedChunksRequest request)
        => EmbeddingCachePrimitives.BuildFingerprints(request);

    public override async Task<EmbedQueryResponse> EmbedQuery(
        EmbedQueryRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Query text is required"));

        try
        {
            var (vector, tokens) = await _voyage.EmbedQueryAsync(request.Text, context.CancellationToken);

            return new EmbedQueryResponse
            {
                Vector = { vector },
                Tokens = tokens
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("circuit breaker", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                "Voyage API unavailable — retry shortly"));
        }
        catch (TaskCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbedQuery failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override Task<GetModelInfoResponse> GetModelInfo(
        GetModelInfoRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new GetModelInfoResponse
        {
            Model = _voyage.Model,
            Dimension = _voyage.Dimension
        });
    }

    public override async Task<RerankResponse> Rerank(
        RerankRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Query is required"));
        if (request.Documents.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "At least one document is required"));
        if (request.TopK < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "top_k must be >= 0 (0 = return all)"));

        _logger.LogDebug("Rerank: {Documents} documents, model={Model}, instruction={HasInstruction}",
            request.Documents.Count,
            string.IsNullOrEmpty(request.Model) ? _voyage.RerankModel : request.Model,
            !string.IsNullOrEmpty(request.Instruction));

        try
        {
            var documents = new List<string>(request.Documents.Count);
            var indexMap = new List<int>(request.Documents.Count);
            foreach (var doc in request.Documents)
            {
                indexMap.Add(doc.Index);
                documents.Add(doc.Text);
            }

            var result = await _voyage.RerankAsync(
                request.Query,
                documents,
                request.Instruction,
                request.Model,
                request.TopK,
                context.CancellationToken);

            var response = new RerankResponse { TotalTokens = result.TotalTokens };
            foreach (var score in result.Results)
            {
                if (score.Index < 0 || score.Index >= indexMap.Count)
                {
                    _logger.LogWarning("Voyage returned out-of-range index {Index} (expected 0-{Max}), skipping",
                        score.Index, indexMap.Count - 1);
                    continue;
                }

                response.Results.Add(new RerankResult
                {
                    Index = indexMap[score.Index],
                    RelevanceScore = score.RelevanceScore
                });
            }

            return response;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("circuit breaker", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                "Voyage API unavailable — retry shortly"));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "Rerank provider rate limited — retry shortly"));
        }
        catch (TaskCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled"));
        }
        catch (TaskCanceledException)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                "Voyage API timed out"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rerank failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    private async Task<EmbedChunksResponse> ComputeAllAsync(
        IReadOnlyList<ChunkGroupInput> groups,
        EmbedChunksRequest request,
        CancellationToken ct)
    {
        var result = await _voyage.EmbedChunksAsync(groups, ct);

        _logger.LogInformation("EmbedChunks: Voyage returned {VectorCount} vectors, {Tokens} tokens",
            result.Vectors.Count, result.TotalTokens);

        var response = new EmbedChunksResponse { TotalTokens = result.TotalTokens };
        var groupChunkOffset = BuildGroupChunkOffset(request);

        foreach (var vec in result.Vectors)
        {
            var globalIndex = groupChunkOffset.GetValueOrDefault(vec.GroupIndex) + vec.ChunkIndex;
            response.Embeddings.Add(new ChunkEmbedding
            {
                Index = globalIndex,
                Vector = { vec.Vector },
                Error = vec.Error ?? ""
            });
        }

        var emptyProtoVecs = response.Embeddings.Count(e => e.Vector.Count == 0);
        if (emptyProtoVecs > 0)
            _logger.LogWarning("EmbedChunks: {Empty}/{Total} proto embeddings have empty Vector field",
                emptyProtoVecs, response.Embeddings.Count);

        return response;
    }

    private async Task<ComputedMissesResult> ComputeMissesAsync(
        EmbedChunksRequest request,
        IReadOnlyList<ChunkFingerprint> misses,
        CancellationToken ct)
    {
        if (misses.Count == 0)
        {
            return new ComputedMissesResult(
                new Dictionary<int, ComputedChunkResult>(),
                new List<CacheEntry>(),
                0);
        }

        var missesByIndex = misses.ToDictionary(static miss => miss.OriginalIndex);
        var missGroups = new List<MissGroup>();
        var flatIndex = 0;
        var reducedGroupIndex = 0;

        foreach (var group in request.Groups)
        {
            var selectedChunks = new List<string>();
            var selectedIndices = new List<int>();

            foreach (var chunk in group.Chunks)
            {
                if (missesByIndex.TryGetValue(flatIndex, out var miss))
                {
                    selectedChunks.Add(miss.Text);
                    selectedIndices.Add(flatIndex);
                }

                flatIndex++;
            }

            if (selectedChunks.Count == 0)
                continue;

            missGroups.Add(new MissGroup(
                reducedGroupIndex,
                group.Context,
                selectedChunks,
                selectedIndices));
            reducedGroupIndex++;
        }

        var voyageInputs = missGroups
            .Select(group => new ChunkGroupInput(group.ReducedGroupIndex, group.Context, group.Chunks))
            .ToList();
        var voyageResult = await _voyage.EmbedChunksAsync(voyageInputs, ct);

        var computedByIndex = new Dictionary<int, ComputedChunkResult>();
        var cacheEntries = new List<CacheEntry>();
        var createdAt = DateTimeOffset.UtcNow;

        foreach (var vector in voyageResult.Vectors)
        {
            if (vector.GroupIndex < 0 || vector.GroupIndex >= missGroups.Count)
                continue;

            var missGroup = missGroups[vector.GroupIndex];
            if (vector.ChunkIndex < 0 || vector.ChunkIndex >= missGroup.OriginalIndices.Count)
                continue;

            var originalIndex = missGroup.OriginalIndices[vector.ChunkIndex];
            var fingerprint = missesByIndex[originalIndex];
            var error = vector.Error ?? "";

            byte[]? vectorBytes = null;
            if (string.IsNullOrEmpty(error))
            {
                vectorBytes = EmbeddingCachePrimitives.NarrowVectorToBytes(vector.Vector);
                cacheEntries.Add(new CacheEntry(fingerprint.Sha256, vectorBytes, createdAt));
            }

            computedByIndex[originalIndex] = new ComputedChunkResult(originalIndex, vectorBytes, error);
        }

        foreach (var miss in misses)
        {
            if (!computedByIndex.ContainsKey(miss.OriginalIndex))
                computedByIndex[miss.OriginalIndex] = new ComputedChunkResult(
                    miss.OriginalIndex,
                    null,
                    "Embedding provider returned no vector.");
        }

        return new ComputedMissesResult(computedByIndex, cacheEntries, voyageResult.TotalTokens);
    }

    private static EmbedChunksResponse BuildResponse(
        IReadOnlyList<ChunkFingerprint> fingerprints,
        IReadOnlyDictionary<string, byte[]> hits,
        IReadOnlyDictionary<int, ComputedChunkResult> computedByIndex,
        int totalTokens)
    {
        var response = new EmbedChunksResponse { TotalTokens = totalTokens };

        foreach (var fingerprint in fingerprints.OrderBy(static fingerprint => fingerprint.OriginalIndex))
        {
            if (computedByIndex.TryGetValue(fingerprint.OriginalIndex, out var computed))
            {
                response.Embeddings.Add(new ChunkEmbedding
                {
                    Index = fingerprint.OriginalIndex,
                    Vector =
                    {
                        computed.VectorBytes is null
                            ? Array.Empty<float>()
                            : EmbeddingCachePrimitives.WidenVectorToFloats(computed.VectorBytes)
                    },
                    Error = computed.Error
                });
                continue;
            }

            if (hits.TryGetValue(fingerprint.Sha256, out var cachedVector))
            {
                response.Embeddings.Add(new ChunkEmbedding
                {
                    Index = fingerprint.OriginalIndex,
                    Vector = { EmbeddingCachePrimitives.WidenVectorToFloats(cachedVector) },
                    Error = ""
                });
                continue;
            }

            response.Embeddings.Add(new ChunkEmbedding
            {
                Index = fingerprint.OriginalIndex,
                Error = "Embedding unavailable."
            });
        }

        return response;
    }

    private static List<ChunkGroupInput> BuildChunkGroups(EmbedChunksRequest request)
    {
        var groups = new List<ChunkGroupInput>(request.Groups.Count);
        for (var i = 0; i < request.Groups.Count; i++)
        {
            var group = request.Groups[i];
            if (group.Chunks.Count == 0)
                continue;

            groups.Add(new ChunkGroupInput(i, group.Context, group.Chunks.ToList()));
        }

        return groups;
    }

    private static Dictionary<int, int> BuildGroupChunkOffset(EmbedChunksRequest request)
    {
        var groupChunkOffset = new Dictionary<int, int>();
        var offset = 0;
        for (var i = 0; i < request.Groups.Count; i++)
        {
            groupChunkOffset[i] = offset;
            offset += request.Groups[i].Chunks.Count;
        }

        return groupChunkOffset;
    }

    private sealed record MissGroup(
        int ReducedGroupIndex,
        string Context,
        IReadOnlyList<string> Chunks,
        IReadOnlyList<int> OriginalIndices);

    private sealed record ComputedChunkResult(
        int OriginalIndex,
        byte[]? VectorBytes,
        string Error);

    private sealed record ComputedMissesResult(
        IReadOnlyDictionary<int, ComputedChunkResult> ByIndex,
        IReadOnlyList<CacheEntry> CacheEntries,
        int TotalTokens);
}
