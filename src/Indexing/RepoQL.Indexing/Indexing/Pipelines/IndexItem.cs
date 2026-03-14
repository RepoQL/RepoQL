using System.Collections;
using System.Diagnostics.CodeAnalysis;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Indexing.Pipelines;

/// <summary>
/// Flow object that accumulates state as it moves through the indexing pipeline.
/// Represents a single file's journey from discovery to committed graph structure.
/// </summary>
/// <remarks>
/// <para><strong>Flow Object Pattern</strong></para>
/// <para>
/// Unlike functional pipelines where immutable data passes between stages, IndexItem is
/// mutable and carries all state forward. Each stage adds information:
/// </para>
/// <list type="bullet">
/// <item><description>Classification: Sets <see cref="MediaType"/></description></item>
/// <item><description>Parsing: Sets <see cref="Records"/> (graph structure)</description></item>
/// <item><description>Analysis: Adds to <see cref="AnnotationsList"/> (lint warnings, etc.)</description></item>
/// <item><description>Commit: Persists to database</description></item>
/// </list>
///
/// <para><strong>Lifecycle</strong></para>
/// <para>
/// Created in <c>IndexingEngine.EnqueueItemAsync</c>, stamped with epoch number, flows through
/// hot-path stages sequentially (within a single worker thread), then scheduled for idle processing.
/// </para>
///
/// <para><strong>Property Bag</strong></para>
/// <para>
/// Implements <c>IDictionary&lt;string, object&gt;</c> for processors to attach custom data.
/// Use <see cref="Get{T}"/> to retrieve typed values.
/// </para>
///
/// <para><strong>Thread Safety</strong></para>
/// <para>
/// Not thread-safe. Single worker processes each item through all hot-path stages sequentially.
/// </para>
/// <para><strong>Equality</strong></para>
/// <para>
/// Index items intentionally do not override equality; deduplication is handled by
/// <see cref="Indexing.IndexingEngine.IndexItemComparer"/> so queue behavior is explicit.
/// </para>
/// </remarks>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix")]
public sealed class IndexItem(RawArtifact rawArtifact, IndexItemOptions options) : IAnnotatedArtifact
{
    private readonly IDictionary<string, object> _dictionaryImplementation =  new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    private int _epochCompletionState;
    private int _timedOutState;
    private int _timeoutAttempts;
    private int _activeHotPathStageCleanupState = 1;
    private int _activeHotPathBusyFlag;
    private int _activeHotPathIdleFlag;
    private int _skipEpochCompletionState;
    private int _deferredRetryState;
    private string? _currentOperation;

    /// <summary>
    ///     The status of the item. Anything besides null is considered a final, terminal state
    /// </summary>
    public PipelineResult? Status { get; set; }

    /// <summary>
    ///    The raw artifact that was discovered,
    ///    contains everything that can be determined from the raw file without specialized parsers
    /// </summary>
    public RawArtifact RawArtifact => rawArtifact;
    
    /// <summary>
    ///  The options used when enqueuing the item
    /// </summary>
    public IndexItemOptions Options => options;
    
    /// <summary>
    ///   The RepoUri that uniquely identifies this artifact
    /// </summary>
    public RepoUri Uri => rawArtifact.Uri;
    public bool IsReadOnly => rawArtifact.IsReadOnly;

    /// <summary>
    /// UTC timestamp captured when this item is created and enqueued for processing.
    /// Used by diagnostics to calculate queue age.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>
    ///     Resolved semantic media type (should be populated after classification)
    /// </summary>
    public SemanticMediaType? MediaType { get; set; }

    /// <summary>
    ///     Hex-encoded content digest computed from the file system.
    /// </summary>
    public string? DigestHex { get; set; }

    /// <summary>
    ///     Catalog state that existed prior to processing (if any).
    /// </summary>
    public DocumentCatalogEntry? ExistingEntry { get; set; }

    internal long Epoch { get; private set; } = -1;

    internal bool TryMarkEpochComplete() => Interlocked.Exchange(ref _epochCompletionState, 1) == 0;

    internal bool IsTimedOut => Volatile.Read(ref _timedOutState) == 1;

    internal bool TryMarkTimedOut() => Interlocked.Exchange(ref _timedOutState, 1) == 0;

    internal int TimeoutAttempts => Volatile.Read(ref _timeoutAttempts);

    internal int IncrementTimeoutAttempts() => Interlocked.Increment(ref _timeoutAttempts);

    internal bool SkipEpochCompletion => Volatile.Read(ref _skipEpochCompletionState) == 1;

    internal bool IsDeferredRetry => Volatile.Read(ref _deferredRetryState) == 1;

    internal string CurrentOperation => Volatile.Read(ref _currentOperation) ?? string.Empty;

    internal void SetCurrentOperation(string? operation)
        => Volatile.Write(ref _currentOperation, operation);

    internal void MarkDeferredRetry()
    {
        Volatile.Write(ref _deferredRetryState, 1);
        Volatile.Write(ref _skipEpochCompletionState, 1);
    }

    internal void MarkSkipEpochCompletion()
        => Volatile.Write(ref _skipEpochCompletionState, 1);

    internal void TrackHotPathStage(IndexingState busyFlag, IndexingState idleFlag)
    {
        Volatile.Write(ref _activeHotPathBusyFlag, (int)busyFlag);
        Volatile.Write(ref _activeHotPathIdleFlag, (int)idleFlag);
        Volatile.Write(ref _activeHotPathStageCleanupState, 0);
    }

