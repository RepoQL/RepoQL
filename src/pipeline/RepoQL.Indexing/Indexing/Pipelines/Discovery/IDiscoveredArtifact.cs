using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Indexing.Indexing.Pipelines.Discovery;

public interface IDiscoveredArtifact : IDictionary<string, object>, IFileInfo
{
    /// <summary>
    ///    The raw artifact that was discovered,
    ///    contains everything that can be determined from the raw file without specialized parsers
    /// </summary>
    public RawArtifact RawArtifact { get; }

    /// <summary>
    ///  The options used when enqueuing the item
    /// </summary>
    public IndexItemOptions Options { get; }
    
    /// <summary>
    ///   The RepoUri that uniquely identifies this artifact
    /// </summary>
    public RepoUri Uri  { get; }

    /// <summary>
    ///     Gets a property with the given key and converts it to <typeparamref name="T"/>, returning null if it cant be found or converted
    /// </summary>
    /// <param name="key">The key</param>
    /// <typeparam name="T">The type of the stored value</typeparam>
    /// <returns>The value, or null</returns>
    public T? Get<T>(string key);

    /// <summary>
    ///   Attempts to get the value specified by key, and converts it to <typeparamref name="T"/>
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value, or default if it could not ber retrieved</param>
    /// <typeparam name="T">The type of the stored value</typeparam>
    /// <returns>True if the key could be found and the value converted</returns>
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value);
}