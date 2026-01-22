using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Grpc.Net.Client;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Diagnostics;

/// <summary>
/// Runs comprehensive diagnostic checks for the RepoQL MCP server.
/// Used to debug connection and startup issues.
/// </summary>
internal sealed class SelfTestRunner
{

    /// <summary>
    /// Run all diagnostic checks and return a plain-text report.
    /// All checks run regardless of earlier failures.
    /// </summary>
    public async Task<string> RunAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RepoQL Diagnostics ===");
        sb.AppendLine();

        // Environment info
        AppendEnvironment(sb);

        // Repository detection
        var (repoFound, repoRoot, repoqlDir) = CheckRepository(sb);

        // Socket path
        string? socketPath = null;
        if (repoFound && repoRoot != null && repoqlDir != null)
        {
            socketPath = CheckSocketPath(sb, repoRoot);
        }
        else
        {
            sb.AppendLine("Socket:");
            sb.AppendLine("  SKIPPED (no repository found)");
            sb.AppendLine();
        }

        // Host launch info
        AppendHostInfo(sb);

        if (repoFound && repoRoot != null)
        {
            AppendExistingHostReport(sb, repoRoot);
            AppendSocketBindReport(sb, repoRoot);
            AppendDatabaseInitReport(sb, repoRoot);
            AppendServicesStartReport(sb, repoRoot);
        }

        // Connection check
        var connected = false;
        if (socketPath != null)
        {
            connected = await CheckConnectionAsync(sb, socketPath, ct);
        }
        else
        {
            sb.AppendLine("Connection:");
            sb.AppendLine("  SKIPPED (no socket path)");
            sb.AppendLine();
        }

        // Health check
        var healthy = false;
        if (connected && socketPath != null)
        {
            healthy = await CheckHealthAsync(sb, socketPath, ct);
        }
        else
        {
            sb.AppendLine("Health:");
            sb.AppendLine("  SKIPPED (no connection)");
            sb.AppendLine();
        }