    internal bool TryClaimHotPathStageCleanup(out IndexingState busyFlag, out IndexingState idleFlag)
    {
        if (Interlocked.Exchange(ref _activeHotPathStageCleanupState, 1) != 0)
        {
            busyFlag = default;
            idleFlag = default;
            return false;
        }

        busyFlag = (IndexingState)Volatile.Read(ref _activeHotPathBusyFlag);
        idleFlag = (IndexingState)Volatile.Read(ref _activeHotPathIdleFlag);
        return busyFlag != default || idleFlag != default;
    }

    internal void ClearHotPathStageTracking()
    {
        Volatile.Write(ref _activeHotPathBusyFlag, 0);
        Volatile.Write(ref _activeHotPathIdleFlag, 0);
    }

    /// <summary>
    /// Release heavy payload data after commit to DuckDB.
    /// The property bag holds DocumentModel (full file text, syntax trees) and other
    /// processor-specific data that is only needed during hot-path stages. Records and
    /// lightweight metadata are preserved for idle processing (pruning, embedding, analysis).
    /// </summary>
    internal void ReleasePostCommitPayload()
    {
        _dictionaryImplementation.Clear();
        AnnotationsList.Clear();
        StructureEmbedding = null;
        ExistingEntry = null;
    }

    /// <summary>
    /// Release all remaining heavyweight data after idle processing completes.
    /// Records are still needed during idle stages (vector refresh, multi-file analysis)
    /// but can be freed once all idle work is done.
    /// </summary>
    internal void ReleasePostIdlePayload()
    {
        Records = null;
    }

    /// <summary>
    ///     Materialized graph records (artifacts, nodes, spans, edges)
    /// </summary>
    public Records? Records { get; set; }

    /// <summary>
    ///     Optional structure embedding generated in the hot path and persisted at commit time.
    /// </summary>
    public DocumentEmbedding? StructureEmbedding { get; set; }

    /// <summary>
    ///     Vendor/library/minified files get lightweight parsing (content searchable, minimal graph structure).
    ///     Set by <see cref="Indexing.IndexingEngine"/> based on URI patterns before parsing stage.
    /// </summary>
    public bool IsLightweight { get; set; }

    /// <summary>
    /// Checks if a URI matches patterns for lightweight parsing: vendor libraries, minified files,
    /// source maps, and lock files. These are still searchable but don't need full AST structure.
    /// </summary>
    internal static bool MatchesLightweightPattern(string uriString)
    {
        // Vendor/library paths
        if (uriString.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase) ||
            uriString.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
            uriString.Contains("/vendor/", StringComparison.OrdinalIgnoreCase) ||
            uriString.Contains("/bower_components/", StringComparison.OrdinalIgnoreCase) ||
            uriString.Contains("/third_party/", StringComparison.OrdinalIgnoreCase) ||
            uriString.Contains("/third-party/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Minified files
        if (uriString.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith(".min.map", StringComparison.OrdinalIgnoreCase))
            return true;

        // Source maps (huge, low structural value)
        if (uriString.EndsWith(".css.map", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith(".js.map", StringComparison.OrdinalIgnoreCase))
            return true;

        // Bundle files
        if (uriString.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith(".bundle.css", StringComparison.OrdinalIgnoreCase))
            return true;

        // Lock files
        if (uriString.EndsWith("/package-lock.json", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith("/yarn.lock", StringComparison.OrdinalIgnoreCase) ||
            uriString.EndsWith("/pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public T? Get<T>(string key) => _dictionaryImplementation.TryGetValue(key, out var value) 
        ? (T)value 
        : default;
    
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        if (!_dictionaryImplementation.TryGetValue(key, out var obj) || obj is not T t)
            return false;
        value = t;
        return true;
    }
    
    /// <summary>Annotations produced by analyzers (internal list)</summary>
    internal List<Annotation> AnnotationsList { get; } = [];

    /// <summary>Annotations produced by analyzers (read-only view for processors)</summary>
    IReadOnlyList<Annotation> IAnnotatedArtifact.Annotations => AnnotationsList;

    #region Raw Properties
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return _dictionaryImplementation.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)_dictionaryImplementation).GetEnumerator();
    }

    public void Add(KeyValuePair<string, object> item)
    {
        _dictionaryImplementation.Add(item);
    }

    public void Clear()
    {
        _dictionaryImplementation.Clear();
    }

    public bool Contains(KeyValuePair<string, object> item)
    {
        return _dictionaryImplementation.Contains(item);
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        _dictionaryImplementation.CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        return _dictionaryImplementation.Remove(item);
    }

    public int Count => _dictionaryImplementation.Count;

    public void Add(string key, object value)
    {
        _dictionaryImplementation.Add(key, value);
    }

    public bool ContainsKey(string key)
    {
        return _dictionaryImplementation.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        return _dictionaryImplementation.Remove(key);
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value)
    {
        return _dictionaryImplementation.TryGetValue(key, out value);
    }

    public object this[string key]
    {
        get => _dictionaryImplementation[key];
        set => _dictionaryImplementation[key] = value;
    }

    public ICollection<string> Keys => _dictionaryImplementation.Keys;

    public ICollection<object> Values => _dictionaryImplementation.Values;
    #endregion

    #region IFileInfo
    public Stream CreateReadStream()
    {
        return rawArtifact.CreateReadStream();
    }

    public bool Exists => rawArtifact.Exists;

    public long Length => rawArtifact.Length;

    public string? PhysicalPath => rawArtifact.PhysicalPath;

    public string Name => rawArtifact.Name;

    public DateTimeOffset LastModified => rawArtifact.LastModified;

    public bool IsDirectory => rawArtifact.IsDirectory;
    #endregion

    internal void SetEpoch(long epoch)
    {
        Epoch = epoch;
    }
}
