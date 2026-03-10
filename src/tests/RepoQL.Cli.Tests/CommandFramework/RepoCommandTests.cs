using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Commands;
using RepoQL.ConsoleApp.CommandImplementations;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;

namespace RepoQL.Cli.Tests.CommandFramework;

/// <summary>
/// Purpose: Verify ::repo command path validation, repo marker walk-up, and repeat-to-confirm.
/// Complexity: Uses real temp directories with .git/.repoql markers. Connection tests use a short
/// cancellation timeout. Confirmation tests share static state so must not run in parallel.
/// </summary>
[NotInParallel(nameof(RepoCommandTests))]
internal sealed class RepoCommandTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"repoql-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Create a temp directory guaranteed to have no .git or .repoql markers in its ancestor chain.
    /// Returns null if the environment doesn't allow it (e.g., all writable paths have markers).
    /// </summary>
    private string? TryCreateTempDirWithoutMarkers()
    {
        // Try paths outside user profile (which typically has .repoql)
        var candidates = new List<string>();

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonData))
            candidates.Add(Path.Combine(commonData, "repoql-cli-tests"));

        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(publicDir))
            candidates.Add(Path.Combine(publicDir, "repoql-cli-tests"));

        var driveRoot = Path.GetPathRoot(Path.GetTempPath());
        if (!string.IsNullOrWhiteSpace(driveRoot))
            candidates.Add(Path.Combine(driveRoot, "repoql-cli-tests"));

        foreach (var baseDir in candidates)
        {
            try { Directory.CreateDirectory(baseDir); }
            catch { continue; }

            var dir = Path.Combine(baseDir, $"repoql-test-{Guid.NewGuid():N}");
            try { Directory.CreateDirectory(dir); }
            catch { continue; }

            if (!RepoLocator.TryFindRepoRoot(dir, out _, out _, allowFallback: false))
            {
                _tempDirs.Add(dir);
                return dir;
            }

            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }

        return null;
    }

    private string CreateTempRepo()
    {
        var dir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    public void Dispose()
    {
        RepoCommand.ResetConfirmation();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // --- Path validation tests (no connection needed) ---

    [Test]
    public async Task EmptyPath_ReturnsError()
    {
        var result = await ExecuteRepo("");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Path is required");
    }

    [Test]
    public async Task WhitespacePath_ReturnsError()
    {
        var result = await ExecuteRepo("   ");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Path is required");
    }

    [Test]
    public async Task NonexistentPath_ReturnsError()
    {
        var result = await ExecuteRepo(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")));
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Directory not found");
    }

    // --- Marker walk-up tests (connection attempt with short timeout) ---

    [Test]
    public async Task ValidRepoPath_AttemptsConnection()
    {
        var repo = CreateTempRepo();
        var result = await ExecuteRepo(repo);
        // Path validation passes; connection fails (no host) — that's expected
        if (result.IsError)
        {
            result.Text.Should().Contain("Failed to connect");
        }
        else
        {
            result.Text.Should().Contain("Switched to repository");
        }
    }

    [Test]
    public async Task SubdirectoryOfRepo_ResolvesToRoot()
    {
        var repo = CreateTempRepo();
        var subDir = Path.Combine(repo, "src", "nested");
        Directory.CreateDirectory(subDir);

        var result = await ExecuteRepo(subDir);
        // Should resolve to repo root, not the subdirectory
        var repoFullPath = Path.GetFullPath(repo);
        if (result.IsError)
        {
            result.Text.Should().Contain(repoFullPath);
        }
        else
        {
            result.Text.Should().Contain(repoFullPath);
        }
    }

    [Test]
    public async Task SubdirectoryOfRepoWithRepoqlMarker_ResolvesToRoot()
    {
        var repo = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(repo, ".repoql"));
        var subDir = Path.Combine(repo, "src", "nested");
        Directory.CreateDirectory(subDir);

        var result = await ExecuteRepo(subDir);
        var repoFullPath = Path.GetFullPath(repo);
        if (result.IsError)
        {
            result.Text.Should().Contain(repoFullPath);
        }
        else
        {
            result.Text.Should().Contain(repoFullPath);
        }
    }

    // --- Confirmation tests (need marker-free dirs, skipped if not possible) ---

    [Test]
    public async Task DirectoryWithoutMarkers_FirstCall_ReturnsConfirmation()
    {
        var bareDir = TryCreateTempDirWithoutMarkers();
        if (bareDir is null) { Skip.Test("Cannot create marker-free directory on this system"); return; }

        var result = await ExecuteRepo(bareDir);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("No repository markers");
        result.Text.Should().Contain("again to confirm");
    }

    [Test]
    public async Task DirectoryWithoutMarkers_SecondCall_Proceeds()
    {
        var bareDir = TryCreateTempDirWithoutMarkers();
        if (bareDir is null) { Skip.Test("Cannot create marker-free directory on this system"); return; }

        var first = await ExecuteRepo(bareDir);
        first.IsError.Should().BeFalse();
        first.Text.Should().Contain("again to confirm");

        // Second call proceeds (connection will fail, but that's expected)
        var second = await ExecuteRepo(bareDir);
        if (second.IsError)
        {
            second.Text.Should().Contain("Failed to connect");
        }
        else
        {
            second.Text.Should().Contain("Switched to repository");
        }
    }

    [Test]
    public async Task DirectoryWithoutMarkers_DifferentPath_ResetsConfirmation()
    {
        var dir1 = TryCreateTempDirWithoutMarkers();
        var dir2 = TryCreateTempDirWithoutMarkers();
        if (dir1 is null || dir2 is null) { Skip.Test("Cannot create marker-free directories on this system"); return; }

        var first = await ExecuteRepo(dir1);
        first.Text.Should().Contain("again to confirm");

        var second = await ExecuteRepo(dir2);
        second.Text.Should().Contain("again to confirm");

        // dir1 needs confirmation again (dir2 replaced it)
        var third = await ExecuteRepo(dir1);
        third.Text.Should().Contain("again to confirm");
    }

    [Test]
    public async Task MarkedRepoPath_ClearsPendingConfirmation()
    {
        var bareDir = TryCreateTempDirWithoutMarkers();
        if (bareDir is null) { Skip.Test("Cannot create marker-free directory on this system"); return; }
        var markedRepo = CreateTempRepo();

        var first = await ExecuteRepo(bareDir);
        first.IsError.Should().BeFalse();
        first.Text.Should().Contain("again to confirm");

        // Switching to a marked repo clears the pending confirmation
        await ExecuteRepo(markedRepo);

        // bareDir needs confirmation again
        var third = await ExecuteRepo(bareDir);
        third.IsError.Should().BeFalse();
        third.Text.Should().Contain("again to confirm");
    }

    // --- Help test ---

    [Test]
    public async Task Help_ShowsUsage()
    {
        var registry = CreateRegistryWithRepoCommand();
        var parsed = new ParsedCommand("repo", [], IsHelp: true);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("repo");
        result.Text.Should().Contain("Switch to a different repository");
    }

    /// <summary>
    /// Execute ::repo[path] with a short timeout so connection tests fail fast.
    /// Host launch is suppressed via REPOQL_SUPPRESS_HOST_LAUNCH (set in GlobalSetup).
    /// StartTimeoutMs is set to 500ms so EnsureServerRunning fails quickly via TimeoutException
    /// rather than waiting for the CTS.
    /// </summary>
    private static async Task<CommandResult> ExecuteRepo(string path)
    {
        var config = new RepoQL.Contracts.Configuration.RepoQlConfig();
        config.Host.StartTimeoutMs = 500;
        var provider = new RepoQlClientProvider(config);
        await using var _ = provider;
        var command = new RepoCommand(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await command.Execute(path, cts.Token);
    }

    private static CommandRegistry CreateRegistryWithRepoCommand()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RepoQL.Contracts.Configuration.RepoQlConfig());
        services.AddSingleton<RepoQlClientProvider>();
        var provider = services.BuildServiceProvider();
        var registry = new CommandRegistry(provider);
        registry.DiscoverCommands();
        return registry;
    }
}
