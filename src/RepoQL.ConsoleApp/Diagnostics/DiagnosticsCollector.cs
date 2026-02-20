using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.FileProviders;
using RepoQL.ConsoleApp.Host;
using RepoQL.ConsoleApp.Logging;
using RepoQL.Contracts;
using RepoQL.Protocol;
using Serilog;

namespace RepoQL.ConsoleApp.Diagnostics;

/// <summary>
/// Purpose: Describe the depth of diagnostic collection for the current request.
/// Complexity: Enum-only configuration to avoid branching errors.
/// </summary>
internal enum DiagnosticCollectionMode
{
    Fast,
    Full
}

/// <summary>
/// Purpose: Gather diagnostics facts from sockets, health checks, and host artifacts.
/// Complexity: Coordinates best-effort probes without throwing or mutating host state.
/// </summary>
internal sealed class DiagnosticsCollector
{
    private static readonly string[] HealthServiceNames =
    [
        "repoql.v1.RepoQL",
        "repoql.embeddings",
        "repoql.indexer",
        "repoql.watcher",
        "repoql.mcp",
        "repoql.mounts",
        "repoql.discovery",
        "repoql.parsing",
        "repoql.analysis",
        "repoql.writer",
        "repoql.reindex",
        "repoql.ready"
    ];

