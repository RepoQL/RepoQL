using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
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
    private const string GhExecutableName = "gh";
    private static readonly object GhCheckLock = new();
    private static bool _ghChecked;
    private static bool _ghAvailable;

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
        var repoFolderName = string.IsNullOrWhiteSpace(spec.Ref)
            ? spec.Repository
            : $"{spec.Repository}@{SanitizeForPath(spec.Ref)}";
        var targetRoot = Path.Combine(_primary.RootPath, ImportsRoot, spec.Owner, repoFolderName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);

        await CloneOrUpdateAsync(spec, targetRoot, cancellationToken).ConfigureAwait(false);

        var fs = new PhysicalFileSystem(
            targetRoot,
            scheme: "github",
            uriPrefix: spec.Repository,
            authority: spec.Owner);

        var refSuffix = string.IsNullOrWhiteSpace(spec.Ref) ? string.Empty : $"@{spec.Ref}";
        var mountId = $"github:{spec.Owner}/{spec.Repository}{refSuffix}";
        return CompositeFileSystemMount.ForScheme(
            mountId,
            fs,
            scheme: "github",
            authority: spec.Owner,
            pathPrefix: spec.Repository,
            includeInEnumeration: true,
            enableWatching: false,
            enableAnalysis: false);
    }

    /// <summary>
    /// Ensures <paramref name="spec"/> is present on disk. Clones via GitHub CLI when missing and uses repo sync for updates.
    /// </summary>
    private async Task CloneOrUpdateAsync(RepositorySpec spec, string targetRoot, CancellationToken ct)
    {
        EnsureGhAvailable();

        if (!Directory.Exists(targetRoot) || !Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            var args = new List<string>
            {
                "repo",
                "clone",
                $"{spec.Owner}/{spec.Repository}",
                targetRoot,
                "--"
            };

            if (!string.IsNullOrWhiteSpace(spec.Ref))
            {
                args.Add("--branch");
                args.Add(spec.Ref!);
            }

            args.Add("--depth");
            args.Add("1");
            await RunGhAsync(args, _primary.RootPath, ct).ConfigureAwait(false);
            return;
        }

        // Use gh repo sync from within the target directory
        var syncArgs = new List<string>
        {
            "repo",
            "sync",
            "--source",
            $"{spec.Owner}/{spec.Repository}",
            "--force"
        };
        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            syncArgs.Add("--branch");
            syncArgs.Add(spec.Ref!);
        }
        await RunGhAsync(syncArgs, targetRoot, ct).ConfigureAwait(false);
    }

    /// <summary>Executes a GitHub CLI command and surfaces stdout/stderr for diagnostics.</summary>
    private async Task RunGhAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GhExecutableName,
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

        _logger.LogInformation("Running gh {Args}", string.Join(' ', arguments));
        if (!process.Start())
            throw new InvalidOperationException("Failed to start GitHub CLI (gh).");

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
            throw new InvalidOperationException($"GitHub CLI (gh) exited with {process.ExitCode}: {stderr}");
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
            var repoSegment = segments[0];
            var referenceFromPath = ExtractRefFromSegment(ref repoSegment);
            var reference = GetQueryParameter(uri, "ref");
            reference ??= referenceFromPath;
            return new RepositorySpec(owner, repoSegment, reference);
        }

        if (string.Equals(uri.Authority, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                throw new InvalidOperationException("GitHub URL must include owner and repository.");
            var owner = segments[0];
            var repoSegment = segments[1];
            var referenceFromPath = ExtractRefFromSegment(ref repoSegment);
            var reference = GetQueryParameter(uri, "ref");
            reference ??= referenceFromPath;
            return new RepositorySpec(owner, repoSegment, reference);
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

    private static string? ExtractRefFromSegment([NotNull] ref string segment)
    {
        var atIndex = segment.IndexOf('@');
        if (atIndex < 0)
            return null;

        var reference = segment[(atIndex + 1)..];
        segment = segment[..atIndex];
        return reference.Length == 0 ? null : reference;
    }

    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return builder.ToString();
    }

    private static void EnsureGhAvailable()
    {
        if (_ghChecked)
        {
            if (!_ghAvailable)
                throw new InvalidOperationException("GitHub CLI (gh) is required for imports but was not found. Install it from https://cli.github.com/ and ensure it is on PATH.");
            return;
        }

        lock (GhCheckLock)
        {
            if (_ghChecked)
            {
                if (!_ghAvailable)
                    throw new InvalidOperationException("GitHub CLI (gh) is required for imports but was not found. Install it from https://cli.github.com/ and ensure it is on PATH.");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GhExecutableName,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("--version");
                using var process = Process.Start(psi);
                if (process is null)
                    throw new InvalidOperationException("Failed to start GitHub CLI (gh).");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    var stderr = process.StandardError.ReadToEnd();
                    throw new InvalidOperationException($"GitHub CLI (gh) invocation failed: {stderr}");
                }
                _ghAvailable = true;
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
                _ghAvailable = false;
                throw new InvalidOperationException("GitHub CLI (gh) is required for imports but was not found. Install it from https://cli.github.com/ and ensure it is on PATH.", ex);
            }
            finally
            {
                _ghChecked = true;
            }
        }
    }
}
