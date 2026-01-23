using System.Text;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Capture a single diagnostic issue with supporting facts and optional guidance.
/// Complexity: Simple data carrier so formatting can remain deterministic.
/// </summary>
public sealed record DiagnosticProblem(string Title, IReadOnlyList<string> Facts, string? Guidance);

/// <summary>
/// Purpose: Aggregate connection, host, and health facts into a single diagnostics snapshot.
/// Complexity: Encapsulates formatted output without introducing external dependencies.
/// </summary>
public sealed record DiagnosticReport
{
    public DateTimeOffset TimestampUtc { get; init; }
    public int ProcessId { get; init; }
    public string? RepoRoot { get; init; }
    public string? CurrentDirectory { get; init; }
    public string? Platform { get; init; }
    public string? Runtime { get; init; }
    public string? RepoqlVersion { get; init; }
    public IReadOnlyList<string> RepoqlEnvironmentVariables { get; init; } = Array.Empty<string>();

    public string? SocketPath { get; init; }
    public bool? SocketExists { get; init; }
    public bool? SocketConnectable { get; init; }
    public string? SocketMappingPath { get; init; }
    public string? SocketMappedPath { get; init; }
    public bool? SocketRedirected { get; init; }
    public int? SocketPathLength { get; init; }
    public int? SocketPlatformLimit { get; init; }
    public bool? SocketBindSucceeded { get; init; }
    public string? SocketBindError { get; init; }

