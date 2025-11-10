using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Indexing.FileSystems;

/// <summary>
/// Describes a mount inside <see cref="CompositeFileSystem"/> and how URIs map to the underlying
/// <see cref="IVirtualFileSystem"/>.
/// </summary>
public sealed record CompositeFileSystemMount
{
    /// <summary>Unique identifier for this mount (e.g. "primary", "github:octocat/hello-world").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The virtual file system providing file access for this mount.</summary>
    public IVirtualFileSystem FileSystem { get; init; } = default!;

    /// <summary>
    /// Predicate that determines whether a <see cref="RepoUri"/> belongs to this mount. When omitted, the
    /// <see cref="FileSystem"/> scheme is used as the matcher.
    /// </summary>
    public Func<RepoUri, bool>? UriPredicate { get; init; }

    /// <summary>
    /// Whether files from this mount participate in enumeration. Disabling enumeration keeps the mount resolvable for
    /// known URIs without indexing all contents up front.
    /// </summary>
    public bool IncludeInEnumeration { get; init; } = true;

    /// <summary>Marks this mount as the primary/default mount (typically the user's working tree).</summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// Creates a mount that treats the provided file system as the default handler for its scheme.
    /// </summary>
    public static CompositeFileSystemMount CreatePrimary(IVirtualFileSystem fileSystem, string? id = null) =>
        new()
        {
            Id = id ?? "primary",
            FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem)),
            IsPrimary = true,
            UriPredicate = uri => string.Equals(uri.Scheme, fileSystem.Scheme, StringComparison.OrdinalIgnoreCase)
        };

    /// <summary>
    /// Creates a mount that matches URIs by scheme plus optional authority and path prefix.
    /// </summary>
    /// <param name="id">Unique mount identifier.</param>
    /// <param name="fileSystem">The backing virtual file system.</param>
    /// <param name="scheme">Scheme to match (e.g. "github").</param>
    /// <param name="authority">
    /// Optional authority/host filter (e.g. repository name). When omitted the mount matches all authorities.
    /// </param>
    /// <param name="pathPrefix">
    /// Optional leading path segment (without scheme/authority) that must be present (e.g. "owner/repo").
    /// </param>
    /// <param name="includeInEnumeration">Whether to enumerate files from this mount.</param>
    public static CompositeFileSystemMount ForScheme(
        string id,
        IVirtualFileSystem fileSystem,
        string scheme,
        string? authority = null,
        string? pathPrefix = null,
        bool includeInEnumeration = true)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Mount id is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(scheme))
            throw new ArgumentException("Scheme is required.", nameof(scheme));

        var normalizedScheme = scheme.ToLowerInvariant();
        var normalizedAuthority = authority?.ToLowerInvariant();
        var normalizedPrefix = pathPrefix is null
            ? null
            : pathPrefix.Trim('/').ToLowerInvariant();

        return new CompositeFileSystemMount
        {
            Id = id,
            FileSystem = fileSystem,
            IncludeInEnumeration = includeInEnumeration,
            UriPredicate = uri =>
            {
                if (!string.Equals(uri.Scheme, normalizedScheme, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (normalizedAuthority is not null &&
                    !string.Equals(uri.Authority, normalizedAuthority, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (normalizedPrefix is not null)
                {
                    var path = uri.AbsolutePath.TrimStart('/');
                    if (!path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            }
        };
    }
}
