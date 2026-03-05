using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace RepoQL.Embedding.Service;

/// <summary>
/// HTTP client for Voyage AI's contextual embeddings API.
/// Always calls /v1/contextualizedembeddings — standard embeddings are never used.
/// </summary>
/// <remarks>
/// Handles Voyage API limits internally:
/// - Max 1,000 inputs (groups) per request
/// - Max 120K total tokens per request
/// - Max 16K total chunks per request
/// Groups are never split across Voyage calls.
/// </remarks>
internal sealed class VoyageAiClient : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.Embedding.Voyage");

    private static readonly Uri ContextualizedEndpoint = new("contextualizedembeddings", UriKind.Relative);
    private static readonly Uri RerankEndpoint = new("rerank", UriKind.Relative);
    private const int MaxGroupsPerRequest = 1000;
    private const int MaxChunksPerRequest = 16_000;
    private const int MaxTokensPerRequest = 120_000;
    private const int EstimatedCharsPerToken = 4;
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly ILogger<VoyageAiClient> _logger;
    private readonly EmbeddingServiceOptions _options;

    // Circuit breaker state
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromSeconds(30);
    private const int CircuitBreakThreshold = 5;

    public VoyageAiClient(IOptions<EmbeddingServiceOptions> options, ILogger<VoyageAiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        var baseUrl = _options.VoyageBaseUrl.TrimEnd('/') + "/";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.VoyageApiKey}");
    }

    public string Model => _options.Model;
    public int Dimension => _options.Dimension;
    public string RerankModel => _options.RerankModel;

    /// <summary>
    /// Embed grouped chunks with contextual awareness.
    /// Each group's context (if present) is prepended as the first element.
    /// Vectors are returned only for content chunks, not context elements.
    /// </summary>
    public async Task<VoyageEmbeddingResult> EmbedChunksAsync(
        IReadOnlyList<ChunkGroupInput> groups,
        CancellationToken ct)
    {
        ThrowIfCircuitOpen();

        // Build Voyage inputs: each group becomes a list where context is first, then chunks.
        // Track which Voyage output indices map back to content chunks.
        var voyageGroups = new List<List<string>>();
        var indexMap = new List<(int groupIdx, int chunkIdx)>(); // maps voyage output → content chunk
        var contextIndices = new HashSet<int>(); // voyage flat indices that are context (skip in output)
        var flatIndex = 0;

        foreach (var group in groups)
        {
            var voyageGroup = new List<string>();
            var hasContext = !string.IsNullOrWhiteSpace(group.Context);

            if (hasContext)
            {
                voyageGroup.Add(group.Context!);
                contextIndices.Add(flatIndex);
                flatIndex++;
            }

            for (var i = 0; i < group.Chunks.Count; i++)
            {
                voyageGroup.Add(group.Chunks[i]);
                indexMap.Add((group.GroupIndex, i));
                flatIndex++;
            }

            voyageGroups.Add(voyageGroup);
        }

        // Split into sub-batches that fit Voyage limits.
        var subBatches = SplitIntoBatches(voyageGroups);
        var allVectors = new List<(int flatIdx, float[] vector)>();
        var totalTokens = 0;

        foreach (var batch in subBatches)
        {
            var (vectors, tokens) = await CallVoyageWithRetryAsync(batch.Groups, "document", ct);
            totalTokens += tokens;

            for (var i = 0; i < vectors.Count; i++)
            {
                allVectors.Add((batch.StartFlatIndex + i, vectors[i]));
            }
        }

        // Map back to content chunks only (skip context elements).
        var results = new List<ChunkVector>();
        var contentFlatIndex = 0;
        foreach (var (voyageFlatIdx, vector) in allVectors)
        {
            if (contextIndices.Contains(voyageFlatIdx))
                continue;

            if (contentFlatIndex < indexMap.Count)
            {
                var (groupIdx, chunkIdx) = indexMap[contentFlatIndex];
                results.Add(new ChunkVector(groupIdx, chunkIdx, vector, null));
            }
            contentFlatIndex++;
        }

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return new VoyageEmbeddingResult(results, totalTokens);
    }

    /// <summary>
    /// Embed a single query string. Uses the contextual endpoint with a single-element group.
    /// </summary>
    public async Task<(float[] Vector, int Tokens)> EmbedQueryAsync(string text, CancellationToken ct)
    {
        ThrowIfCircuitOpen();

        var groups = new List<List<string>> { new() { text } };
        var (vectors, tokens) = await CallVoyageWithRetryAsync(groups, "query", ct);

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return (vectors.Count > 0 ? vectors[0] : [], tokens);
    }

    /// <summary>
    /// Rerank documents by relevance to a query using Voyage's rerank endpoint.
    /// </summary>
    public async Task<VoyageRerankResult> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        string? instruction,
        string? model,
        int topK,
        CancellationToken ct)
    {
        ThrowIfCircuitOpen();

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _options.RerankModel : model;

        using var activity = ActivitySource.StartActivity("voyage.rerank", ActivityKind.Client);
        activity?.SetTag("rerank.model", effectiveModel);
        activity?.SetTag("rerank.documents", documents.Count);

        var request = new JsonObject
        {
            ["query"] = query,
            ["documents"] = new JsonArray(documents.Select(d => JsonValue.Create(d)).ToArray<JsonNode>()),
            ["model"] = effectiveModel,
            ["return_documents"] = false
        };

        if (topK > 0)
            request["top_k"] = topK;

        if (!string.IsNullOrWhiteSpace(instruction))
            request["instruction"] = instruction;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(
                    request.ToJsonString(),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(RerankEndpoint, content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError("Voyage rerank API error {StatusCode}: {Body}", response.StatusCode, errorBody);
                    throw new HttpRequestException(
                        $"Voyage rerank API error {(int)response.StatusCode}: {errorBody}",
                        null,
                        response.StatusCode);
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var totalTokens = root.TryGetProperty("usage", out var usage)
                    && usage.TryGetProperty("total_tokens", out var tokensProp)
                        ? tokensProp.GetInt32()
                        : 0;

                activity?.SetTag("rerank.tokens", totalTokens);

                var results = new List<VoyageRerankScore>();
                if (root.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var index = item.GetProperty("index").GetInt32();
                        var score = item.GetProperty("relevance_score").GetSingle();
                        results.Add(new VoyageRerankScore(index, score));
                    }
                }

                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return new VoyageRerankResult(results, totalTokens);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && IsRetryable(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Voyage rerank failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt + 1, MaxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception)
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitBreakThreshold)
                {
                    _circuitOpenUntil = DateTime.UtcNow + CircuitOpenDuration;
                    _logger.LogError("Circuit breaker opened after {Failures} consecutive failures", failures);
                }
                throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private async Task<(List<float[]> Vectors, int Tokens)> CallVoyageWithRetryAsync(
        List<List<string>> groups,
        string inputType,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await CallVoyageAsync(groups, inputType, ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && IsRetryable(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                _logger.LogWarning(ex, "Voyage API call failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt + 1, MaxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception)
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitBreakThreshold)
                {
                    _circuitOpenUntil = DateTime.UtcNow + CircuitOpenDuration;
                    _logger.LogError("Circuit breaker opened after {Failures} consecutive failures", failures);
                }
                throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private async Task<(List<float[]> Vectors, int Tokens)> CallVoyageAsync(
        List<List<string>> groups,
        string inputType,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("voyage.contextualized_embed", ActivityKind.Client);
        activity?.SetTag("embed.model", _options.Model);
        activity?.SetTag("embed.groups", groups.Count);
        activity?.SetTag("embed.input_type", inputType);

        var request = new JsonObject
        {
            ["inputs"] = new JsonArray(groups.Select(g =>
                new JsonArray(g.Select(c => JsonValue.Create(c)).ToArray<JsonNode>())
            ).ToArray<JsonNode>()),
            ["model"] = _options.Model,
            ["input_type"] = inputType,
            ["output_dimension"] = _options.Dimension,
            ["output_dtype"] = _options.OutputDtype
        };

        using var content = new StringContent(
            request.ToJsonString(),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(ContextualizedEndpoint, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Voyage API error {StatusCode}: {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Voyage API error {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var totalTokens = root.TryGetProperty("total_tokens", out var tokensProp) ? tokensProp.GetInt32() : 0;
        activity?.SetTag("embed.tokens", totalTokens);

        var vectors = new List<float[]>();

        if (root.TryGetProperty("results", out var results))
        {
            // Contextual endpoint returns: { results: [{ embeddings: [[...], [...]], index: 0 }, ...] }
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("embeddings", out var embeddings))
                    continue;

                foreach (var embedding in embeddings.EnumerateArray())
                {
                    var vec = new float[embedding.GetArrayLength()];
                    var i = 0;
                    foreach (var val in embedding.EnumerateArray())
                        vec[i++] = val.GetSingle();
                    vectors.Add(vec);
                }
            }
        }

        _logger.LogDebug("Voyage returned {VectorCount} vectors, {Tokens} tokens", vectors.Count, totalTokens);
        return (vectors, totalTokens);
    }

    private static List<VoyageBatch> SplitIntoBatches(List<List<string>> groups)
    {
        var batches = new List<VoyageBatch>();
        var currentGroups = new List<List<string>>();
        var currentChunks = 0;
        var currentTokenEstimate = 0;
        var currentFlatStart = 0;
        var runningFlatIndex = 0;

        foreach (var group in groups)
        {
            var groupChunks = group.Count;
            var groupTokens = group.Sum(c => c.Length / EstimatedCharsPerToken);

            // Would this group push us over limits?
            if (currentGroups.Count > 0 &&
                (currentGroups.Count + 1 > MaxGroupsPerRequest ||
                 currentChunks + groupChunks > MaxChunksPerRequest ||
                 currentTokenEstimate + groupTokens > MaxTokensPerRequest))
            {
                batches.Add(new VoyageBatch(currentGroups, currentFlatStart));
                currentGroups = [];
                currentChunks = 0;
                currentTokenEstimate = 0;
                currentFlatStart = runningFlatIndex;
            }

            currentGroups.Add(group);
            currentChunks += groupChunks;
            currentTokenEstimate += groupTokens;
            runningFlatIndex += groupChunks;
        }

        if (currentGroups.Count > 0)
            batches.Add(new VoyageBatch(currentGroups, currentFlatStart));

        return batches;
    }

    private void ThrowIfCircuitOpen()
    {
        if (DateTime.UtcNow < _circuitOpenUntil)
            throw new InvalidOperationException("Voyage API circuit breaker is open — retry shortly");
    }

    private static bool IsRetryable(HttpRequestException ex)
        => ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    public void Dispose() => _httpClient.Dispose();

    private record VoyageBatch(List<List<string>> Groups, int StartFlatIndex);
}

internal record ChunkGroupInput(int GroupIndex, string? Context, IReadOnlyList<string> Chunks);
internal record ChunkVector(int GroupIndex, int ChunkIndex, float[] Vector, string? Error);
internal record VoyageEmbeddingResult(IReadOnlyList<ChunkVector> Vectors, int TotalTokens);
internal record VoyageRerankScore(int Index, float RelevanceScore);
internal record VoyageRerankResult(IReadOnlyList<VoyageRerankScore> Results, int TotalTokens);
