using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.LLM.Client;

/// <summary>
/// Embedding provider using OpenRouter API with e5-large-v2.
/// Activated when OPENROUTER_API_KEY environment variable is set.
/// </summary>
public sealed class OpenRouterEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const string Endpoint = "https://openrouter.ai/api/v1/embeddings";
    private const string DefaultModel = "intfloat/e5-large-v2";
    private const int DefaultDimension = 1024;
    private const int MaxBatchSize = 100;
    private const int DefaultTimeoutSeconds = 120;

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

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var results = await EmbedBatchAsync([text], cancellationToken);
        return results.Length > 0 ? results[0] : null;
    }

    public async Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        if (!Enabled || texts is null || texts.Count == 0)
            return [];

        var allResults = new float[]?[texts.Count];
        var batches = texts.Chunk(MaxBatchSize).ToArray();
        var resultIndex = 0;

        foreach (var batch in batches)
        {
            try
            {
                var embeddings = await CallApiAsync(batch.ToArray(), cancellationToken);
                foreach (var embedding in embeddings)
                {
                    allResults[resultIndex++] = embedding;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error embedding batch of {Count} texts", batch.Length);
                // Fill with nulls for failed batch
                for (int i = 0; i < batch.Length; i++)
                {
                    allResults[resultIndex++] = null;
                }
            }
        }

        return allResults;
    }

    public async Task<float[]?[]> EmbedBatchAsync(
        IReadOnlyList<string>? texts,
        BatchEmbeddingProgress progress,
        CancellationToken cancellationToken = default)
    {
        // Progress is handled by caller; we just do the embedding
        return await EmbedBatchAsync(texts, cancellationToken);
    }

    private async Task<float[][]> CallApiAsync(string[] texts, CancellationToken ct)
    {
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
