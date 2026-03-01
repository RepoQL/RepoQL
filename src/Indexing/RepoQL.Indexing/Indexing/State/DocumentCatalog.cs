using System.Collections.Concurrent;
using RepoQL.Contracts;

namespace RepoQL.Indexing.Indexing.State;

/// <summary>
/// Contract for document catalog operations.
/// </summary>
public interface IDocumentCatalog
{
    /// <summary>
    /// Hydrates catalog from storage. Safe to call multiple times (idempotent).
    /// </summary>
    Task EnsureInitializedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Determines if file needs indexing based on digest comparison.
    /// </summary>
    /// <returns>
    /// <see cref="DocumentCatalogDecision.SkipUpToDate"/> if digest matches committed state,
    /// <see cref="DocumentCatalogDecision.Reindex"/> if digest differs,
    /// <see cref="DocumentCatalogDecision.Unknown"/> if file never indexed.
    /// </returns>
    DocumentCatalogEvaluation Evaluate(RepoUri uri, string digestHex);

    /// <summary>
    /// Registers file as pending processing. Prevents duplicate work if same file
    /// enqueued twice before commit completes.
    /// </summary>
    void BeginProcessing(RepoUri uri, string digestHex);

    /// <summary>
    /// Clears pending state. Called when processing completes (success or error).
    /// </summary>
    void CompleteProcessing(RepoUri uri);

    /// <summary>
    /// Updates catalog with committed entry. MUST only be called from OnCommitted callback
    /// after database write succeeds. Clears pending state.
    /// </summary>
    void ApplyUpsert(DocumentCatalogEntry entry);

    /// <summary>
    /// Removes entry from catalog. MUST only be called from OnCommitted callback
    /// after database delete succeeds. Clears pending state.
    /// </summary>
    void ApplyDelete(RepoUri uri);

    /// <summary>
    /// Total committed entries in the catalog.
    /// </summary>
    int EntryCount { get; }

    /// <summary>
    /// Files currently being processed (pending digest computations).
    /// </summary>
    int PendingDigestCount { get; }
}

/// <summary>
/// Result of <see cref="IDocumentCatalog.Evaluate"/> operation.
/// </summary>
public readonly record struct DocumentCatalogEvaluation(
    DocumentCatalogDecision Decision,
    DocumentCatalogEntry? Existing);

/// <summary>
/// Three-state decision model for catalog evaluation.
/// </summary>
public enum DocumentCatalogDecision
{
    /// <summary>File never indexed before.</summary>
    Unknown,

    /// <summary>Digest matches committed state - no work needed.</summary>
    SkipUpToDate,

    /// <summary>Digest differs from committed state - reindex required.</summary>
    Reindex
}

/// <summary>
/// In-memory index of committed documents. Enables incremental indexing through
/// digest-based change detection.
/// </summary>
/// <remarks>
/// <para><strong>Incremental Indexing</strong></para>
/// <para>
/// Stores digest (xxHash64) for each committed document. On next indexing pass,
/// compares new digest with stored value. If match → skip. If differ → reindex.
/// </para>
///
/// <para><strong>Pending Digests</strong></para>
/// <para>
/// Tracks files currently being processed via <see cref="BeginProcessing"/>.
/// If same file enqueued twice with same digest before first completes,
/// second <see cref="Evaluate"/> returns <see cref="DocumentCatalogDecision.SkipUpToDate"/>
/// immediately (prevents duplicate work).
/// </para>
///
/// <para><strong>Update Protocol</strong></para>
/// <list type="number">
/// <item><description>Call <see cref="Evaluate"/> to check if work needed</description></item>
/// <item><description>If Reindex or Unknown, call <see cref="BeginProcessing"/></description></item>
/// <item><description>Process item through pipeline</description></item>
/// <item><description>In WriteOperation.OnCommitted callback, call <see cref="ApplyUpsert"/></description></item>
/// </list>
/// <para>
/// This ensures catalog reflects committed state (not in-progress state).
/// </para>
///
/// <para><strong>Thread Safety</strong></para>
/// <para>
/// Uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for thread-safe reads and updates.
/// </para>
///
/// <para><strong>Future: Persistence</strong></para>
/// <para>
/// Currently hydrates from database on startup. Future: Persist snapshots to disk for faster cold start.
/// </para>
/// </remarks>
public sealed class DocumentCatalog(IDocumentCatalogDataSource dataSource) : IDocumentCatalog
{
    private readonly IDocumentCatalogDataSource _dataSource = dataSource;
    private readonly ConcurrentDictionary<string, DocumentCatalogEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingDigests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private Task? _initialization;

    /// <inheritdoc />
    public int EntryCount => _entries.Count;

    /// <inheritdoc />
    public int PendingDigestCount => _pendingDigests.Count;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        var task = Volatile.Read(ref _initialization);
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = _initialization;
            if (task is null)
            {
                task = LoadAsync();
                Volatile.Write(ref _initialization, task);
            }
        }
        finally
        {
            _initializationGate.Release();
        }

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public DocumentCatalogEvaluation Evaluate(RepoUri uri, string digestHex)
    {
        var key = GetKey(uri);

        if (_pendingDigests.TryGetValue(key, out var pendingDigest) &&
            string.Equals(pendingDigest, digestHex, StringComparison.OrdinalIgnoreCase))
        {
            return new DocumentCatalogEvaluation(DocumentCatalogDecision.SkipUpToDate, null);
        }

        if (_entries.TryGetValue(key, out var existing))
        {
            if (string.Equals(existing.Digest, digestHex, StringComparison.OrdinalIgnoreCase))
            {
                return new DocumentCatalogEvaluation(DocumentCatalogDecision.SkipUpToDate, existing);
            }

            return new DocumentCatalogEvaluation(DocumentCatalogDecision.Reindex, existing);
        }

        return new DocumentCatalogEvaluation(DocumentCatalogDecision.Unknown, null);
    }

    public void BeginProcessing(RepoUri uri, string digestHex)
    {
        var key = GetKey(uri);
        _pendingDigests[key] = digestHex;
    }

    public void CompleteProcessing(RepoUri uri)
    {
        var key = GetKey(uri);
        _pendingDigests.TryRemove(key, out _);
    }

    public void ApplyUpsert(DocumentCatalogEntry entry)
    {
        var key = GetKey(entry.Uri);
        _entries[key] = entry;
        _pendingDigests.TryRemove(key, out _);
        // Future: persist snapshots of _entries for faster cold start once format coverage expands.
    }

    public void ApplyDelete(RepoUri uri)
    {
        var key = GetKey(uri);
        _entries.TryRemove(key, out _);
        _pendingDigests.TryRemove(key, out _);
    }

    private async Task LoadAsync()
    {
        var entries = await _dataSource.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var key = GetKey(entry.Uri);
            _entries[key] = entry;
        }
    }

    private static string GetKey(RepoUri uri)
    {
        return RepoUri.NormalizeContainerKey(uri);
    }
}
