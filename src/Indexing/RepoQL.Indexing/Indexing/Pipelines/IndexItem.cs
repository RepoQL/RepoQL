using System.Collections;
using System.Diagnostics.CodeAnalysis;
using RepoQL.Contracts;
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
/// </remarks>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix")]
public sealed class IndexItem(RawArtifact rawArtifact, IndexItemOptions options) : IAnnotatedArtifact
{
    private readonly IDictionary<string, object> _dictionaryImplementation =  new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    ///     Materialized graph records (artifacts, nodes, spans, edges)
    /// </summary>
    public Records? Records { get; set; }

    public T? Get<T>(string key) => _dictionaryImplementation.TryGetValue(key, out var value) 
        ? (T)value 
        : default;
    
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        if (_dictionaryImplementation.TryGetValue(key, out var obj) || obj is not T t)
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

    public bool IsReadOnly => _dictionaryImplementation.IsReadOnly;

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
