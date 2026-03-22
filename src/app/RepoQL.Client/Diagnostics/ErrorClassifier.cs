using System.Net.Sockets;
using System.Text.RegularExpressions;
using Grpc.Core;
using RepoQL.Protocol;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Classifies exceptions to determine if they're infrastructure errors (connection, host, etc.)
/// vs user-input errors (SQL syntax, invalid parameters, etc.).
/// </summary>
public static class ErrorClassifier
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
        RepoQlDiagnosticsException => true,

        // gRPC connection/server errors
        RpcException rpc when IsUserRpcError(rpc) => false,
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

    private static bool IsUserRpcError(RpcException rpc)
    {
        if (rpc.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange)
            return true;

        return IsSqlError(rpc.Status.Detail) || IsSqlError(rpc.Message);
    }

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

    /// <summary>
    /// Extracts a clean error message from an exception, stripping gRPC wrapper noise.
    /// Converts: Status(StatusCode="Internal", Detail="Binder Error: ...")
    /// To: Binder Error: ...
    /// </summary>
    public static string GetCleanMessage(Exception ex)
    {
        if (ex is RepoQlDiagnosticsException diag && diag.InnerException is not null)
            return GetCleanMessage(diag.InnerException);

        var message = ex.Message;

        // Handle RpcException - extract the Detail from the Status
        if (ex is RpcException rpc)
        {
            // rpc.Status.Detail contains the clean error without wrapper
            if (!string.IsNullOrWhiteSpace(rpc.Status.Detail))
            {
                message = rpc.Status.Detail;
            }
        }

        // Fallback: if message still has the Status wrapper, try to extract Detail
        if (message.StartsWith("Status(StatusCode=", StringComparison.Ordinal))
        {
            var detailMatch = System.Text.RegularExpressions.Regex.Match(
                message,
                @"Detail=""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100));

            if (detailMatch.Success)
            {
                message = detailMatch.Groups[1].Value;
            }
        }

        return message;
    }

    /// <summary>
    /// Enriches SQL error messages with schema discovery hints.
    /// Parses table/view names from DuckDB's "Candidate bindings" and suggests DESCRIBE.
    /// </summary>
    public static string EnrichSqlError(string message)
    {
        if (message.Contains("Binder Error", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Candidate bindings:", StringComparison.OrdinalIgnoreCase))
        {
            return EnrichBinderError(message);
        }

        if (message.Contains("Catalog Error", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return EnrichCatalogError(message);
        }

        return message;
    }

    private static readonly HashSet<string> CoreTables = new(StringComparer.OrdinalIgnoreCase)
        { "artifact", "node", "edge", "span", "annotation" };

    private static string EnrichBinderError(string message)
    {
        var tableNames = ExtractTableNames(message);
        if (tableNames.Count == 0) return message;

        var describes = string.Join(", ", tableNames.Select(t => $"DESCRIBE {t}"));
        var helpHint = FormatSchemaHelpHint(tableNames);
        return $"{message}\n\nTip: Use {describes} to see all available columns.{helpHint}";
    }

    private static string EnrichCatalogError(string message)
    {
        return $"{message}\n\nTip: Use SHOW TABLES to list available tables and views." +
               "\nDocs: explore(uriGlob=\"help:///schema/**\", keywords=\"your table name\")";
    }

    private static string FormatSchemaHelpHint(List<string> tableNames)
    {
        var hints = new List<string>();
        foreach (var name in tableNames)
        {
            if (CoreTables.Contains(name))
                hints.Add("help:///schema/core.md");
            else
                hints.Add($"help:///schema/views/{name.Replace('_', '-')}.md");
        }

        var dedupedHints = hints.Distinct().ToList();
        return $"\nDocs: read(\"{string.Join("; ", dedupedHints)}\")";
    }

    internal static List<string> ExtractTableNames(string message)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strategy 1: "tablename.column" in candidate bindings (e.g. "files.uri")
        foreach (Match m in Regex.Matches(message, @"""(\w+)\.\w+""", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            tables.Add(m.Groups[1].Value);

        // Strategy 2: Extract from FROM/JOIN clauses in the LINE echo
        // Matches: FROM tablename, JOIN tablename, FROM tablename(...) (table functions)
        foreach (Match m in Regex.Matches(message, @"\b(?:FROM|JOIN)\s+(\w+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            tables.Add(m.Groups[1].Value);

        // Remove SQL keywords and DuckDB error text that false-match
        tables.Remove("SELECT");
        tables.Remove("WHERE");
        tables.Remove("LATERAL");
        tables.Remove("clause"); // "not found in FROM clause!"

        return tables.ToList();
    }
}
