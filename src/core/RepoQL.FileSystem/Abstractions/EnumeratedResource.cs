using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// A file discovered during enumeration together with its canonical RepoURI.
/// </summary>
/// <param name="File">The file handle.</param>
/// <param name="Uri">The canonical RepoURI for the file.</param>
public sealed record EnumeratedResource(IFileInfo File, RepoUri Uri);