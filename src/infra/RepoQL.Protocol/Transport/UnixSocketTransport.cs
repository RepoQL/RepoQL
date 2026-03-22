using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace RepoQL.Protocol.Transport;

/// <summary>
/// Provides Unix socket transport configuration for gRPC connections.
/// </summary>
/// <remarks>
/// Implements:
/// - REPO-EARS-006: The system SHALL use Unix sockets on all platforms including Windows 10+
/// </remarks>
public sealed class UnixSocketTransport
{
    private readonly string _socketPath;

    /// <summary>
    /// Initializes a new instance of UnixSocketTransport.
    /// </summary>
    /// <param name="socketPath">Optional socket path. Uses platform defaults if not provided.</param>
    public UnixSocketTransport(string? socketPath = null)
    {
        _socketPath = NormalizeSocketPath(socketPath ?? GetDefaultSocketPath());
        ValidateSocketPath();
    }

    /// <summary>
    /// Gets the gRPC address for this transport.
    /// </summary>
    public static string Address => "http://unix";

    /// <summary>
    /// Gets the socket path.
    /// </summary>
    public string SocketPath => _socketPath;

    /// <summary>
    /// Creates an HttpMessageHandler configured for Unix socket communication.
    /// </summary>
    public SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            // Connection pooling settings
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            // Keepalive settings for long-running streams
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true
        };
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Socket ownership is transferred to NetworkStream via ownsSocket: true.")]
    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        }
        catch (SocketException ex)
        {
            throw new PlatformNotSupportedException(
                "Unix domain sockets are not supported on this platform. " +
                "Windows requires version 1803 (build 17134) or later with AF_UNIX support enabled.",
                ex);
        }

        try
        {
            var endpoint = CreateEndPoint();
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            // Set socket options for better performance
            socket.NoDelay = true;

            // Set send/receive buffer sizes for streaming
            socket.SendBufferSize = 64 * 1024;    // 64KB
            socket.ReceiveBufferSize = 64 * 1024; // 64KB

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private UnixDomainSocketEndPoint CreateEndPoint()
    {
        return new UnixDomainSocketEndPoint(_socketPath);
    }

    private static string GetDefaultSocketPath()
    {
        // Socket should be colocated with the database in the repository
        // Default to current directory's .repoql folder
        var currentDir = Path.GetFullPath(Directory.GetCurrentDirectory());
        return RepoqlSocketPathResolver.ResolvePhysical(currentDir);
    }

    private void ValidateSocketPath()
    {
        if (string.IsNullOrWhiteSpace(_socketPath))
        {
            throw new ArgumentException("Socket path cannot be empty", nameof(_socketPath));
        }

        // Check path length limits - applies to all platforms using AF_UNIX
        var maxLength = OperatingSystem.IsMacOS() ? 104 : 108;
        if (_socketPath.Length >= maxLength)
        {
            throw new ArgumentException(
                $"Socket path too long ({_socketPath.Length} chars). " +
                $"Maximum is {maxLength - 1} characters. " +
                $"Path: {_socketPath}",
                nameof(_socketPath));
        }

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create directory for socket: {directory}",
                    ex);
            }
        }
    }

    /// <summary>
    /// Checks if the socket file exists and attempts to clean up stale sockets.
    /// Uses atomic rename to avoid race conditions with other processes.
    /// </summary>
    /// <returns>True if a stale socket was removed, false otherwise.</returns>
    public static bool TryCleanupStaleSocket(string? socketPath = null)
        => TryCleanupStaleSocket(socketPath, out _);

    public static bool TryCleanupStaleSocket(string? socketPath, out Exception? error)
    {
        error = null;
        var path = NormalizeSocketPath(socketPath ?? GetDefaultSocketPath());

        if (!File.Exists(path))
            return false;

        try
        {
            // Try to connect to see if it's active
            using var testSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            testSocket.Connect(new UnixDomainSocketEndPoint(path));
            testSocket.Close();

            // Socket is active, don't delete
            return false;
        }
        catch (SocketException)
        {
            // Socket file exists but nothing is listening - it's stale
            // Use atomic rename-then-delete to avoid race with another process binding
            try
            {
                var tempPath = path + ".stale." + Guid.NewGuid().ToString("N")[..8];
                File.Move(path, tempPath);

                // Verify the socket is still stale after rename (another process could have bound)
                // If the original path exists again, a new server started - leave it alone
                if (File.Exists(path))
                {
                    // New server started, try to restore our renamed file or just delete it
                    try { File.Delete(tempPath); } catch { }
                    return false;
                }

                File.Delete(tempPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }
        catch (PlatformNotSupportedException)
        {
            // AF_UNIX not supported on this platform (older Windows)
            // Just try to delete the stale file directly - it's likely from a previous install
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }
    }

    /// <summary>
    /// Ensures the socket path is clean and ready for binding.
    /// This should be called by the server before attempting to bind.
    /// </summary>
    public void EnsureCleanForBinding()
    {
        // Clean up any stale socket file
        if (TryCleanupStaleSocket(_socketPath))
        {
            // Log or report that a stale socket was cleaned up
            // In production, this would use proper logging
        }

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string NormalizeSocketPath(string path)
        => path.Replace('\\', '/');
}
