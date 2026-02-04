namespace RepoQL.Contracts;

/// <summary>
/// A timestamped log entry for an operation.
/// </summary>
/// <param name="Timestamp">When this entry was logged.</param>
/// <param name="Type">Entry type: created, file_indexed, file_embedded, file_ready, file_failed, embedding_failed, completed, cancelled.</param>
/// <param name="Message">Optional message (e.g., error details, summary).</param>
/// <param name="Uri">Optional URI this entry relates to.</param>
public record OperationEntry(
    DateTimeOffset Timestamp,
    string Type,
    string? Message,
    RepoUri? Uri)
{
    /// <summary>Entry type: operation started.</summary>
    public const string TypeCreated = "created";

    /// <summary>Entry type: file finished indexing.</summary>
    public const string TypeFileIndexed = "file_indexed";

    /// <summary>Entry type: file has structure embedding.</summary>
    public const string TypeFileEmbedded = "file_embedded";

    /// <summary>Entry type: file ready (embedding not applicable).</summary>
    public const string TypeFileReady = "file_ready";

    /// <summary>Entry type: indexing failed.</summary>
    public const string TypeFileFailed = "file_failed";

    /// <summary>Entry type: embedding failed.</summary>
    public const string TypeEmbeddingFailed = "embedding_failed";

    /// <summary>Entry type: all files terminal.</summary>
    public const string TypeCompleted = "completed";

    /// <summary>Entry type: operation cancelled.</summary>
    public const string TypeCancelled = "cancelled";
}
