using System.Net.Sockets;
using RepoQL.Contracts;

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
        _socketPath = socketPath ?? GetDefaultSocketPath();
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

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

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
        var currentDir = Directory.GetCurrentDirectory();
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(currentDir);

        return Path.Combine(repoqlDir, "repoql.sock");
    }

    private void ValidateSocketPath()
    {
        if (string.IsNullOrWhiteSpace(_socketPath))
        {
            throw new ArgumentException("Socket path cannot be empty", nameof(_socketPath));
        }

        // Check path length limits
        if (!OperatingSystem.IsWindows())
        {
            // Unix domain socket path limit is typically 108 characters
            const int MaxUnixSocketPathLength = 108;
            if (_socketPath.Length >= MaxUnixSocketPathLength)
            {
                throw new ArgumentException(
                    $"Socket path too long ({_socketPath.Length} chars). " +
                    $"Maximum is {MaxUnixSocketPathLength - 1} characters.",
                    nameof(_socketPath));
            }
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
    /// </summary>
    /// <returns>True if a stale socket was removed, false otherwise.</returns>
    public static bool TryCleanupStaleSocket(string? socketPath = null)
    {
        var path = socketPath ?? GetDefaultSocketPath();

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
            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
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
}