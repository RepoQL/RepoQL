using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Options to configure a <see cref="RepoQlClient"/>.
/// </summary>
public sealed class RepoQlClientOptions
{
    /// <summary>
    /// Optional explicit repository path. When not provided, the client will discover the repo root
    /// using <see cref="RepoLocator.FindRepoRoot"/> starting at the current working directory.
    /// </summary>
    public string? RepositoryPath { get; init; }

    /// <summary>
    /// Optional explicit Unix socket path. When set, this takes precedence over <see cref="RepositoryPath"/>.
    /// If not set, the client mirrors the server's behavior:
    /// use "&lt;repo&gt;/.repoql/socket.path" when present, else "&lt;repo&gt;/.repoql/repoql.sock".
    /// </summary>
    public string? SocketPath { get; init; }

    /// <summary>
    /// Default deadline for unary calls (e.g., raw query, summaries). <c>null</c> means no explicit deadline.
    /// </summary>
    public TimeSpan? DefaultTimeout { get; init; }
}