        // Database check
        if (healthy && socketPath != null)
        {
            await CheckDatabaseAsync(sb, socketPath, ct);
        }
        else
        {
            sb.AppendLine("Database:");
            sb.AppendLine("  SKIPPED (not healthy)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendEnvironment(StringBuilder sb)
    {
        sb.AppendLine("Environment:");
        sb.AppendLine($"  MCP Server CWD: {Directory.GetCurrentDirectory()}");
        sb.AppendLine($"  OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"  Platform: {RuntimeInformation.RuntimeIdentifier}");
        sb.AppendLine($"  Process ID: {Environment.ProcessId}");

        // REPOQL_* environment variables
        var repoqlVars = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k && k.StartsWith("REPOQL_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (repoqlVars.Count > 0)
        {
            sb.AppendLine("  REPOQL_* vars:");
            foreach (var kv in repoqlVars)
            {
                sb.AppendLine($"    {kv.Key}={kv.Value}");
            }
        }
        else
        {
            sb.AppendLine("  REPOQL_* vars: (none set)");
        }
        sb.AppendLine();
    }

    private static (bool found, string? repoRoot, string? repoqlDir) CheckRepository(StringBuilder sb)
    {
        sb.AppendLine("Repository:");

        var cwd = Directory.GetCurrentDirectory();
        var found = RepoLocator.TryFindRepoRoot(cwd, out var repoRoot, out var searchedFrom);

        sb.AppendLine($"  Searched from: {searchedFrom}");

        if (!found)
        {
            sb.AppendLine("  Status: NOT FOUND");
            sb.AppendLine("  No .git or .repoql marker found in directory tree");
            sb.AppendLine();
            return (false, null, null);
        }

        sb.AppendLine($"  Resolved root: {repoRoot}");

        var repoqlDir = RepoqlPaths.GetRepoqlDirectoryPath(repoRoot!);
        var repoqlExists = Directory.Exists(repoqlDir);
        sb.AppendLine($"  .repoql dir: {(repoqlExists ? "exists" : "MISSING")}");

        if (repoqlExists)
        {
            // Check for database file
            var dbPath = Path.Combine(repoqlDir, "index.duckdb");
            var dbExists = File.Exists(dbPath);
            sb.AppendLine($"  Database file: {(dbExists ? "exists" : "not found")}");
        }

        sb.AppendLine();
        return (true, repoRoot, repoqlExists ? repoqlDir : null);
    }

    private static string? CheckSocketPath(StringBuilder sb, string repoRoot)
    {
        sb.AppendLine("Socket:");

        using var repoRootProvider = new PhysicalFileProvider(repoRoot);
        var mappingFile = repoRootProvider.GetRepoqlFileInfo(RepoqlPaths.SocketMapFileName);
        string socketPath;

        if (mappingFile.Exists)
        {
            var rawMapping = repoRootProvider.TryReadRepoqlFileText(RepoqlPaths.SocketMapFileName);
            var mappingPath = mappingFile.PhysicalPath ?? RepoqlPaths.GetSocketMappingPath(repoRoot);
            sb.AppendLine($"  Mapping file: {mappingPath}");
            if (rawMapping is null)
            {
                sb.AppendLine("  Mapped to: <unreadable>");
                socketPath = RepoqlPaths.GetDefaultSocketPath(repoRoot);
            }
            else
            {
                var mapped = rawMapping.Trim();
                sb.AppendLine(string.IsNullOrWhiteSpace(mapped)
                    ? "  Mapped to: <empty>"
                    : $"  Mapped to: {mapped}");
                socketPath = string.IsNullOrWhiteSpace(mapped)
                    ? RepoqlPaths.GetDefaultSocketPath(repoRoot)
                    : mapped;
            }
        }
        else
        {
            socketPath = RepoqlPaths.GetDefaultSocketPath(repoRoot);
            sb.AppendLine($"  Path: {socketPath}");
        }

        socketPath = RepoqlSocketPathResolver.NormalizeSocketPath(socketPath, repoRoot);

        // Check if socket file exists
        var socketExists = File.Exists(socketPath);
        sb.AppendLine($"  File exists: {(socketExists ? "yes" : "no")}");

        // Check path length (Unix domain sockets have 108 char limit)
        if (socketPath.Length >= 108)
        {
            sb.AppendLine($"  WARNING: Path length ({socketPath.Length}) exceeds Unix socket limit (108)");
        }

        sb.AppendLine();
        return socketPath;
    }

    private static void AppendHostInfo(StringBuilder sb)
    {
        sb.AppendLine("Host:");

        var diag = RepoQlClient.GetHostDiagnostics();

        if (diag.ExecutablePath != null)
        {
            sb.AppendLine($"  Executable: {diag.ExecutablePath}");
        }
        else
        {
            sb.AppendLine("  Executable: (not launched yet)");
        }

        if (diag.WorkingDirectory != null)
        {
            sb.AppendLine($"  Working dir: {diag.WorkingDirectory}");
        }

        if (diag.LaunchTime.HasValue)
        {
            sb.AppendLine($"  Launched: {diag.LaunchTime.Value:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (diag.ProcessId.HasValue)
        {
            sb.AppendLine($"  PID: {diag.ProcessId.Value}");
        }

        if (diag.HasExited.HasValue)
        {
            if (diag.HasExited.Value)
            {
                sb.AppendLine($"  Status: exited (code: {diag.ExitCode})");
            }
            else
            {
                sb.AppendLine("  Status: running");
            }
        }

        // Recent stderr output
        if (diag.StderrTail.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Recent output ({diag.StderrTail.Count} lines):");
            // Show last 20 lines max in diagnostics
            var linesToShow = diag.StderrTail.TakeLast(20);
            foreach (var line in linesToShow)
            {
                sb.AppendLine($"    {line}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendExistingHostReport(StringBuilder sb, string repoRoot)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "existing-host.json", out ExistingHostReport? report) || report is null)
            return;

        sb.AppendLine(report.ToString());
        sb.AppendLine();
    }

    private static void AppendSocketBindReport(StringBuilder sb, string repoRoot)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "socket-bind.json", out SocketBindReport? report) || report is null)
            return;

        sb.AppendLine(report.ToString());
        sb.AppendLine();
    }

    private static void AppendDatabaseInitReport(StringBuilder sb, string repoRoot)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "database-init.json", out DatabaseInitReport? report) || report is null)
            return;

        sb.AppendLine(report.ToString());
        sb.AppendLine();
    }

    private static void AppendServicesStartReport(StringBuilder sb, string repoRoot)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "services-start.json", out ServicesStartReport? report) || report is null)
            return;

        sb.AppendLine(report.ToString());
        sb.AppendLine();
    }