    public int? HostProcessId { get; init; }
    public bool? HostRunning { get; init; }
    public int? HostExitCode { get; init; }
    public string? HostExecutablePath { get; init; }
    public string? HostWorkingDirectory { get; init; }
    public DateTimeOffset? HostStartedAtUtc { get; init; }
    public IReadOnlyList<string> HostLogTail { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HostStderrTail { get; init; } = Array.Empty<string>();

    public string? HealthOverall { get; init; }
    public string? HealthRepoQl { get; init; }
    public string? HealthReason { get; init; }
    public IReadOnlyList<string> HealthDegradedServices { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> HealthServices { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? ChannelState { get; init; }
    public bool? LeaseStreamActive { get; init; }
    public DateTimeOffset? LeaseLastHeartbeatUtc { get; init; }

    public bool? DbExists { get; init; }
    public bool? DbLocked { get; init; }
    public int? DbLockHolderPid { get; init; }
    public string? DbLockHolderName { get; init; }

    public long? NodeCount { get; init; }
    public string? IndexingDiagnosticsText { get; init; }

    public IReadOnlyDictionary<string, string> Artifacts { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> ProbeFailures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DiagnosticProblem> Problems { get; init; } = Array.Empty<DiagnosticProblem>();

    public override string ToString()
    {
        var builder = new StringBuilder();
        var problems = Problems.Count > 0 ? Problems : DiagnosticReportProblems.Build(this);
        var verdict = DetermineVerdict(problems);

        // Header with verdict
        builder.AppendLine($"RepoQL: {verdict}");
        builder.AppendLine();

        // Problems section (only if any)
        if (problems.Count > 0)
        {
            builder.AppendLine("problems:");
            foreach (var problem in problems)
            {
                builder.AppendLine($"- {problem.Title}");
                foreach (var fact in problem.Facts)
                    builder.AppendLine($"  {fact}");
                if (!string.IsNullOrWhiteSpace(problem.Guidance))
                    builder.AppendLine($"  guidance: {problem.Guidance}");
            }
            builder.AppendLine();
        }

        // Status line: services | nodes | activity
        var statusParts = new List<string>();
        var (servingCount, totalCount, notServingNames) = GetServiceCounts();
        if (totalCount > 0)
            statusParts.Add($"{servingCount}/{totalCount} services");
        else if (SocketConnectable == false)
            statusParts.Add("no connection");
        if (NodeCount.HasValue)
            statusParts.Add($"{NodeCount.Value:N0} nodes");
        var activity = GetActivityStatus();
        if (!string.IsNullOrEmpty(activity))
            statusParts.Add(activity);
        if (statusParts.Count > 0)
            builder.AppendLine($"status: {string.Join(" | ", statusParts)}");

        // Host line: pid | version | uptime
        var hostParts = new List<string>();
        if (HostProcessId.HasValue)
            hostParts.Add($"pid {HostProcessId.Value}");
        if (!string.IsNullOrWhiteSpace(RepoqlVersion))
            hostParts.Add($"v{RepoqlVersion}");
        var uptime = GetUptime();
        if (!string.IsNullOrEmpty(uptime))
            hostParts.Add($"up {uptime}");
        else if (HostRunning == false && HostExitCode.HasValue)
            hostParts.Add($"exited ({HostExitCode.Value})");
        if (hostParts.Count > 0)
            builder.AppendLine($"host: {string.Join(" | ", hostParts)}");

        // Repo line
        if (!string.IsNullOrWhiteSpace(RepoRoot))
            builder.AppendLine($"repo: {RepoRoot}");

        // Pending services (only when starting)
        if (verdict == "STARTING" && notServingNames.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"pending: {string.Join(", ", notServingNames)}");
        }

        // Recent errors (extracted from indexing diagnostics)
        var recentError = ExtractLastError();
        if (!string.IsNullOrEmpty(recentError))
        {
            builder.AppendLine();
            builder.AppendLine("recent errors:");
            builder.AppendLine($"- {recentError}");
        }

        // Host log (only if crashed or has errors)
        if (HostRunning == false && HostExitCode != 0 && HostLogTail.Count > 0)
        {
            var errorLines = HostLogTail
                .Where(l => l.Contains("ERR", StringComparison.OrdinalIgnoreCase))
                .TakeLast(3)
                .ToList();
            if (errorLines.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("host log:");
                foreach (var line in errorLines)
                    builder.AppendLine($"- {line}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private string DetermineVerdict(IReadOnlyList<DiagnosticProblem> problems)
    {
        // DOWN: socket not connectable, or host crashed
        if (SocketConnectable == false)
            return "DOWN";
        if (HostRunning == false && HostExitCode is not null and not 0)
            return "DOWN";

        // STARTING: health not serving yet but socket is connectable
        if (SocketConnectable == true && HealthOverall != "SERVING")
            return "STARTING";

        // DEGRADED: has problems but still running
        if (problems.Count > 0)
            return "DEGRADED";

        // Check for non-serving services
        var (serving, total, _) = GetServiceCounts();
        if (total > 0 && serving < total)
            return "DEGRADED";

        return "OK";
    }

    private (int Serving, int Total, List<string> NotServingNames) GetServiceCounts()
    {
        if (HealthServices.Count == 0)
            return (0, 0, new List<string>());

        var serving = HealthServices.Count(kvp => kvp.Value == "SERVING");
        var notServing = HealthServices
            .Where(kvp => kvp.Value != "SERVING")
            .Select(kvp => kvp.Key.Replace("repoql.", ""))
            .ToList();
        return (serving, HealthServices.Count, notServing);
    }

    private string? GetActivityStatus()
    {
        if (string.IsNullOrWhiteSpace(IndexingDiagnosticsText))
            return null;

        // Parse status from indexing diagnostics
        var match = System.Text.RegularExpressions.Regex.Match(
            IndexingDiagnosticsText,
            @"status:\s*(\w+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private string? GetUptime()
    {
        if (!HostStartedAtUtc.HasValue || HostRunning != true)
            return null;

        var elapsed = TimestampUtc - HostStartedAtUtc.Value;
        if (elapsed.TotalDays >= 1)
            return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalSeconds}s";
    }

    private string? ExtractLastError()
    {
        if (string.IsNullOrWhiteSpace(IndexingDiagnosticsText))
            return null;

        // Match last_error field until end of line
        var match = System.Text.RegularExpressions.Regex.Match(
            IndexingDiagnosticsText,
            @"last_error:\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!match.Success)
            return null;

        var error = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(error))
            return null;

        // Simplify file paths
        error = System.Text.RegularExpressions.Regex.Replace(
            error,
            @"file:///[^:]+/([^/:]+\.\w+)",
            "$1");
        return error;
    }

}

/// <summary>
/// Purpose: Derive diagnostic problem statements from a report snapshot.
/// Complexity: Applies a small set of deterministic rules without side effects.
/// </summary>
internal static class DiagnosticReportProblems
{
    public static IReadOnlyList<DiagnosticProblem> Build(DiagnosticReport report)
    {
        var problems = new List<DiagnosticProblem>();

        if (report.SocketPath is not null && report.SocketExists == false)
        {
            problems.Add(new DiagnosticProblem(
                "Host not running",
                [
                    $"socket={report.SocketPath}",
                    "exists=false"
                ],
                "Start the host or run a RepoQL command to launch it."));
        }

        if (report.SocketExists == true && report.SocketConnectable == false)
        {
            problems.Add(new DiagnosticProblem(
                "Stale socket",
                [
                    $"socket={report.SocketPath}",
                    "connectable=false"
                ],
                "Remove the socket file or restart the host."));
        }

        if (IsNotServing(report.HealthOverall))
        {
            var facts = new List<string> { "overall=NOT_SERVING" };
            if (!string.IsNullOrWhiteSpace(report.HealthReason))
                facts.Add($"reason={report.HealthReason}");
            problems.Add(new DiagnosticProblem(
                "Host not ready",
                facts,
                "Wait for readiness or inspect health services for blockers."));
        }

        if (string.Equals(report.ChannelState, "TransientFailure", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new DiagnosticProblem(
                "Channel stuck",
                ["channel=TransientFailure"],
                "Reconnect the client to refresh the channel."));
        }

        if (report.DbLocked == true && !string.Equals(report.DbLockHolderName, "repoql", StringComparison.OrdinalIgnoreCase))
        {
            var holder = report.DbLockHolderPid.HasValue
                ? $"{report.DbLockHolderName ?? "unknown"} (pid {report.DbLockHolderPid})"
                : report.DbLockHolderName ?? "unknown";
            problems.Add(new DiagnosticProblem(
                "Database locked by external process",
                [$"holder={holder}"],
                "Close the process holding the lock or restart the host."));
        }

        if (report.HostRunning == false && report.HostLogTail.Any(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(new DiagnosticProblem(
                "Previous host crashed",
                ["host_running=false", "host_log=error"],
                "Inspect host log for the crash root cause."));
        }

        return problems;
    }

    private static bool IsNotServing(string? status)
        => string.Equals(status, "NOT_SERVING", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "NotServing", StringComparison.OrdinalIgnoreCase);
}
