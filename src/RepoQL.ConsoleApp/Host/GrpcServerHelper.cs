using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RepoQL.Contracts;
using RepoQL.Protocol.Transport;

namespace RepoQL.ConsoleApp.Host;

public static class GrpcServerHelper
{
    public static void ConfigureUnixSocket(KestrelServerOptions options, string? repositoryPath = null)
    {
        var repoPath = Path.GetFullPath(repositoryPath ?? Directory.GetCurrentDirectory());
        var socketPath = GetActualSocketPath(repoPath);
        var transport = new UnixSocketTransport(socketPath);
        transport.EnsureCleanForBinding();
        options.ListenUnixSocket(socketPath, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });
        SetSocketPermissions(socketPath);
    }

    private static string GetActualSocketPath(string repositoryPath)
    {
        if (IsWslWindowsMount(repositoryPath))
        {
            var repoHash = ComputeStableHash(repositoryPath);
            var socketDir = Path.Combine("/tmp", "repoql", repoHash);
            Directory.CreateDirectory(socketDir);

            var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repositoryPath);
            var mappingFile = Path.Combine(repoqlDir, "socket.path");

            var socketPath = Path.Combine(socketDir, "repoql.sock");
            File.WriteAllText(mappingFile, socketPath + Environment.NewLine);
            return socketPath;
        }

        var localRepoqlDir = RepoLocator.EnsureRepoqlDirectory(repositoryPath);
        return Path.GetFullPath(Path.Combine(localRepoqlDir, "repoql.sock"));
    }

    private static bool IsWslWindowsMount(string path)
    {
        // Check if we're running in WSL
        if (!File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop") && !File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop-late"))
            return false;

        // Standard /mnt/<drive> paths
        if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if the path is on a drvfs (Windows) filesystem by reading /proc/mounts
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
                        return true;
                }
            }
        }
        catch
        {
            // If we can't read /proc/mounts, fall back to path heuristics
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

    private static void SetSocketPermissions(string socketPath)
    {
        if (OperatingSystem.IsWindows()) return;

        // Set directory permissions (755 = rwxr-xr-x)
        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            RunChmod("755", directory, "directory");
        }

        // Set socket file permissions (666 = rw-rw-rw-) to allow all users to connect
        // The socket file may not exist yet at this point (Kestrel creates it),
        // so we defer socket chmod to after binding via a background task
        _ = Task.Run(async () =>
        {
            // Wait briefly for Kestrel to create the socket file
            for (int i = 0; i < 50; i++) // Up to 5 seconds
            {
                await Task.Delay(100);
                if (File.Exists(socketPath))
                {
                    RunChmod("666", socketPath, "socket");
                    return;
                }
            }
            Console.Error.WriteLine($"[GrpcServerHelper] Warning: Socket file '{socketPath}' was not created within timeout, skipping chmod.");
        });
    }

    private static void RunChmod(string mode, string path, string description)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"{mode} \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
            if (process?.ExitCode != 0)
            {
                var stderr = process?.StandardError.ReadToEnd();
                Console.Error.WriteLine($"[GrpcServerHelper] Warning: chmod {mode} failed on {description} '{path}' (exit={process?.ExitCode}): {stderr}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GrpcServerHelper] Warning: Failed to chmod {description} '{path}': {ex.Message}");
        }
    }
}
