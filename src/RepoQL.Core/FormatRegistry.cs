using System.Collections.Concurrent;
using RepoQL.Contracts;

namespace RepoQL.Core;

public sealed class FormatRegistry : IFormatRegistry
{
    private readonly IReadOnlyList<FormatDescriptor> _formats;
    private readonly ConcurrentDictionary<string, FormatDescriptor> _byMedia;
    private readonly ConcurrentDictionary<string, FormatDescriptor> _byLabel;

    public FormatRegistry(IEnumerable<FormatDescriptor> descriptors)
    {
        var list = descriptors?.ToList() ?? throw new ArgumentNullException(nameof(descriptors));
        _formats = list;
        _byMedia = new ConcurrentDictionary<string, FormatDescriptor>(StringComparer.OrdinalIgnoreCase);
        _byLabel = new ConcurrentDictionary<string, FormatDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in list)
        {
            _byMedia.TryAdd(descriptor.MediaType.ToString(), descriptor);
            foreach (var label in descriptor.Labels)
            {
                _byLabel.TryAdd(label, descriptor);
            }
        }
    }

    public IEnumerable<FormatDescriptor> Formats => _formats;

    public bool TryResolveByMedia(SemanticMediaType mediaType, out FormatDescriptor descriptor)
    {
        if (_byMedia.TryGetValue(mediaType.ToString(), out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = default!;
        return false;
    }

    public bool TryResolveByLabel(string label, out FormatDescriptor descriptor)
    {
        if (label is null)
        {
            descriptor = default!;
            return false;
        }

        if (_byLabel.TryGetValue(label, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = default!;
        return false;
    }
}
