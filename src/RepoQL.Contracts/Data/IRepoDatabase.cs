using System.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Data;

/// <summary>
/// Unified database interface for RepoQL. Handles both reads and writes
/// with internal locking to ensure single-writer semantics.
/// </summary>
/// <remarks>
/// <para>
/// Architecture: Two DuckDB connections (read-only + read-write) with a
/// <see cref="System.Threading.ReaderWriterLockSlim"/> for concurrency control.
/// Multiple concurrent readers allowed; writers get exclusive access.
/// </para>
/// <para>
/// All methods are synchronous - DuckDB is embedded, async overhead is waste.
/// </para>
/// </remarks>
public interface IRepoDatabase : IDisposable
{
    /// <summary>
    /// Ensures schema (tables, indexes, macros, UDFs) exists. Idempotent.
    /// Call once at startup.
    /// </summary>
    void EnsureSchema();

    /// <summary>
    /// Execute a SQL query and return results as dictionaries.
    /// This is the primary read interface for agents.
    /// </summary>
    /// <remarks>
    /// Uses the read-only connection. Multiple concurrent queries allowed.
    /// Results are materialized before returning (no deferred execution).
    /// </remarks>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql);

    /// <summary>
    /// Execute a SQL query with a custom row mapper.
    /// </summary>
    IReadOnlyList<T> Query<T>(string sql, Func<IDataRecord, T> mapper);

    /// <summary>
    /// Index a parsed artifact (content + nodes + spans + edges).
    /// Replaces any existing artifact at the same URI.
    /// </summary>
    /// <returns>Result with document ID and whether it was an update.</returns>
    /// <exception cref="Exception">Throws on database errors.</exception>
    IndexResult IndexArtifact(RepoUri uri, ParsedArtifact artifact);

    /// <summary>
    /// Index multiple artifacts in a single transaction for better performance.
    /// </summary>
    IReadOnlyList<IndexResult> IndexArtifactBatch(IReadOnlyList<(RepoUri Uri, ParsedArtifact Artifact)> items);

    /// <summary>
    /// Delete an artifact and its entire subtree by URI.
    /// </summary>
    /// <returns>True if artifact was found and deleted; false if not found.</returns>
    bool DeleteArtifact(RepoUri uri);

    /// <summary>
    /// Replace annotations for an artifact. Deletes existing annotations from
    /// the specified sources, then inserts the new ones.
    /// </summary>
    /// <param name="artifactUri">URI of the artifact to update.</param>
    /// <param name="annotations">New annotations to insert.</param>
    /// <param name="sourcesToClear">Sources to clear before inserting. If null, inferred from annotations.</param>
    /// <returns>True if artifact was found; false if not found.</returns>
    bool ReplaceAnnotations(RepoUri artifactUri, IReadOnlyList<Annotation> annotations, IReadOnlyCollection<string>? sourcesToClear = null);

    /// <summary>
    /// Write embeddings in batch. Supports both structure and full embeddings,
    /// document and object scope, and chunked content.
    /// </summary>
    /// <remarks>
    /// Uses upsert semantics based on (doc_id, node_id, chunk_index, embedding_type).
    /// </remarks>
    void WriteEmbeddings(IReadOnlyList<DocumentEmbedding> embeddings);
}
