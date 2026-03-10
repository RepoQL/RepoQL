using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
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
    private readonly DuckDbDataStore _db;
    private readonly ILogger<GithubRepositoryImporter> _logger;

    public GithubRepositoryImporter(
        PhysicalFileSystem primaryFileSystem,
        DuckDbDataStore db,
        ILogger<GithubRepositoryImporter> logger)
    {
        _primary = primaryFileSystem ?? throw new ArgumentNullException(nameof(primaryFileSystem));
        _db = db ?? throw new ArgumentNullException(nameof(db));
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
    public async Task<CompositeFileSystemMount> ImportAsync(RepoUri source, bool analyze = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("[GitHub] Starting import for {Uri}", source.AbsoluteUri);

        var spec = ParseSource(source);
        _logger.LogDebug("[GitHub] Parsed: owner={Owner}, repo={Repo}, ref={Ref}",
            spec.Owner, spec.Repository, spec.Ref ?? "(default)");

        // Always use the same folder for a repo regardless of branch
        // Branch switching is handled via git checkout, not separate clones
        var targetRoot = Path.Combine(_primary.RootPath, ImportsRoot, spec.Owner, spec.Repository);

        _logger.LogDebug("[GitHub] Target path: {Path}", targetRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);

        var cloneStart = sw.ElapsedMilliseconds;
        await CloneOrUpdateAsync(spec, targetRoot, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[GitHub] Clone/sync completed ({ElapsedMs}ms)", sw.ElapsedMilliseconds - cloneStart);

        var fs = new PhysicalFileSystem(
            targetRoot,
            scheme: "github",
            uriPrefix: spec.Repository,
            authority: spec.Owner);

        // Mount ID doesn't include ref since we switch branches on the same clone
        var mountId = $"github:{spec.Owner}/{spec.Repository}";

        _logger.LogDebug("[GitHub] Creating mount {MountId}", mountId);
        var mount = CompositeFileSystemMount.ForScheme(
            mountId,
            fs,
            scheme: "github",
            authority: spec.Owner,
            pathPrefix: spec.Repository,
            includeInEnumeration: true,
            enableWatching: false,
            enableAnalysis: analyze);

        // Persist mount so it survives restarts
        _logger.LogDebug("[GitHub] Persisting mount record...");
        _db.SaveMount(new FileSystemMountRecord
        {
            Id = mount.Id,
            Scheme = "github",
            Authority = spec.Owner,
            PathPrefix = spec.Repository,
            SourceUri = source.AbsoluteUri,
            LocalPath = targetRoot,
            IncludeInEnumeration = true,
            EnableWatching = false,
            EnableAnalysis = analyze
        });

        _logger.LogInformation("[GitHub] Import completed for {Owner}/{Repo} in {ElapsedMs}ms",
            spec.Owner, spec.Repository, sw.ElapsedMilliseconds);

        return mount;
    }

    /// <summary>
    /// Ensures <paramref name="spec"/> is present on disk. Clones via GitHub CLI when missing,
    /// switches branches via git checkout for existing clones.
    /// </summary>
    private async Task CloneOrUpdateAsync(RepositorySpec spec, string targetRoot, CancellationToken ct)
    {
        var canUseGh = true;
        _logger.LogDebug("[GitHub] Checking gh CLI availability...");
        try
        {
            EnsureGhAvailable();
            _logger.LogDebug("[GitHub] gh CLI is available");
        }
        catch (InvalidOperationException ex)
        {
            canUseGh = false;
            _logger.LogWarning(ex, "[GitHub] gh CLI unavailable; falling back to git clone.");
        }

        // Check if target is a valid git repository (has .git folder)
        // If directory exists but isn't a valid repo, it's likely a failed previous clone - delete and retry
        var gitDir = Path.Combine(targetRoot, ".git");
        var isValidRepo = Directory.Exists(targetRoot) && Directory.Exists(gitDir);
        var needsClone = !isValidRepo;

        if (needsClone && Directory.Exists(targetRoot))
        {
            _logger.LogWarning("[GitHub] Target directory exists but is not a valid git repo (no .git folder). " +
                "This may be from a failed previous clone. Deleting and re-cloning: {Path}", targetRoot);
            try
            {
                Directory.Delete(targetRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GitHub] Failed to delete invalid directory: {Path}", targetRoot);
                throw new InvalidOperationException($"Cannot clean up invalid clone directory: {targetRoot}", ex);
            }
        }

        if (needsClone)
        {
            _logger.LogInformation("[GitHub] Cloning {Owner}/{Repo}",
                spec.Owner, spec.Repository);
            if (canUseGh)
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
                    _logger.LogDebug("[GitHub] Using branch/ref: {Ref}", spec.Ref);
                }

                args.Add("--shallow-since=1 year ago");
                _logger.LogDebug("[GitHub] Using shallow clone (since 1 year ago)");

                try
                {
                    await RunGhAsync(args, _primary.RootPath, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GitHub] gh clone failed for {Owner}/{Repo}; retrying with git clone.",
                        spec.Owner, spec.Repository);
                    if (Directory.Exists(targetRoot))
                    {
                        Directory.Delete(targetRoot, recursive: true);
                    }
                }
            }

            await RunGitCloneAsync(spec, targetRoot, ct).ConfigureAwait(false);
            return;
        }

        // Repo already exists - switch branch if specified, otherwise just pull
        _logger.LogInformation("[GitHub] Existing clone found at {Path}", targetRoot);

        RemoveStaleIndexLock(targetRoot);
        try
        {
            await RunGitAsync(["reset", "--hard", "HEAD"], targetRoot, ct).ConfigureAwait(false);
            await RunGitAsync(["clean", "-fd"], targetRoot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHub] Failed to clean working tree at {Path}", targetRoot);
            throw new InvalidOperationException($"Failed to clean working tree at {targetRoot}", ex);
        }

        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            _logger.LogInformation("[GitHub] Switching to branch/ref: {Ref}", spec.Ref);

            // Fetch the requested ref with 1 year of history for blame/log support
            // Note: This works for branches. Tags may need the initial clone to have included them.
            await FetchShallowWithFallbackAsync(["fetch", "origin", spec.Ref!, "--shallow-since=1 year ago"],
                ["fetch", "origin", spec.Ref!, "--depth=1"], targetRoot, ct).ConfigureAwait(false);

            // Checkout the requested branch
            await RunGitAsync(["checkout", spec.Ref!], targetRoot, ct).ConfigureAwait(false);

            // Pull latest changes only when on a branch
            if (IsOnBranch(targetRoot))
            {
                await FetchShallowWithFallbackAsync(["pull", "--shallow-since=1 year ago"],
                    ["pull", "--depth=1"], targetRoot, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("[GitHub] Detached HEAD after checkout; skipping pull.");
            }
        }
        else
        {
            // No specific ref - just pull latest on current branch
            _logger.LogInformation("[GitHub] Pulling latest changes on current branch");
            await FetchShallowWithFallbackAsync(["fetch", "origin", "--shallow-since=1 year ago"],
                ["fetch", "origin", "--depth=1"], targetRoot, ct).ConfigureAwait(false);
            if (IsOnBranch(targetRoot))
            {
                await FetchShallowWithFallbackAsync(["pull", "--shallow-since=1 year ago"],
                    ["pull", "--depth=1"], targetRoot, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("[GitHub] Detached HEAD detected; skipping pull.");
            }
        }
    }

    private void RemoveStaleIndexLock(string targetRoot)
    {
        var lockPath = Path.Combine(targetRoot, ".git", "index.lock");
        if (!File.Exists(lockPath))
            return;

        var lastWriteUtc = File.GetLastWriteTimeUtc(lockPath);
        var age = DateTime.UtcNow - lastWriteUtc;
        if (age <= TimeSpan.FromHours(1))
        {
            _logger.LogWarning("[GitHub] Git index lock file is present and recent (age {AgeMinutes}m). " +
                "Skipping removal: {Path}", Math.Round(age.TotalMinutes), lockPath);
            return;
        }

        _logger.LogWarning("[GitHub] Removing stale git index lock file (age {AgeMinutes}m): {Path}",
            Math.Round(age.TotalMinutes), lockPath);
        try
        {
            File.Delete(lockPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHub] Failed to delete stale git index lock file: {Path}", lockPath);
            throw new InvalidOperationException($"Cannot remove stale git index lock file: {lockPath}", ex);
        }
    }

    private bool IsOnBranch(string targetRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = targetRoot
        };
        startInfo.ArgumentList.Add("symbolic-ref");
        startInfo.ArgumentList.Add("HEAD");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("[GitHub] Failed to start git to determine branch state at {Path}", targetRoot);
                return false;
            }

            process.WaitForExit();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogDebug("[GitHub] git symbolic-ref HEAD failed: {Stderr}", stderr.Trim());
                }
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stdout))
                _logger.LogDebug("[GitHub] git symbolic-ref HEAD returned: {Stdout}", stdout.Trim());

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHub] Failed to determine branch state at {Path}", targetRoot);
            return false;
        }
    }

    /// <summary>
    /// Runs a shallow git command, falling back to a depth-based alternative when the remote has no commits
    /// within the shallow-since window (git fails with "error processing shallow info").
    /// </summary>
    private async Task FetchShallowWithFallbackAsync(
        IReadOnlyList<string> shallowArgs,
        IReadOnlyList<string> depthFallbackArgs,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            await RunGitAsync(shallowArgs, workingDirectory, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("error processing shallow info", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[GitHub] Shallow fetch failed (repo may have no recent commits). Retrying with --depth=1.");
            await RunGitAsync(depthFallbackArgs, workingDirectory, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Clones a repository using git directly (fallback when gh is unavailable/fails).</summary>
    private async Task RunGitCloneAsync(RepositorySpec spec, string targetRoot, CancellationToken cancellationToken)
    {
        var args = new List<string> { "clone" };
        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            args.Add("--branch");
            args.Add(spec.Ref!);
            _logger.LogDebug("[GitHub] Using branch/ref for git clone: {Ref}", spec.Ref);
        }

        args.Add("--shallow-since=1 year ago");
        args.Add(spec.CloneUrl);
        args.Add(targetRoot);
        _logger.LogDebug("[GitHub] Running fallback git clone for {CloneUrl}", spec.CloneUrl);

        try
        {
            await RunGitAsync(args, _primary.RootPath, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("error processing shallow info", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[GitHub] Shallow clone failed (repo may have no recent commits). Retrying with --depth=1 for {Owner}/{Repo}.",
                spec.Owner, spec.Repository);
            if (Directory.Exists(targetRoot))
                Directory.Delete(targetRoot, recursive: true);
        }

        // Retry with --depth=1 which always works regardless of commit age
        var depthArgs = new List<string> { "clone", "--depth=1" };
        if (!string.IsNullOrWhiteSpace(spec.Ref))
        {
            depthArgs.Add("--branch");
            depthArgs.Add(spec.Ref!);
        }
        depthArgs.Add(spec.CloneUrl);
        depthArgs.Add(targetRoot);
        _logger.LogDebug("[GitHub] Running depth-1 fallback clone for {CloneUrl}", spec.CloneUrl);
        await RunGitAsync(depthArgs, _primary.RootPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes a git command.</summary>
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

        _logger.LogDebug("Running git {Args}", string.Join(' ', arguments));
        if (!process.Start())
            throw new InvalidOperationException("Failed to start git.");

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