    public async Task<DiagnosticReport> CollectAsync(DiagnosticCollectionMode mode, CancellationToken ct = default)
    {
        var probeFailures = new List<string>();
        var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var healthServices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var degradedServices = new List<string>();

        var now = DateTimeOffset.UtcNow;
        var cwd = Directory.GetCurrentDirectory();
        var platform = RuntimeInformation.OSDescription.Trim();
        var runtime = RuntimeInformation.FrameworkDescription.Trim();
        var repoRoot = RepoLocator.TryFindRepoRoot(cwd, out var resolvedRepo, out _)
            ? resolvedRepo
            : null;

        var repoqlEnv = GetRepoqlEnvironmentVariables();

        string? socketPath = null;
        string? mappingPath = null;
        string? mappedPath = null;
        bool? socketExists = null;
        bool? socketConnectable = null;
        int? socketPathLength = null;
        int? socketPlatformLimit = null;
        bool? socketRedirected = null;
        bool? socketBindSucceeded = null;
        string? socketBindError = null;

        string? healthOverall = null;
        string? healthRepoQl = null;
        string? healthReason = null;
        int? rpcActiveRequests = null;
        int? rpcHangingRequests = null;
        long? rpcOldestRequestAgeMs = null;
        string? rpcOldestRequestMethod = null;
        long? rpcHangThresholdMs = null;
        string? channelState = null;

        bool? dbExists = null;
        bool? dbLocked = null;
        int? dbLockHolderPid = null;
        string? dbLockHolderName = null;

        long? nodeCount = null;
        string? indexingDiagnostics = null;

        IReadOnlyList<string> hostLogTail = Array.Empty<string>();
        IReadOnlyList<string> hostStderrTail = Array.Empty<string>();

        int? hostPid = null;
        bool? hostRunning = null;
        int? hostExitCode = null;
        string? hostExe = null;
        string? hostCwd = null;
        DateTimeOffset? hostStartedAt = null;

        if (repoRoot is not null)
        {
            using var repoRootProvider = new PhysicalFileProvider(repoRoot);
            var mappingFile = repoRootProvider.GetRepoqlFileInfo(RepoqlPaths.SocketMapFileName);
            if (mappingFile.Exists)
            {
                mappingPath = mappingFile.PhysicalPath ?? RepoqlPaths.GetSocketMappingPath(repoRoot);
                var rawMapping = repoRootProvider.TryReadRepoqlFileText(RepoqlPaths.SocketMapFileName);
                mappedPath = rawMapping?.Trim();
                socketPath = string.IsNullOrWhiteSpace(mappedPath)
                    ? RepoqlPaths.GetDefaultSocketPath(repoRoot)
                    : mappedPath;
            }
            else
            {
                socketPath = RepoqlPaths.GetDefaultSocketPath(repoRoot);
            }

            socketPath = RepoqlSocketPathResolver.NormalizeSocketPath(socketPath, repoRoot);
            socketPathLength = socketPath.Length;
            socketPlatformLimit ??= OperatingSystem.IsMacOS() ? 104 : 108;
            socketExists = File.Exists(socketPath);

            var socketBindReport = ReadSocketBindReport(repoRoot, artifacts);
            if (socketBindReport is not null)
            {
                socketRedirected = socketBindReport.SocketRedirected;
                socketPlatformLimit = socketBindReport.PlatformLimit;
                socketBindSucceeded = socketBindReport.BindSucceeded;
                socketBindError = socketBindReport.BindError;
                if (string.IsNullOrWhiteSpace(mappingPath))
                    mappingPath = socketBindReport.MappingFilePath;
                if (!string.IsNullOrWhiteSpace(socketBindReport.SocketPath))
                    socketPath = socketBindReport.SocketPath;
                if (!string.IsNullOrWhiteSpace(socketPath))
                {
                    socketPathLength = socketPath.Length;
                    socketExists = File.Exists(socketPath);
                }
            }

            hostLogTail = ReadHostLogTail(repoRoot, probeFailures);

            var dbPath = Path.Combine(RepoqlPaths.GetRepoqlDirectoryPath(repoRoot), "index.duckdb");
            dbExists = File.Exists(dbPath);
            if (dbExists == true)
            {
                var lockHolder = DatabaseLockInspector.TryGetLockHolder(dbPath, Log.Logger);
                if (lockHolder is not null)
                {
                    dbLocked = true;
                    dbLockHolderPid = lockHolder.ProcessId;
                    dbLockHolderName = lockHolder.ProcessName;
                }
                else
                {
                    dbLocked = false;
                }
            }

            ReadExistingHostReport(repoRoot, artifacts);
            ReadDatabaseInitReport(repoRoot, artifacts);
            ReadServicesStartReport(repoRoot, artifacts);
        }

        if (!string.IsNullOrWhiteSpace(socketPath))
        {
            socketConnectable = await TryConnectAsync(socketPath, probeFailures, ct).ConfigureAwait(false);
            if (socketConnectable == true)
            {
                using var channel = CreateGrpcChannel(socketPath);

                var overallHealth = await TryCheckHealthAsync(
                    channel,
                    string.Empty,
                    probeFailures,
                    ct).ConfigureAwait(false);
                healthOverall = overallHealth.Status;
                healthReason = overallHealth.Reason;
                degradedServices = overallHealth.Degraded;
                rpcActiveRequests = overallHealth.RpcActiveRequests;
                rpcHangingRequests = overallHealth.RpcHangingRequests;
                rpcOldestRequestAgeMs = overallHealth.RpcOldestRequestAgeMs;
                rpcOldestRequestMethod = overallHealth.RpcOldestRequestMethod;
                rpcHangThresholdMs = overallHealth.RpcHangThresholdMs;

                var repoQlHealth = await TryCheckHealthAsync(
                    channel,
                    "repoql.v1.RepoQL",
                    probeFailures,
                    ct).ConfigureAwait(false);
                healthRepoQl = repoQlHealth.Status;

                foreach (var service in HealthServiceNames)
                {
                    var serviceHealth = await TryCheckHealthAsync(channel, service, probeFailures, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(serviceHealth.Status))
                        healthServices[service] = serviceHealth.Status;
                }

                if (mode == DiagnosticCollectionMode.Full)
                {
                    var dbDiag = await PopulateDatabaseDiagnosticsAsync(channel, ct, probeFailures).ConfigureAwait(false);
                    nodeCount = dbDiag.NodeCount;
                    indexingDiagnostics = dbDiag.IndexingDiagnostics;
                }
            }
        }

        var hostDiag = RepoQlClient.GetHostDiagnostics();
        hostStderrTail = hostDiag.StderrTail;
        hostExitCode = hostDiag.ExitCode;
        hostExe = hostDiag.ExecutablePath;
        hostCwd = hostDiag.WorkingDirectory;
        hostPid = hostDiag.ProcessId;
        hostRunning = hostDiag.HasExited.HasValue ? !hostDiag.HasExited.Value : null;
        if (hostDiag.LaunchTime.HasValue)
            hostStartedAt = new DateTimeOffset(hostDiag.LaunchTime.Value, TimeSpan.Zero);

        return new DiagnosticReport
        {
            TimestampUtc = now,
            ProcessId = Environment.ProcessId,
            RepoRoot = repoRoot,
            CurrentDirectory = cwd,
            Platform = string.IsNullOrWhiteSpace(platform) ? null : platform,
            Runtime = string.IsNullOrWhiteSpace(runtime) ? null : runtime,
            RepoqlVersion = HostLogging.GetHostVersion(),
            RepoqlEnvironmentVariables = repoqlEnv,
            SocketPath = socketPath,
            SocketExists = socketExists,
            SocketConnectable = socketConnectable,
            SocketMappingPath = mappingPath,
            SocketMappedPath = mappedPath,
            SocketRedirected = socketRedirected,
            SocketPathLength = socketPathLength,
            SocketPlatformLimit = socketPlatformLimit,
            SocketBindSucceeded = socketBindSucceeded,
            SocketBindError = socketBindError,
            HealthOverall = healthOverall,
            HealthRepoQl = healthRepoQl,
            HealthReason = healthReason,
            HealthDegradedServices = degradedServices,
            HealthServices = healthServices,
            RpcActiveRequests = rpcActiveRequests,
            RpcHangingRequests = rpcHangingRequests,
            RpcOldestRequestAgeMs = rpcOldestRequestAgeMs,
            RpcOldestRequestMethod = rpcOldestRequestMethod,
            RpcHangThresholdMs = rpcHangThresholdMs,
            ChannelState = channelState,
            LeaseStreamActive = null,
            LeaseLastHeartbeatUtc = null,
            DbExists = dbExists,
            DbLocked = dbLocked,
            DbLockHolderPid = dbLockHolderPid,
            DbLockHolderName = dbLockHolderName,
            NodeCount = nodeCount,
            IndexingDiagnosticsText = indexingDiagnostics,
            HostLogTail = hostLogTail,
            HostStderrTail = hostStderrTail,
            HostProcessId = hostPid,
            HostRunning = hostRunning,
            HostExitCode = hostExitCode,
            HostExecutablePath = hostExe,
            HostWorkingDirectory = hostCwd,
            HostStartedAtUtc = hostStartedAt,
            Artifacts = artifacts,
            ProbeFailures = probeFailures
        };
    }

