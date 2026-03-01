using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Embedding.Client;

/// <summary>
/// Purpose: Connects to the remote embedding service over gRPC and implements contextual embedding.
/// Complexity: Channel lifecycle, bearer token injection, proto ↔ domain mapping,
/// and dimension validation on first call.
/// </summary>
public sealed class GrpcEmbeddingProvider : IContextualEmbeddingProvider, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmbeddingService.EmbeddingServiceClient _client;
    private readonly string _apiKey;
    private readonly ILogger<GrpcEmbeddingProvider>? _logger;

    private string? _model;
    private int _dimension;
    private bool _infoFetched;

    public GrpcEmbeddingProvider(
        string url,
        string apiKey,
        int timeoutSeconds,
        ILogger<GrpcEmbeddingProvider>? logger)
    {
        _apiKey = apiKey;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        _client = new EmbeddingService.EmbeddingServiceClient(_channel);
    }

    public string Model => _model ?? "unknown";
    public int Dimension => _dimension;
    public bool Enabled => true;

    public async Task<ContextualEmbeddingResult> EmbedChunksAsync(
        IReadOnlyList<DocumentChunkGroup> groups,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelInfoAsync(cancellationToken).ConfigureAwait(false);

        var request = new EmbedChunksRequest();
        foreach (var group in groups)
        {
            var protoGroup = new ChunkGroup
            {
                DocumentUri = group.DocumentUri,
                Context = group.Context ?? ""
            };
            protoGroup.Chunks.AddRange(group.Chunks);
            request.Groups.Add(protoGroup);
        }

        var response = await _client.EmbedChunksAsync(
            request,
            headers: AuthHeaders(),
            cancellationToken: cancellationToken);

        var vectors = new List<ContextualChunkVector>(response.Embeddings.Count);

        // Build group offset map to convert flat index back to (group, chunk).
        var groupOffsets = new int[groups.Count];
        var offset = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            groupOffsets[i] = offset;
            offset += groups[i].Chunks.Count;
        }

        foreach (var embedding in response.Embeddings)
        {
            var (groupIdx, chunkIdx) = FlatIndexToGroupChunk(embedding.Index, groupOffsets);
            var vector = embedding.Vector.Count > 0
                ? embedding.Vector.ToArray()
                : null;
            var error = string.IsNullOrEmpty(embedding.Error) ? null : embedding.Error;
            vectors.Add(new ContextualChunkVector(groupIdx, chunkIdx, vector, error));
        }

        return new ContextualEmbeddingResult(vectors, response.TotalTokens);
    }

    public async Task<float[]?> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelInfoAsync(cancellationToken).ConfigureAwait(false);

        var response = await _client.EmbedQueryAsync(
            new EmbedQueryRequest { Text = text },
            headers: AuthHeaders(),
            cancellationToken: cancellationToken);

        return response.Vector.Count > 0 ? response.Vector.ToArray() : null;
    }

    private async Task EnsureModelInfoAsync(CancellationToken ct)
    {
        if (_infoFetched)
            return;

        var info = await _client.GetModelInfoAsync(
            new GetModelInfoRequest(),
            headers: AuthHeaders(),
            cancellationToken: ct);

        _model = info.Model;
        _dimension = info.Dimension;
        _infoFetched = true;

        _logger?.LogInformation(
            "Remote embedding service: model={Model}, dimension={Dimension}",
            _model, _dimension);
    }

    private Metadata AuthHeaders()
    {
        return new Metadata
        {
            { "authorization", $"Bearer {_apiKey}" }
        };
    }

    private static (int GroupIndex, int ChunkIndex) FlatIndexToGroupChunk(
        int flatIndex,
        int[] groupOffsets)
    {
        for (var i = groupOffsets.Length - 1; i >= 0; i--)
        {
            if (flatIndex >= groupOffsets[i])
                return (i, flatIndex - groupOffsets[i]);
        }

        return (0, flatIndex);
    }

    public void Dispose() => _channel.Dispose();
}
