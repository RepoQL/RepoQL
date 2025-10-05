using Microsoft.AspNetCore.Server.Kestrel.Core;
using RepoQL.Protocol.Transport;

namespace RepoQL.Host.Services;

/// <summary>
/// Helper class for configuring gRPC server with Unix socket transport.
/// </summary>
public static class GrpcServerHelper
{
    /// <summary>
    /// Configures Kestrel to listen on a Unix socket with proper cleanup.
    /// </summary>
    /// <param name="options">Kestrel server options to configure.</param>
    /// <param name="repositoryPath">Optional repository path. Defaults to current directory.</param>
    public static void ConfigureUnixSocket(KestrelServerOptions options, string? repositoryPath = null)
    {
        // Build socket path
        var repoPath = Path.GetFullPath(repositoryPath ?? Directory.GetCurrentDirectory());
        var socketPath = GetActualSocketPath(repoPath);

        // Create transport helper and ensure clean socket
        var transport = new UnixSocketTransport(socketPath);
        transport.EnsureCleanForBinding();

        // Configure Kestrel to listen on Unix socket
        options.ListenUnixSocket(socketPath, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2; // gRPC requires HTTP/2
        });

        // Set socket permissions to be accessible
        SetSocketPermissions(socketPath);

        // Log the socket path (in production, use proper logging)
        Console.WriteLine($"gRPC server listening on Unix socket: {socketPath}");
    }

    /// <summary>
    /// Gets the actual socket path, handling WSL Windows filesystem mounts.
    /// </summary>
    private static string GetActualSocketPath(string repositoryPath)
    {
        // Check if we're on WSL with a Windows mount
        if (IsWslWindowsMount(repositoryPath))
        {
            // Use a Linux-native path for the socket
            var repoHash = Math.Abs(repositoryPath.GetHashCode()).ToString();
            var socketDir = Path.Combine("/tmp", "repoql", repoHash);
            Directory.CreateDirectory(socketDir);

            // Store a mapping file so clients can find the socket
            var repoqlDir = Path.Combine(repositoryPath, ".repoql");
            Directory.CreateDirectory(repoqlDir);
            var mappingFile = Path.Combine(repoqlDir, "socket.path");
            var socketPath = Path.Combine(socketDir, "repoql.sock");
            File.WriteAllText(mappingFile, socketPath);

            return socketPath;
        }
        else
        {
            // Use the repository-local socket path
            var repoqlDir = Path.Combine(repositoryPath, ".repoql");
            Directory.CreateDirectory(repoqlDir);
            return Path.GetFullPath(Path.Combine(repoqlDir, "repoql.sock"));
        }
    }

    /// <summary>
    /// Checks if a path is on a WSL Windows filesystem mount.
    /// </summary>
    private static bool IsWslWindowsMount(string path)
    {
        // WSL Windows mounts typically start with /mnt/
        return path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
               File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop");
    }

    /// <summary>
    /// Sets appropriate permissions on the socket file for access.
    /// </summary>
    private static void SetSocketPermissions(string socketPath)
    {
        if (OperatingSystem.IsWindows())
            return; // Windows doesn't use Unix permissions

        try
        {
            // Set directory permissions to 755 (rwxr-xr-x)
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

            // Note: We can't set permissions on the socket file itself here
            // because Kestrel hasn't created it yet. The socket will be created
            // with the process's umask permissions, which should be sufficient.
        }
        catch
        {
            // Ignore permission errors - may not have permission to change
        }
    }

    /// <summary>
    /// Gets the socket path for a repository.
    /// </summary>
    /// <param name="repositoryPath">Repository path. Defaults to current directory.</param>
    /// <returns>The Unix socket path.</returns>
    public static string GetSocketPath(string? repositoryPath = null)
    {
        var repoPath = Path.GetFullPath(repositoryPath ?? Directory.GetCurrentDirectory());

        // Check if there's a socket mapping file (for WSL Windows mounts)
        var mappingFile = Path.Combine(repoPath, ".repoql", "socket.path");
        if (File.Exists(mappingFile))
        {
            return File.ReadAllText(mappingFile).Trim();
        }

        // Otherwise return the default path
        return GetActualSocketPath(repoPath);
    }

    /// <summary>
    /// Checks if a RepoQL server is already running for the repository.
    /// </summary>
    /// <param name="repositoryPath">Repository path to check.</param>
    /// <returns>True if a server is running, false otherwise.</returns>
    public static bool IsServerRunning(string? repositoryPath = null)
    {
        var socketPath = GetSocketPath(repositoryPath);

        if (!File.Exists(socketPath))
            return false;

        // Try to connect to see if it's active
        try
        {
            using var testSocket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);

            testSocket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
            testSocket.Close();

            // Successfully connected - server is running
            return true;
        }
        catch
        {
            // Cannot connect - no server running
            return false;
        }
    }
}