using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Resolve RepoQL socket paths using repo-local mapping files and platform rules.
/// Complexity: Encapsulates WSL mapping, normalization, and override handling in one place.
/// </summary>
public static class RepoqlSocketPathResolver
{
    public static string ResolvePhysical(string repoRoot, string? overridePath = null, bool enableWslMapping = false)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));

        var resolvedRoot = Path.GetFullPath(repoRoot);
        using var repoRootProvider = new PhysicalFileProvider(resolvedRoot);
        var writer = enableWslMapping ? new PhysicalRepoqlFileWriter(resolvedRoot) : null;
        return Resolve(resolvedRoot, repoRootProvider, overridePath, writer);
    }

    public static string Resolve(
        string repoRoot,
        IFileProvider repoRootProvider,
        string? overridePath = null,
        IRepoqlFileWriter? writer = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));
        ArgumentNullException.ThrowIfNull(repoRootProvider);

        var resolvedRoot = Path.GetFullPath(repoRoot);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return NormalizeSocketPath(overridePath, resolvedRoot);
        }

        var mapped = repoRootProvider.TryReadRepoqlSocketMapping();
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            return NormalizeSocketPath(mapped, resolvedRoot);
        }

        if (writer != null && IsWslWindowsMount(resolvedRoot))
        {
            var socketPath = ResolveWslSocketPath(resolvedRoot, writer);
            return NormalizeSocketPath(socketPath, resolvedRoot);
        }

        var defaultSocket = RepoqlPaths.GetDefaultSocketPath(resolvedRoot);
        return NormalizeSocketPath(defaultSocket, resolvedRoot);
    }

    public static string NormalizeSocketPath(string path, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Socket path cannot be null or empty.", nameof(path));

        var trimmed = path.Trim();
        var resolved = Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.GetFullPath(Path.Combine(repoRoot, trimmed));
        return resolved.Replace('\\', '/');
    }

    private static string ResolveWslSocketPath(string repoRoot, IRepoqlFileWriter writer)
    {
        var repoHash = ComputeStableHash(repoRoot);
        var socketDir = Path.Combine("/tmp", "repoql", repoHash);
        Directory.CreateDirectory(socketDir);

        var socketPath = Path.Combine(socketDir, RepoqlPaths.SocketFileName);
        writer.WriteAllText(RepoqlPaths.SocketMapFileName, socketPath + Environment.NewLine);
        return socketPath;
    }

    private static bool IsWslWindowsMount(string path)
    {
        if (!File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop") &&
            !File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop-late"))
        {
            return false;
        }

        if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var mounts = File.ReadAllLines("/proc/mounts");
            foreach (var mount in mounts)
            {
                var parts = mount.Split(' ');
                if (parts.Length >= 3 && parts[2].Equals("drvfs", StringComparison.OrdinalIgnoreCase))
                {
                    var mountPoint = parts[1];
                    if (path.StartsWith(mountPoint, StringComparison.Ordinal) &&
                        (path.Length == mountPoint.Length || path[mountPoint.Length] == '/'))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore /proc/mounts errors and fall back to path heuristics.
        }

        return false;
    }

    private static string ComputeStableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}
