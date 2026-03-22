using System.Diagnostics;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Data;

public sealed record WriteOperation
{
    public required Guid Id { get; init; }
    public required WriteOperationType Type { get; init; }
    public required RepoUri Uri { get; init; }
    public required Records ParsedData { get; init; }
    /// <summary>
    ///     Optional parent activity context so DB write participates in the same distributed trace.
    /// </summary>
    public ActivityContext? ParentContext { get; init; }
    public Func<WriteOperation, CommitResult, Task>? OnCommitted { get; init; }
    /// <summary>
    ///     Optional cancellation token for operations that support cancellation (e.g., Checkpoint).
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     Structure embeddings to write (for WriteStructureEmbeddings operation type).
    /// </summary>
    public IReadOnlyList<StructureEmbeddingData>? StructureEmbeddings { get; init; }
}

/// <summary>
/// Data for a single structure embedding to be written.
/// </summary>
public sealed record StructureEmbeddingData(
    Guid DocId,
    Guid NodeId,
    string Uri,
    float[] Embedding,
    string Model,
    int Dimension);