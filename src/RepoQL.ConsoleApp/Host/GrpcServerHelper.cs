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
        return path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) 
        && (File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop") || File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop-late"));
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
        try
        {
            var directory = Path.GetDirectoryName(socketPath);
            if (!string.IsNullOrEmpty(directory))
            {
                using var dirProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"755 \"{directory}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                dirProcess?.WaitForExit();
            }
        }
        catch { }
    }
}
