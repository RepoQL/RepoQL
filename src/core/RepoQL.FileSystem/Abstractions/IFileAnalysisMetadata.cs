using Microsoft.Extensions.FileProviders;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// Optional metadata attached to <see cref="IFileInfo" /> instances to influence pipeline behavior.
/// </summary>
public interface IFileAnalysisMetadata
{
    /// <summary>
    /// When true, the file originates from a read-only mount and analysis should be skipped.
    /// </summary>
    bool IsReadOnly { get; }
}
