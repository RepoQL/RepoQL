using System.Collections.Immutable;

namespace RepoQL.Contracts;

public sealed class FormatDescriptor
{
    public FormatDescriptor(
        SemanticMediaType mediaType,
        IFormatLoader loader,
        IFormatAnalyzer analyzer,
        IFormatMaterializer? materializer = null,
        IEnumerable<string>? labels = null)
    {
        MediaType = mediaType ?? throw new ArgumentNullException(nameof(mediaType));
        Loader = loader ?? throw new ArgumentNullException(nameof(loader));
        Analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        Materializer = materializer;
        Labels = labels is null
            ? ImmutableArray<string>.Empty
            : labels.Select(static l => l.Trim()).Where(static l => !string.IsNullOrWhiteSpace(l)).ToImmutableArray();
    }

    public SemanticMediaType MediaType { get; }

    public IFormatLoader Loader { get; }

    public IFormatAnalyzer Analyzer { get; }

    public IFormatMaterializer? Materializer { get; }

    public IReadOnlyCollection<string> Labels { get; }
}
