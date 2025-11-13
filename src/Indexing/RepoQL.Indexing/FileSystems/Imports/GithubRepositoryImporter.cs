using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Indexing.FileSystems.Imports;

/// <summary>
/// Imports GitHub repositories by cloning them under <c>.repoql/imports/github</c> and exposing a read-only mount
/// whose URIs take the form <c>github://owner/repo/path</c>. Designed for both CLI commands and agent-driven imports.
/// </summary>
public sealed class GithubRepositoryImporter : IVirtualFileSystemImporter
{
    private static readonly string ImportsRoot = Path.Combine(".repoql", "imports", "github");
    private readonly PhysicalFileSystem _primary;
    private readonly ILogger<GithubRepositoryImporter> _logger;

    public GithubRepositoryImporter(PhysicalFileSystem primaryFileSystem, ILogger<GithubRepositoryImporter> logger)
    {
        _primary = primaryFileSystem ?? throw new ArgumentNullException(nameof(primaryFileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool CanHandle(RepoUri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.Equals(source.Scheme, "github", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(source.Authority, "github.com", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<CompositeFileSystemMount> ImportAsync(RepoUri source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var spec = ParseSource(source);
        var targetRoot = Path.Combine(_primary.RootPath, ImportsRoot, spec.Owner, spec.Repository);
        Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);

        await CloneOrUpdateAsync(spec, targetRoot, cancellationToken).ConfigureAwait(false);

        var fs = new PhysicalFileSystem(
            targetRoot,
            scheme: "github",
            uriPrefix: spec.Repository,
            authority: spec.Owner);

        var mountId = $"github:{spec.Owner}/{spec.Repository}";
        return CompositeFileSystemMount.ForScheme(
            mountId,
            fs,
            scheme: "github",
            authority: spec.Owner,
            pathPrefix: spec.Repository,
            includeInEnumeration: true,
            enableWatching: false);
    }

    /// <summary>
    /// Ensures <paramref name="spec"/> is present on disk. Performs a shallow clone when missing and fetch/pull when a
    /// previous clone exists.
    /// </summary>
    private async Task CloneOrUpdateAsync(RepositorySpec spec, string targetRoot, CancellationToken ct)
    {
        if (!Directory.Exists(targetRoot) || !Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            await RunGitAsync([
                "clone",
                "--depth", "1",
                spec.CloneUrl,
                targetRoot
            ], _primary.RootPath, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(spec.Ref))
            {
                await RunGitAsync(["-C", targetRoot, "checkout", spec.Ref!], _primary.RootPath, ct).ConfigureAwait(false);
            }
            return;
        }

        await RunGitAsync(["-C", targetRoot, "fetch", "--all"], _primary.RootPath, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            await RunGitAsync(["-C", targetRoot, "checkout", spec.Ref!], _primary.RootPath, ct).ConfigureAwait(false);
            await RunGitAsync(["-C", targetRoot, "reset", "--hard", spec.Ref!], _primary.RootPath, ct).ConfigureAwait(false);
        }
        else
        {
            await RunGitAsync(["-C", targetRoot, "pull", "--ff-only"], _primary.RootPath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Executes a git command and surfaces stdout/stderr for diagnostics.</summary>
    private async Task RunGitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => tcs.TrySetResult(true);

        _logger.LogInformation("Running git {Args}", string.Join(' ', arguments));
        if (!process.Start())
            throw new InvalidOperationException("Failed to start git process.");

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { /* ignore */ }
            tcs.TrySetCanceled(cancellationToken);
        });

        await tcs.Task.ConfigureAwait(false);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git exited with {process.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stdout))
            _logger.LogDebug("{Stdout}", stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("{Stderr}", stderr.Trim());
    }

    /// <summary>Normalizes a GitHub URI (custom scheme or https) into a repository specification.</summary>
    private static RepositorySpec ParseSource(RepoUri uri)
    {
        if (string.Equals(uri.Scheme, "github", StringComparison.OrdinalIgnoreCase))
        {
            var owner = uri.Authority;
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (string.IsNullOrWhiteSpace(owner))
            {
                if (segments.Length < 2)
                    throw new InvalidOperationException("github:// URIs must include owner and repository.");
                owner = segments[0];
                segments = segments[1..];
            }

            if (segments.Length == 0)
                throw new InvalidOperationException("Repository segment missing from github:// URI.");
            var repo = segments[0];
            var reference = GetQueryParameter(uri, "ref");
            return new RepositorySpec(owner, repo, reference);
        }

        if (string.Equals(uri.Authority, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                throw new InvalidOperationException("GitHub URL must include owner and repository.");
            var owner = segments[0];
            var repo = segments[1];
            var reference = GetQueryParameter(uri, "ref");
            return new RepositorySpec(owner, repo, reference);
        }

        throw new InvalidOperationException($"Unsupported GitHub URI '{uri}'.");
    }

    private readonly record struct RepositorySpec(string Owner, string Repository, string? Ref)
    {
        public string CloneUrl => $"https://github.com/{Owner}/{Repository}.git";
    }

    /// <summary>Simple helper to read a single query parameter from the URI.</summary>
    private static string? GetQueryParameter(RepoUri uri, string key)
    {
        if (string.IsNullOrEmpty(uri.Query))
            return null;

        var trimmed = uri.Query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 0)
                continue;

            if (!string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                continue;

            return parts.Length > 1
                ? Uri.UnescapeDataString(parts[1])
                : string.Empty;
        }

        return null;
    }
}
