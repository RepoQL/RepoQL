using System.Threading;
using RepoQL.Contracts;

namespace RepoQL.Core.Operations;

/// <summary>
/// Tracks a batch of indexing work until completion.
/// </summary>
/// <remarks>
/// <para><strong>Purpose</strong></para>
/// <para>
/// Provide a per-request view of indexing progress for a fixed URI scope, including embedding readiness.
/// </para>
/// <para><strong>Complexity</strong></para>
/// <para>
/// Uses a 500ms polling timer with a re-entrancy guard and per-URI transition tracking to
/// log progress and resolve completion deterministically.
/// </para>
/// </remarks>
public sealed class Operation : IOperation
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const string MissingRegistryMessage = "URI not found in registry";
    private const string UnknownErrorMessage = "Unknown error";

    private readonly UriRegistry _registry;
    private readonly RepoUri[] _scope;
    private readonly IProgress<OperationProgress>? _progress;
    private readonly List<OperationEntry> _log = new();
    private readonly HashSet<RepoUri> _indexedLogged = new();
    private readonly HashSet<RepoUri> _embeddedLogged = new();
    private readonly HashSet<RepoUri> _readyLogged = new();
    private readonly HashSet<RepoUri> _fileFailedLogged = new();
    private readonly HashSet<RepoUri> _embeddingFailedLogged = new();
    private readonly HashSet<RepoUri> _failedCounted = new();
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource<OperationProgress> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Timer? _timer;
    private int _polling;

    public Operation(
        UriRegistry registry,
        string description,
        IEnumerable<RepoUri> scope,
        IProgress<OperationProgress>? progress = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        _progress = progress;

        if (scope is null)
            throw new ArgumentNullException(nameof(scope));

        _scope = scope.Distinct().ToArray();

        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTimeOffset.UtcNow;
        State = OperationState.Running;
        Progress = OperationProgress.Create(_scope.Length, 0, 0, 0);

        _log.Add(new OperationEntry(CreatedAt, OperationEntry.TypeCreated, null, null));

        if (_scope.Length == 0)
        {
            CompleteImmediately();
            return;
        }

        _timer = new Timer(static state => ((Operation)state!).Poll(), this, PollInterval, PollInterval);
    }

    public string Id { get; }

    public string Description { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public OperationState State { get; private set; }

    public OperationProgress Progress { get; private set; }

    public IReadOnlyList<OperationEntry> Log => _log;

    public Task<OperationProgress> Completion => _completion.Task;

    public void Cancel()
    {
        lock (_stateLock)
        {
            if (State != OperationState.Running)
                return;

            StopTimer();
            State = OperationState.Cancelled;
            CompletedAt = DateTimeOffset.UtcNow;
            _log.Add(new OperationEntry(CompletedAt.Value, OperationEntry.TypeCancelled, null, null));
            _completion.TrySetCanceled();
        }
    }

    public void RecordMilestone(string name, string? detail = null)
    {
        lock (_stateLock)
        {
            if (State != OperationState.Running)
                return;

            var message = detail is null ? name : $"{name}: {detail}";
            _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeMilestone, message, null));
        }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            OperationProgress progressSnapshot;
            bool shouldComplete;

            lock (_stateLock)
            {
                if (State != OperationState.Running)
                    return;

                foreach (var uri in _scope)
                {
                    if (!_registry.TryGetValue(uri, out var entry))
                    {
                        if (_fileFailedLogged.Add(uri))
                        {
                            _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeFileFailed, MissingRegistryMessage, uri));
                            _failedCounted.Add(uri);
                        }

                        continue;
                    }

                    if (entry.Status == UriStatus.Indexed)
                    {
                        if (_indexedLogged.Add(uri))
                            _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeFileIndexed, null, uri));
                    }
                    else if (entry.Status == UriStatus.Failed || entry.Status == UriStatus.Skipped)
                    {
                        if (_fileFailedLogged.Add(uri))
                        {
                            _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeFileFailed, entry.Error ?? UnknownErrorMessage, uri));
                            _failedCounted.Add(uri);
                        }

                        continue;
                    }

                    switch (entry.EmbeddingStatus)
                    {
                        case EmbeddingStatus.Embedded:
                            if (_embeddedLogged.Add(uri))
                                _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeFileEmbedded, null, uri));
                            break;
                        case EmbeddingStatus.NotApplicable:
                            if (_readyLogged.Add(uri))
                                _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeFileReady, null, uri));
                            break;
                        case EmbeddingStatus.Failed:
                            if (_embeddingFailedLogged.Add(uri))
                            {
                                _log.Add(new OperationEntry(DateTimeOffset.UtcNow, OperationEntry.TypeEmbeddingFailed, entry.Error ?? UnknownErrorMessage, uri));
                                _failedCounted.Add(uri);
                            }
                            break;
                    }
                }

                progressSnapshot = UpdateProgressLocked();
                shouldComplete = progressSnapshot.EmbeddedCount + progressSnapshot.FailedCount == progressSnapshot.TotalFiles;
            }

            // Report progress before completing so callbacks fire before task resolves
            ReportProgress(progressSnapshot);

            if (shouldComplete)
            {
                lock (_stateLock)
                {
                    CompleteLocked(progressSnapshot);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private OperationProgress UpdateProgressLocked()
    {
        var embeddedCount = _embeddedLogged.Count + _readyLogged.Count;
        var progress = OperationProgress.Create(_scope.Length, _indexedLogged.Count, embeddedCount, _failedCounted.Count);
        Progress = progress;
        return progress;
    }

    private void CompleteImmediately()
    {
        lock (_stateLock)
        {
            Progress = OperationProgress.Empty;
            CompletedAt = DateTimeOffset.UtcNow;
            State = OperationState.Completed;
            _log.Add(new OperationEntry(CompletedAt.Value, OperationEntry.TypeCompleted, null, null));
            _completion.TrySetResult(Progress);
        }

        ReportProgress(Progress);
    }

    private void CompleteLocked(OperationProgress progress)
    {
        if (State != OperationState.Running)
            return;

        StopTimer();
        CompletedAt = DateTimeOffset.UtcNow;
        State = _failedCounted.Count > 0 ? OperationState.CompletedWithFailures : OperationState.Completed;
        _log.Add(new OperationEntry(CompletedAt.Value, OperationEntry.TypeCompleted, null, null));
        _completion.TrySetResult(progress);
    }

    private void StopTimer()
    {
        var timer = _timer;
        if (timer is null)
            return;

        _timer = null;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
    }

    private void ReportProgress(OperationProgress progress)
    {
        if (_progress is null)
            return;

        try
        {
            _progress.Report(progress);
        }
        catch
        {
            // Progress callbacks should never fail operations.
        }
    }
}
