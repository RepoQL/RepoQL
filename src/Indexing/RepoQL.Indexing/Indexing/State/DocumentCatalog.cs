using System.Collections.Concurrent;
using RepoQL.Contracts;

namespace RepoQL.Indexing.Indexing.State;

public interface IDocumentCatalog
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
    DocumentCatalogEvaluation Evaluate(RepoUri uri, string digestHex);
    void BeginProcessing(RepoUri uri, string digestHex);
    void CompleteProcessing(RepoUri uri);
    void ApplyUpsert(DocumentCatalogEntry entry);
    void ApplyDelete(RepoUri uri);
}

public readonly record struct DocumentCatalogEvaluation(
    DocumentCatalogDecision Decision,
    DocumentCatalogEntry? Existing);

public enum DocumentCatalogDecision
{
    Unknown,
    SkipUpToDate,
    Reindex
}

/// <summary>
/// In-memory index of committed documents. Hydrates from storage on demand, supports
/// change detection for incremental indexing, and will be extended to persist snapshots
/// once warm-start latency becomes a concern.
/// </summary>
public sealed class DocumentCatalog(IDocumentCatalogDataSource dataSource) : IDocumentCatalog
{
    private readonly IDocumentCatalogDataSource _dataSource = dataSource;
    private readonly ConcurrentDictionary<string, DocumentCatalogEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingDigests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private Task? _initialization;

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

    private static string GetKey(RepoUri uri) => uri.AbsoluteUri;
}
