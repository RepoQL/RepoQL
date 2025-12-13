using System.Net.Sockets;
using Grpc.Core;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Diagnostics;

/// <summary>
/// Classifies exceptions to determine if they're infrastructure errors (connection, host, etc.)
/// vs user-input errors (SQL syntax, invalid parameters, etc.).
/// </summary>
internal static class ErrorClassifier
{
    /// <summary>
    /// Returns true if the exception indicates an infrastructure problem that warrants
    /// running diagnostics (connection lost, host crashed, timeout, etc.).
    /// </summary>
    public static bool IsInfrastructureError(Exception ex)
    {
        // Check the exception and its inner exceptions
        return IsInfrastructureErrorCore(ex) ||
               (ex.InnerException != null && IsInfrastructureError(ex.InnerException));
    }

    private static bool IsInfrastructureErrorCore(Exception ex) => ex switch
    {
        // gRPC connection/server errors
        RpcException rpc when rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal => true,

        // Socket/network errors
        SocketException => true,

        // IO errors (but not SQL-related ones that come through as IOException)
        IOException io when !IsSqlError(io.Message) => true,

        // Timeouts
        TimeoutException => true,

        // Repository not found
        RepoRootNotFoundException => true,

        // Disposed objects (connection was dropped)
        ObjectDisposedException => true,

        // HTTP/2 connection failures
        InvalidOperationException ioe when
            ioe.Message.Contains("HTTP/2", StringComparison.OrdinalIgnoreCase) &&
            ioe.Message.Contains("not established", StringComparison.OrdinalIgnoreCase) => true,

        // Client not connected
        InvalidOperationException ioe when
            ioe.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) => true,

        // Failed to launch host
        InvalidOperationException ioe when
            ioe.Message.Contains("Failed to launch", StringComparison.OrdinalIgnoreCase) => true,

        _ => false
    };

    private static bool IsSqlError(string message)
    {
        // DuckDB error patterns
        return message.Contains("Parser Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Binder Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Catalog Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Conversion Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid Input Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Constraint Error", StringComparison.OrdinalIgnoreCase);
    }
}
