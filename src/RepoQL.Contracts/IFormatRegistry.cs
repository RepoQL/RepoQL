namespace RepoQL.Contracts;

public interface IFormatRegistry
{
    IEnumerable<FormatDescriptor> Formats { get; }

    bool TryResolveByMedia(SemanticMediaType mediaType, out FormatDescriptor descriptor);

    bool TryResolveByLabel(string label, out FormatDescriptor descriptor);
}
