using System.Collections.Concurrent;
using RepoQL.Contracts;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Core;

public sealed class AnalysisWorkspace(
    IMultiFileSystem fileSystem,
    IFileClassifier classifier,
    IHasher hasher,
    IFormatRegistry registry)
    : IAnalysisWorkspace
{
    private readonly IMultiFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IFileClassifier _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    private readonly IHasher _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
    private readonly IFormatRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ConcurrentDictionary<string, DocumentModel?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DocumentModel?> LoadAsync(RepoUri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var cacheKey = uri.AbsoluteUri;
        if (_cache.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        var file = _fileSystem.GetFile(uri);
        if (!file.Exists)
        {
            _cache.TryAdd(cacheKey, null);
            return null;
        }

        var artifact = new DiscoveredArtifact
        {
            File = file,
            RepoUri = uri,
            MediaType = _classifier.GetMediaType(file)
        };
        artifact.Hash = await _hasher.HashAsync(file, cancellationToken).ConfigureAwait(false);

        if (artifact.MediaType is not null && _registry.TryResolveByMedia(artifact.MediaType, out var directDescriptor))
        {
            var doc = await LoadWithDescriptorAsync(directDescriptor, artifact, cancellationToken).ConfigureAwait(false);
            _cache.TryAdd(cacheKey, doc);
            return doc;
        }

        foreach (var descriptor in _registry.Formats)
        {
            if (!await descriptor.Loader.CanLoadAsync(artifact, cancellationToken).ConfigureAwait(false))
                continue;
            var doc = await descriptor.Loader.LoadAsync(artifact, cancellationToken).ConfigureAwait(false);
            _cache.TryAdd(cacheKey, doc);
            return doc;
        }

        _cache.TryAdd(cacheKey, null);
        return null;
    }

    public async Task<IReadOnlyList<EmbeddedFragment>> DiscoverEmbedsAsync(DocumentModel document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_registry.TryResolveByMedia(document.MediaType, out var descriptor))
        {
            var list = new List<EmbeddedFragment>();
            await foreach (var fragment in descriptor.Loader.DiscoverEmbedsAsync(document, cancellationToken).ConfigureAwait(false))
            {
                list.Add(fragment);
            }
            return list;
        }

        return [];
    }

    private async Task<DocumentModel> LoadWithDescriptorAsync(FormatDescriptor descriptor, DiscoveredArtifact artifact, CancellationToken cancellationToken)
    {
        if (!await descriptor.Loader.CanLoadAsync(artifact, cancellationToken).ConfigureAwait(false))
        {
            // Loader declined this artifact; fall back to manual loop
            foreach (var candidate in _registry.Formats)
            {
                if (!await candidate.Loader.CanLoadAsync(artifact, cancellationToken).ConfigureAwait(false))
                    continue;
                return await candidate.Loader.LoadAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
        }

        return await descriptor.Loader.LoadAsync(artifact, cancellationToken).ConfigureAwait(false);
    }
}
