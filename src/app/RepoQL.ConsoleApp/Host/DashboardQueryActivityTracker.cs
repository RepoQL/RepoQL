using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Track recent dashboard-visible tool activity with enough detail to answer what clients are doing now.
/// Complexity: Maintains a bounded recent history plus active in-flight requests for snapshot serialization.
/// </summary>
public sealed class DashboardQueryActivityTracker
{
    private const int MaxEntries = 24;
    private readonly ConcurrentDictionary<long, ActiveQuery> _active = new();
    private readonly object _recentLock = new();
    private readonly List<DashboardQueryActivityEntry> _recent = [];
    private long _nextId;

    public Scope Begin(string tool, string parameters, int tokenBudget)
    {
        var query = new ActiveQuery(
            Id: Interlocked.Increment(ref _nextId),
            Tool: NormalizeTool(tool),
            Parameters: Summarize(parameters, 96),
            TokenBudget: Math.Max(0, tokenBudget),
            StartedAtUtc: DateTime.UtcNow);

        _active[query.Id] = query;
        return new Scope(this, query.Id, query.Tool, query.Parameters, query.TokenBudget, query.StartedAtUtc);
    }

    public IReadOnlyList<DashboardQueryActivityEntry> CaptureSnapshot(DateTime nowUtc)
    {
        var entries = _active.Values
            .OrderByDescending(query => query.StartedAtUtc)
            .Select(query => new DashboardQueryActivityEntry(
                Id: query.Id,
                Tool: query.Tool,
                Parameters: query.Parameters,
                TokenBudget: query.TokenBudget,
                TokensUsed: 0,
                ElapsedMs: Math.Max(0, (long)(nowUtc - query.StartedAtUtc).TotalMilliseconds),
                ResultSummary: "Running",
                TimestampUtc: query.StartedAtUtc,
                State: QueryActivityState.Running))
            .ToList();

        lock (_recentLock)
        {
            entries.AddRange(_recent);
        }

        return entries.Take(MaxEntries).ToArray();
    }

    private void Complete(ActiveQuery query, QueryActivityState state, string resultSummary, int tokensUsed, DateTime completedAtUtc)
    {
        _active.TryRemove(query.Id, out _);

        var entry = new DashboardQueryActivityEntry(
            Id: query.Id,
            Tool: query.Tool,
            Parameters: query.Parameters,
            TokenBudget: query.TokenBudget,
            TokensUsed: Math.Max(0, tokensUsed),
            ElapsedMs: Math.Max(0, (long)(completedAtUtc - query.StartedAtUtc).TotalMilliseconds),
            ResultSummary: Summarize(resultSummary, 96),
            TimestampUtc: completedAtUtc,
            State: state);

        lock (_recentLock)
        {
            _recent.Insert(0, entry);
            if (_recent.Count > MaxEntries)
            {
                _recent.RemoveRange(MaxEntries, _recent.Count - MaxEntries);
            }
        }
    }

    private static string NormalizeTool(string tool)
        => string.IsNullOrWhiteSpace(tool) ? "query" : tool.Trim().ToLowerInvariant();

    private static string Summarize(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(none)";

        var normalized = Regex.Replace(text, "\\s+", " ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..Math.Max(1, maxLength - 3)] + "...";
    }

    private readonly record struct ActiveQuery(
        long Id,
        string Tool,
        string Parameters,
        int TokenBudget,
        DateTime StartedAtUtc);

    public readonly record struct DashboardQueryActivityEntry(
        long Id,
        string Tool,
        string Parameters,
        int TokenBudget,
        int TokensUsed,
        long ElapsedMs,
        string ResultSummary,
        DateTime TimestampUtc,
        QueryActivityState State);

    public enum QueryActivityState
    {
        Running,
        Completed,
        Failed
    }

    public sealed class Scope : IDisposable
    {
        private readonly DashboardQueryActivityTracker owner;
        private readonly long id;
        private readonly string tool;
        private readonly string parameters;
        private readonly int tokenBudget;
        private readonly DateTime startedAtUtc;
        private int _completed;

        internal Scope(
            DashboardQueryActivityTracker owner,
            long id,
            string tool,
            string parameters,
            int tokenBudget,
            DateTime startedAtUtc)
        {
            this.owner = owner;
            this.id = id;
            this.tool = tool;
            this.parameters = parameters;
            this.tokenBudget = tokenBudget;
            this.startedAtUtc = startedAtUtc;
        }

        public T Complete<T>(T response, string resultSummary, int tokensUsed)
        {
            Mark(QueryActivityState.Completed, resultSummary, tokensUsed);
            return response;
        }

        public T Fail<T>(T response, string resultSummary, int tokensUsed = 0)
        {
            Mark(QueryActivityState.Failed, resultSummary, tokensUsed);
            return response;
        }

        public void Cancel(string resultSummary = "Cancelled")
            => Mark(QueryActivityState.Failed, resultSummary, 0);

        private void Mark(QueryActivityState state, string resultSummary, int tokensUsed)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            owner.Complete(
                new ActiveQuery(id, tool, parameters, tokenBudget, startedAtUtc),
                state,
                resultSummary,
                tokensUsed,
                DateTime.UtcNow);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            owner.Complete(
                new ActiveQuery(id, tool, parameters, tokenBudget, startedAtUtc),
                QueryActivityState.Failed,
                "Cancelled",
                0,
                DateTime.UtcNow);
        }
    }
}