    private static async Task<bool> CheckConnectionAsync(StringBuilder sb, string socketPath, CancellationToken ct)
    {
        sb.AppendLine("Connection:");

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token);
            sb.AppendLine("  Status: OK (connected)");
            sb.AppendLine();
            return true;
        }
        catch (OperationCanceledException)
        {
            sb.AppendLine("  Status: TIMEOUT (5s)");
            sb.AppendLine();
            return false;
        }
        catch (SocketException ex)
        {
            sb.AppendLine($"  Status: FAILED");
            sb.AppendLine($"  Error: {ex.SocketErrorCode} - {ex.Message}");
            sb.AppendLine();
            return false;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Status: FAILED");
            sb.AppendLine($"  Error: {ex.GetType().Name} - {ex.Message}");
            sb.AppendLine();
            return false;
        }
    }

    private static async Task<bool> CheckHealthAsync(StringBuilder sb, string socketPath, CancellationToken ct)
    {
        sb.AppendLine("Health:");

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (_, _) => new ValueTask<Stream>(new NetworkStream(socket, ownsSocket: false))
            };

            using var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
            {
                HttpHandler = handler,
                Credentials = Grpc.Core.ChannelCredentials.Insecure
            });

            var healthClient = new Grpc.Health.V1.Health.HealthClient(channel);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await healthClient.CheckAsync(
                new Grpc.Health.V1.HealthCheckRequest { Service = "repoql.v1.RepoQL" },
                cancellationToken: cts.Token);

            var status = response.Status.ToString();
            var isServing = response.Status == Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving;

            sb.AppendLine($"  Status: {(isServing ? "OK" : "UNHEALTHY")} ({status})");
            sb.AppendLine();
            return isServing;
        }
        catch (OperationCanceledException)
        {
            sb.AppendLine("  Status: TIMEOUT");
            sb.AppendLine();
            return false;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Status: FAILED");
            sb.AppendLine($"  Error: {ex.GetType().Name} - {ex.Message}");
            sb.AppendLine();
            return false;
        }
    }

    private static async Task CheckDatabaseAsync(StringBuilder sb, string socketPath, CancellationToken ct)
    {
        sb.AppendLine("Database:");

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (_, _) => new ValueTask<Stream>(new NetworkStream(socket, ownsSocket: false))
            };

            using var channel = GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
            {
                HttpHandler = handler,
                Credentials = Grpc.Core.ChannelCredentials.Insecure
            });

            var client = new RepoQL.Contracts.RepoQL.RepoQLClient(channel);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            // Simple query test
            var simpleResult = await client.ExecuteRawQueryAsync(
                new RawQueryRequest { Sql = "SELECT 1 as test" },
                cancellationToken: cts.Token);

            sb.AppendLine("  Simple query: OK");

            // Node count
            var countResult = await client.ExecuteRawQueryAsync(
                new RawQueryRequest { Sql = "SELECT COUNT(*) as cnt FROM node" },
                cancellationToken: cts.Token);

            if (countResult.Rows.Count > 0 && countResult.Rows[0].Values.Count > 0)
            {
                var count = countResult.Rows[0].Values[0].NumberValue;
                sb.AppendLine($"  Node count: {count:N0}");
            }

            // Indexer status
            var diagResult = await client.ExecuteRawQueryAsync(
                new RawQueryRequest { Sql = "SELECT indexing_diagnostics() as diag" },
                cancellationToken: cts.Token);

            if (diagResult.Rows.Count > 0 && diagResult.Rows[0].Values.Count > 0)
            {
                var diagText = diagResult.Rows[0].Values[0].StringValue;
                if (!string.IsNullOrWhiteSpace(diagText))
                {
                    sb.AppendLine("  Indexer status:");
                    foreach (var line in diagText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        sb.AppendLine($"    {line.Trim()}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Status: FAILED");
            sb.AppendLine($"  Error: {ex.GetType().Name} - {ex.Message}");
        }

        sb.AppendLine();
    }
}
