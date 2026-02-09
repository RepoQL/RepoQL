using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace RepoQL.Contracts;

/// <summary>
/// In-memory registry of all URIs in the repository with their status.
///
/// Purpose: Central source of truth for what's in the repository and its readiness state.
/// Enables full wildcard glob matching, scope readiness checks, and indexer observability.
///
/// Complexity: Inherits ConcurrentDictionary for thread-safe access. Indexer writes,
/// glob/search reads. FileEntry is immutable, so updates replace entire entries atomically.
/// Readers see consistent state (either old entry or new entry, never partial).
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public class UriRegistry : ConcurrentDictionary<RepoUri, FileEntry>
{
    /// <summary>
    /// Creates an empty URI registry.
    /// </summary>
    public UriRegistry() : base(RepoUriComparer.Instance)
    {
    }

    /// <summary>
    /// Registers a file as discovered. Does not overwrite existing entries.
    /// </summary>
    /// <param name="uri">The file URI.</param>
    /// <returns>True if the file was newly registered, false if it already existed.</returns>
    public bool TryRegisterDiscovered(RepoUri uri)
    {
        return TryAdd(uri, FileEntry.Discovered());
    }

    /// <summary>
    /// Updates a file's status to Indexing.
    /// </summary>
    public void SetIndexing(RepoUri uri)
    {
        AddOrUpdate(
            uri,
            _ => FileEntry.Discovered() with { Status = UriStatus.Indexing },
            (_, existing) => existing with { Status = UriStatus.Indexing });
    }

    /// <summary>
    /// Updates a file's status to Indexed with its symbols and line count.
    /// </summary>
    /// <param name="uri">The file URI.</param>
    /// <param name="lineCount">Total number of lines in the file.</param>
    /// <param name="symbols">Symbol URIs mapped to their entries (kind and span).</param>
    public void SetIndexed(RepoUri uri, int lineCount, IReadOnlyDictionary<RepoUri, SymbolEntry> symbols)
    {
        AddOrUpdate(
            uri,
            _ => new FileEntry(
                Status: UriStatus.Indexed,
                IndexedAt: DateTime.UtcNow,
                Error: null,
                EmbeddingStatus: EmbeddingStatus.Pending,
                EmbeddedChunkCount: 0,
                EmbeddedAt: null,
                LineCount: lineCount,
                Symbols: symbols),
            (_, existing) => existing with
            {
                Status = UriStatus.Indexed,
                IndexedAt = DateTime.UtcNow,
                Error = null,
                LineCount = lineCount,
                Symbols = symbols
            });
    }

    /// <summary>
    /// Updates a file's status to Indexed with its symbols (backward-compatible overload).
    /// Line count defaults to 0 (unavailable), and symbols are converted to SymbolEntry with kind only.
    /// </summary>
    /// <param name="uri">The file URI.</param>
    /// <param name="symbols">Symbol URIs mapped to their kind.</param>
    public void SetIndexed(RepoUri uri, IReadOnlyDictionary<RepoUri, string> symbols)
    {
        var symbolEntries = symbols.ToDictionary(
            kvp => kvp.Key,
            kvp => SymbolEntry.WithKindOnly(kvp.Value))
            .AsReadOnly();

        SetIndexed(uri, lineCount: 0, symbolEntries);
    }

    /// <summary>
    /// Updates a file's status to Failed with an error message.
    /// </summary>
    public void SetFailed(RepoUri uri, string error)
    {
        AddOrUpdate(
            uri,
            _ => FileEntry.WithError(error),
            (_, existing) => existing with
            {
                Status = UriStatus.Failed,
                Error = error
            });
    }

    /// <summary>
    /// Marks a file as Stale (needs re-indexing).
    /// </summary>
    public void SetStale(RepoUri uri)
    {
        AddOrUpdate(
            uri,
            _ => FileEntry.Discovered() with { Status = UriStatus.Stale },
            (_, existing) => existing with
            {
                Status = UriStatus.Stale,
                EmbeddingStatus = EmbeddingStatus.Pending
            });
    }

    /// <summary>
    /// Updates a file's embedding status to Embedding.
    /// </summary>
    public void SetEmbedding(RepoUri uri)
    {
        if (TryGetValue(uri, out var existing))
        {
            TryUpdate(uri, existing with { EmbeddingStatus = EmbeddingStatus.Embedding }, existing);
        }
    }

    /// <summary>
    /// Updates a file's embedding status to Embedded with chunk count.
    /// </summary>
    public void SetEmbedded(RepoUri uri, int chunkCount)
    {
        if (TryGetValue(uri, out var existing))
        {
            TryUpdate(uri, existing with
            {
                EmbeddingStatus = EmbeddingStatus.Embedded,
                EmbeddedChunkCount = chunkCount,
                EmbeddedAt = DateTime.UtcNow
            }, existing);
        }
    }

    /// <summary>
    /// Updates a file's embedding status to Failed.
    /// </summary>
    public void SetEmbeddingFailed(RepoUri uri, string error)
    {
        if (TryGetValue(uri, out var existing))
        {
            TryUpdate(uri, existing with
            {
                EmbeddingStatus = EmbeddingStatus.Failed,
                Error = existing.Error ?? error
            }, existing);
        }
    }

    /// <summary>
    /// Marks a file as not applicable for embedding (e.g., binary files).
    /// </summary>
    public void SetEmbeddingNotApplicable(RepoUri uri)
    {
        if (TryGetValue(uri, out var existing))
        {
            TryUpdate(uri, existing with { EmbeddingStatus = EmbeddingStatus.NotApplicable }, existing);
        }
    }

    /// <summary>
    /// Marks a file as up-to-date (skipped by indexer because content unchanged).
    /// Transitions Discovered/Pending to Indexed/NotApplicable so Operation completion tracking works.
    /// Does not downgrade files already in a terminal or more advanced embedding state.
    /// </summary>
    public void SetSkippedUpToDate(RepoUri uri)
    {
        if (!TryGetValue(uri, out var existing))
            return;

        // Don't downgrade files already in a terminal embedding state
        if (existing.Status == UriStatus.Indexed && existing.EmbeddingStatus != EmbeddingStatus.Pending)
            return;

        TryUpdate(uri, existing with
        {
            Status = UriStatus.Indexed,
            EmbeddingStatus = existing.EmbeddingStatus == EmbeddingStatus.Pending
                ? EmbeddingStatus.NotApplicable
                : existing.EmbeddingStatus
        }, existing);
    }

    /// <summary>
    /// Removes a file and returns its entry if it existed.
    /// </summary>
    public FileEntry? RemoveFile(RepoUri uri)
    {
        return TryRemove(uri, out var entry) ? entry : null;
    }

    /// <summary>
    /// Gets all file URIs (not including symbols).
    /// </summary>
    public IEnumerable<RepoUri> FileUris => Keys;

    /// <summary>
    /// Gets all entries with their URIs.
    /// </summary>
    public IEnumerable<KeyValuePair<RepoUri, FileEntry>> FileEntries => this;

    /// <summary>
    /// Gets files by status.
    /// </summary>
    public IEnumerable<RepoUri> GetByStatus(UriStatus status)
    {
        return this.Where(kv => kv.Value.Status == status).Select(kv => kv.Key);
    }

    /// <summary>
    /// Gets files by embedding status.
    /// </summary>
    public IEnumerable<RepoUri> GetByEmbeddingStatus(EmbeddingStatus status)
    {
        return this.Where(kv => kv.Value.EmbeddingStatus == status).Select(kv => kv.Key);
    }

    /// <summary>
    /// Comparer for RepoUri that uses case-insensitive AbsoluteUri comparison.
    /// </summary>
    private sealed class RepoUriComparer : IEqualityComparer<RepoUri>
    {
        public static readonly RepoUriComparer Instance = new();

        public bool Equals(RepoUri? x, RepoUri? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.AbsoluteUri, y.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(RepoUri obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
        }
    }
}
