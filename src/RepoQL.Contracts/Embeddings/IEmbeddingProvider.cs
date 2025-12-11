namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Progress information for batch embedding operations.
/// </summary>
/// <param name="BatchNumber">Current batch number (1-based)</param>
/// <param name="TotalBatches">Total number of batches</param>
/// <param name="ItemsProcessed">Number of items processed so far (including current batch)</param>
/// <param name="TotalItems">Total number of items to process</param>
/// <param name="ElapsedTime">Time elapsed since start of embedding operation</param>
public readonly record struct BatchEmbeddingProgress(int BatchNumber, int TotalBatches, int ItemsProcessed, int TotalItems, TimeSpan ElapsedTime)
{
    /// <summary>
    /// Estimates remaining time based on current progress and elapsed time.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (ItemsProcessed <= 0 || TotalItems <= 0 || ElapsedTime <= TimeSpan.Zero)
                return null;

            var remainingItems = TotalItems - ItemsProcessed;
            if (remainingItems <= 0)
                return TimeSpan.Zero;

            var msPerItem = ElapsedTime.TotalMilliseconds / ItemsProcessed;
            return TimeSpan.FromMilliseconds(msPerItem * remainingItems);
        }
    }

    /// <summary>
    /// Gets the completion percentage (0-100).
    /// </summary>
    public int PercentComplete => TotalItems > 0 ? (int)(ItemsProcessed * 100.0 / TotalItems) : 0;
}

public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimension { get; }
    bool Enabled { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed a batch with progress reporting.
    /// </summary>
    Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
        => EmbedBatchAsync(texts, cancellationToken); // Default implementation ignores progress
}