    private static IReadOnlyList<string> GetRepoqlEnvironmentVariables()
    {
        var result = new List<string>();
        foreach (var entry in Environment.GetEnvironmentVariables().OfType<System.Collections.DictionaryEntry>())
        {
            if (entry.Key is not string key)
                continue;

            if (!key.StartsWith("REPOQL_", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add($"{key}={entry.Value}");
        }

        return result;
    }

    private static async Task<bool?> TryConnectAsync(string socketPath, List<string> probeFailures, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            probeFailures.Add($"socket_connect: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
    }

    private static async Task<HealthProbeSnapshot> TryCheckHealthAsync(
        GrpcChannel channel,
        string service,
        List<string> probeFailures,
        CancellationToken ct)
    {
        try
        {
            var client = new Health.HealthClient(channel);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var call = client.CheckAsync(new HealthCheckRequest { Service = service }, cancellationToken: cts.Token);
            var response = await call.ResponseAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var reason = GetTrailerValue(trailers, "repoql-reason");
            var degradedRaw = GetTrailerValue(trailers, "repoql-degraded");
            var degraded = string.IsNullOrWhiteSpace(degradedRaw)
                ? new List<string>()
                : degradedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var status = NormalizeHealthStatus(response.Status);
            return new HealthProbeSnapshot(
                status,
                reason,
                degraded,
                TryParseInt(GetTrailerValue(trailers, "repoql-rpc-active")),
                TryParseInt(GetTrailerValue(trailers, "repoql-rpc-hanging")),
                TryParseLong(GetTrailerValue(trailers, "repoql-rpc-oldest-ms")),
                GetTrailerValue(trailers, "repoql-rpc-oldest-method"),
                TryParseLong(GetTrailerValue(trailers, "repoql-rpc-hang-threshold-ms")));
        }
        catch (Exception ex)
        {
            var label = string.IsNullOrWhiteSpace(service) ? "health" : $"health[{service}]";
            probeFailures.Add($"{label}: {ex.GetType().Name} - {ex.Message}");
            return new HealthProbeSnapshot(null, null, new List<string>(), null, null, null, null, null);
        }
    }

    private static async Task<(long? NodeCount, string? IndexingDiagnostics)> PopulateDatabaseDiagnosticsAsync(
        GrpcChannel channel,
        CancellationToken ct,
        List<string> probeFailures)
    {
        try
        {
            var client = new RepoQL.Contracts.RepoQL.RepoQLClient(channel);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var countResult = await client.ExecuteRawQueryAsync(
                new RawQueryRequest { Sql = "SELECT COUNT(*) as cnt FROM node" },
                cancellationToken: cts.Token).ResponseAsync.ConfigureAwait(false);

            long? nodeCount = null;
            if (countResult.Rows.Count > 0 && countResult.Rows[0].Values.Count > 0)
                nodeCount = (long)countResult.Rows[0].Values[0].NumberValue;

            var diagResult = await client.ExecuteRawQueryAsync(
                new RawQueryRequest { Sql = "SELECT indexing_diagnostics() as diag" },
                cancellationToken: cts.Token).ResponseAsync.ConfigureAwait(false);

            string? indexingDiagnostics = null;
            if (diagResult.Rows.Count > 0 && diagResult.Rows[0].Values.Count > 0)
                indexingDiagnostics = diagResult.Rows[0].Values[0].StringValue;

            return (nodeCount, indexingDiagnostics);
        }
        catch (Exception ex)
        {
            probeFailures.Add($"database: {ex.GetType().Name} - {ex.Message}");
            return (null, null);
        }
    }

    private static GrpcChannel CreateGrpcChannel(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        return GrpcChannel.ForAddress("http://unix", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Insecure
        });
    }

    private static string NormalizeHealthStatus(HealthCheckResponse.Types.ServingStatus status)
        => status switch
        {
            HealthCheckResponse.Types.ServingStatus.Serving => "SERVING",
            HealthCheckResponse.Types.ServingStatus.NotServing => "NOT_SERVING",
            _ => "UNKNOWN"
        };

    private static string? GetTrailerValue(Metadata metadata, string key)
    {
        foreach (var entry in metadata)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return null;
    }

    private static int? TryParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? TryParseLong(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static IReadOnlyList<string> ReadHostLogTail(string repoRoot, List<string> probeFailures)
    {
        try
        {
            var logPath = Path.Combine(RepoqlPaths.GetRepoqlDirectoryPath(repoRoot), "host.log");
            if (!File.Exists(logPath))
                return Array.Empty<string>();

            var buffer = new Queue<string>();
            foreach (var line in File.ReadLines(logPath))
            {
                if (buffer.Count >= 50)
                    buffer.Dequeue();
                buffer.Enqueue(line);
            }

            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            probeFailures.Add($"host_log: {ex.GetType().Name} - {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static SocketBindReport? ReadSocketBindReport(string repoRoot, Dictionary<string, string> artifacts)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "socket-bind.json", out SocketBindReport? report) || report is null)
            return null;

        artifacts["socket-bind.json"] = report.BindSucceeded ? "OK" : "FAILED";
        return report;
    }

    private static void ReadExistingHostReport(string repoRoot, Dictionary<string, string> artifacts)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "existing-host.json", out ExistingHostReport? report) || report is null)
            return;

        var summary = report.ShutdownSucceeded
            ? "shutdown_ok"
            : report.ShutdownAttempted
                ? "shutdown_failed"
                : "not_attempted";
        artifacts["existing-host.json"] = summary;
    }

    private static void ReadDatabaseInitReport(string repoRoot, Dictionary<string, string> artifacts)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "database-init.json", out DatabaseInitReport? report) || report is null)
            return;

        var status = report.OpenSucceeded ? "OK" : "FAILED";
        artifacts["database-init.json"] = status;
    }

    private static void ReadServicesStartReport(string repoRoot, Dictionary<string, string> artifacts)
    {
        if (!HostDiagnosticsStore.TryReadReport(repoRoot, "services-start.json", out ServicesStartReport? report) || report is null)
            return;

        var status = report.Issues.Count == 0 ? "OK" : "DEGRADED";
        artifacts["services-start.json"] = status;
    }

    /// <summary>
    /// Purpose: Carry one health check result plus diagnostic trailers.
    /// Complexity: Value-only transport for health probe outputs.
    /// </summary>
    private sealed record HealthProbeSnapshot(
        string? Status,
        string? Reason,
        List<string> Degraded,
        int? RpcActiveRequests,
        int? RpcHangingRequests,
        long? RpcOldestRequestAgeMs,
        string? RpcOldestRequestMethod,
        long? RpcHangThresholdMs);
}
