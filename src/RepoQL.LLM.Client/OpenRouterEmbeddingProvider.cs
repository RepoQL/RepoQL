using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.LLM.Client;

/// <summary>
/// Embedding provider using OpenRouter API with all-MiniLM-L6-v2.
/// Activated when OPENROUTER_API_KEY environment variable is set.
/// </summary>
/// <remarks>
/// <para><strong>Environment Variables:</strong></para>
/// <list type="bullet">
///   <item><c>OPENROUTER_API_KEY</c> - Required. Your OpenRouter API key.</item>
///   <item><c>REPOQL_OPENROUTER_CONCURRENCY</c> - Max concurrent API calls for batch processing (default: 4, max: 16).</item>
/// </list>
/// <para>
/// Batch embedding uses <see cref="Parallel.ForEachAsync"/> to process multiple 100-item batches
/// concurrently, significantly reducing wall-clock time for large embedding jobs.
/// </para>
/// </remarks>
public sealed class OpenRouterEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.Embeddings.OpenRouter");

    private const string Endpoint = "https://openrouter.ai/api/v1/embeddings";
    private const string DefaultModel = "sentence-transformers/all-minilm-l6-v2";
    private const int DefaultDimension = 384;
    private const int MaxBatchSize = 100;
    private const int DefaultTimeoutSeconds = 120;

    private static int GetApiConcurrency()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_OPENROUTER_CONCURRENCY"), out var c) && c > 0)
            return Math.Min(c, 16); // Cap to prevent abuse
        return 4; // Default: 4 concurrent API calls
    }

    private static readonly int ApiConcurrency = GetApiConcurrency();

    private readonly record struct BatchWorkItem(int BatchIndex, int StartIndex, string[] Texts);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public string Model => DefaultModel;
    public int Dimension => DefaultDimension;
    public bool Enabled => !string.IsNullOrEmpty(_apiKey);

    public OpenRouterEmbeddingProvider(
        string? apiKey = null,
        HttpClient? httpClient = null,
        ILogger<OpenRouterEmbeddingProvider>? logger = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        _logger = logger ?? NullLogger<OpenRouterEmbeddingProvider>.Instance;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds) };
            _ownsHttpClient = true;
        }

        if (Enabled)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://repoql.dev");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "RepoQL");
        }
    }

    public async Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var results = await EmbedBatchCoreAsync([text], cancellationToken);
        return results.Length > 0 ? results[0] : null;
    }

    public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
        => EmbedQueryAsync(text, cancellationToken);

    public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(texts, cancellationToken);

    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(texts, cancellationToken);

    public Task<float[]?[]> EmbedPassageBatchAsync(
        IReadOnlyList<string>? texts,
        BatchEmbeddingProgress progress,
        CancellationToken cancellationToken = default)
    {
        // Progress is handled by caller; we just do the embedding
        return EmbedBatchCoreAsync(texts, cancellationToken);
    }

    private async Task<float[]?[]> EmbedBatchCoreAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        if (!Enabled || texts is null || texts.Count == 0)
            return [];

        var allResults = new float[]?[texts.Count];

        // Create indexed batch work items
        var batches = texts
            .Chunk(MaxBatchSize)
            .Select((batch, index) => new BatchWorkItem(
                BatchIndex: index,
                StartIndex: index * MaxBatchSize,
                Texts: batch.ToArray()))
            .ToArray();

        if (batches.Length == 0)
            return allResults;

        // Single batch - no parallelism overhead
        if (batches.Length == 1)
        {
            await ProcessSingleBatchAsync(batches[0], allResults, cancellationToken);
            return allResults;
        }

        // Parallel processing for multiple batches
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = ApiConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(batches, options, async (batch, ct) =>
        {
            await ProcessSingleBatchAsync(batch, allResults, ct);
        });

        return allResults;
    }

    private async Task ProcessSingleBatchAsync(
        BatchWorkItem batch,
        float[]?[] allResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var embeddings = await CallApiAsync(batch.Texts, cancellationToken);

            // Direct array writes are thread-safe (non-overlapping indices)
            for (var i = 0; i < embeddings.Length && batch.StartIndex + i < allResults.Length; i++)
            {
                allResults[batch.StartIndex + i] = embeddings[i];
            }

            _logger.LogDebug("Completed batch {BatchIndex} ({Count} embeddings)",
                batch.BatchIndex, embeddings.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error embedding batch {BatchIndex} of {Count} texts",
                batch.BatchIndex, batch.Texts.Length);
            // Array elements already initialized to null - no action needed
        }
    }

    private async Task<float[][]> CallApiAsync(string[] texts, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("openrouter.embed", ActivityKind.Client);
        activity?.SetTag("embed.provider", "openrouter");
        activity?.SetTag("embed.model", Model);
        activity?.SetTag("embed.count", texts.Length);

        var request = new JsonObject
        {
            ["model"] = Model,
            ["input"] = new JsonArray(texts.Select(t => JsonValue.Create(t)).ToArray())
        };

        var json = request.ToJsonString();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogDebug("Calling OpenRouter embeddings API with {Count} texts", texts.Length);

        var response = await _httpClient.PostAsync(Endpoint, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("OpenRouter embeddings API error {StatusCode}: {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenRouter API error {response.StatusCode}: {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataArray))
        {
            throw new InvalidOperationException("No 'data' array in embeddings response");
        }

        var results = new List<float[]>();
        foreach (var item in dataArray.EnumerateArray())
        {
            if (item.TryGetProperty("embedding", out var embeddingArray))
            {
                var embedding = new float[embeddingArray.GetArrayLength()];
                int i = 0;
                foreach (var val in embeddingArray.EnumerateArray())
                {
                    embedding[i++] = val.GetSingle();
                }
                results.Add(embedding);
            }
        }

        _logger.LogDebug("Received {Count} embeddings from OpenRouter", results.Count);
        return results.ToArray();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
