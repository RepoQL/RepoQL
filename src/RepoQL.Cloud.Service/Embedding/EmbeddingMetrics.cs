using System.Diagnostics.Metrics;

namespace RepoQL.Cloud.Service.Embedding;

/// <summary>
/// Purpose: Central OTEL meter for all embedding service instrumentation.
/// Complexity: Counters and histograms for chunk sources, token accounting,
/// and Voyage API call performance.
/// </summary>
internal sealed class EmbeddingMetrics
{
    public static readonly Meter Meter = new("RepoQL.Embedding.Service");

    private static readonly Counter<long> ChunksCounter =
        Meter.CreateCounter<long>("repoql.embedding.chunks", "chunks",
            "Chunks processed, tagged by source (cache or voyage)");

    private static readonly Counter<long> TokensVoyageCounter =
        Meter.CreateCounter<long>("repoql.embedding.tokens.voyage", "tokens",
            "Actual tokens consumed by Voyage API calls");

    private static readonly Counter<long> TokensCacheSavedCounter =
        Meter.CreateCounter<long>("repoql.embedding.tokens.cache_saved", "tokens",
            "Estimated tokens saved by serving from cache instead of Voyage");

    private static readonly Counter<long> RerankTokensCounter =
        Meter.CreateCounter<long>("repoql.embedding.rerank.tokens", "tokens",
            "Tokens consumed by Voyage rerank API calls");

    private static readonly Histogram<double> VoyageDurationHistogram =
        Meter.CreateHistogram<double>("repoql.embedding.voyage.duration", "ms",
            "Voyage API call duration");

    private const int EstimatedCharsPerToken = 4;

    public static void RecordCacheHits(int chunks, IEnumerable<string> chunkTexts)
    {
        ChunksCounter.Add(chunks, new KeyValuePair<string, object?>("source", "cache"));

        var estimatedTokens = 0L;
        foreach (var text in chunkTexts)
            estimatedTokens += text.Length / EstimatedCharsPerToken;

        if (estimatedTokens > 0)
            TokensCacheSavedCounter.Add(estimatedTokens);
    }

    public static void RecordVoyageChunks(int chunks, int tokens)
    {
        ChunksCounter.Add(chunks, new KeyValuePair<string, object?>("source", "voyage"));
        if (tokens > 0)
            TokensVoyageCounter.Add(tokens);
    }

    public static void RecordVoyageDuration(double milliseconds)
        => VoyageDurationHistogram.Record(milliseconds);

    public static void RecordRerankTokens(int tokens)
    {
        if (tokens > 0)
            RerankTokensCounter.Add(tokens);
    }
}
