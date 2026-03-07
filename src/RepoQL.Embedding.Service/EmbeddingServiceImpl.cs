using Grpc.Core;

namespace RepoQL.Embedding.Service;

/// <summary>
/// gRPC service implementation. Thin relay: validate, forward to Voyage, return.
/// </summary>
internal sealed class EmbeddingServiceImpl : EmbeddingService.EmbeddingServiceBase
{
    private readonly VoyageAiClient _voyage;
    private readonly ILogger<EmbeddingServiceImpl> _logger;

    public EmbeddingServiceImpl(VoyageAiClient voyage, ILogger<EmbeddingServiceImpl> logger)
    {
        _voyage = voyage;
        _logger = logger;
    }

    public override async Task<EmbedChunksResponse> EmbedChunks(
        EmbedChunksRequest request,
        ServerCallContext context)
    {
        if (request.Groups.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "At least one chunk group is required"));

        var groups = new List<ChunkGroupInput>(request.Groups.Count);
        var totalChunks = 0;

        for (var i = 0; i < request.Groups.Count; i++)
        {
            var group = request.Groups[i];
            if (group.Chunks.Count == 0)
                continue;

            groups.Add(new ChunkGroupInput(i, group.Context, group.Chunks.ToList()));
            totalChunks += group.Chunks.Count;
        }

        _logger.LogInformation("EmbedChunks: {Groups} groups, {Chunks} chunks", groups.Count, totalChunks);

        try
        {
            var result = await _voyage.EmbedChunksAsync(groups, context.CancellationToken);

            var response = new EmbedChunksResponse { TotalTokens = result.TotalTokens };

            // Build flat index mapping: for each group, its chunks contribute sequential flat indices.
            var groupChunkOffset = new Dictionary<int, int>();
            var offset = 0;
            for (var i = 0; i < request.Groups.Count; i++)
            {
                groupChunkOffset[i] = offset;
                offset += request.Groups[i].Chunks.Count;
            }

            _logger.LogInformation("EmbedChunks: Voyage returned {VectorCount} vectors, {Tokens} tokens",
                result.Vectors.Count, result.TotalTokens);

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

            // Verify proto serialization
            var emptyProtoVecs = response.Embeddings.Count(e => e.Vector.Count == 0);
            if (emptyProtoVecs > 0)
                _logger.LogWarning("EmbedChunks: {Empty}/{Total} proto embeddings have empty Vector field",
                    emptyProtoVecs, response.Embeddings.Count);

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
            // Build ordered document list; preserve caller indices for mapping back.
            var documents = new List<string>(request.Documents.Count);
            var indexMap = new List<int>(request.Documents.Count); // voyage position → caller index
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
}
