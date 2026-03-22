using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem;

/// <summary>
/// A change event for a resource.
/// </summary>
/// <param name="Kind">Change kind.</param>
/// <param name="File">The file which changed</param>
/// <param name="CurrentUri">Canonical resource URI affected.</param>
/// <param name="PreviousUri">For moves/renames the previous canonical URI.</param>
public sealed record ResourceChange(ResourceEvent Kind, IFileInfo File, RepoUri CurrentUri, RepoUri? PreviousUri = null);