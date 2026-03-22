using Microsoft.Extensions.FileProviders;

namespace RepoQL.Contracts;

public class DiscoveredArtifact
{
    public required IFileInfo File { get; init; }
    public required RepoUri RepoUri { get; init; }
    public byte[]? Hash { get; set; }
    public SemanticMediaType? MediaType { get; set; }
}