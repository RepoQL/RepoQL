using System.Runtime.CompilerServices;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.Web.Services;

/// <summary>
/// Provides operations for managing the RepoQL host: reindex, import, shutdown.
/// Tracks operation state and provides events for UI updates.
/// </summary>
internal sealed class OperationsService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<OperationsService> _logger;
    private readonly object _stateLock = new();
    private UIOperationState _currentState = UIOperationState.Idle();
    private CancellationTokenSource? _operationCts;

    public OperationsService(
        RepoQlConnectionManager connectionManager,
        ILogger<OperationsService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>Fired when operation state changes.</summary>
    public event EventHandler<UIOperationState>? StateChanged;

    /// <summary>Current operation state.</summary>
    public UIOperationState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>Whether any operation is currently running.</summary>
    public bool IsOperationRunning => CurrentState.Status != OperationStatus.Idle;

    /// <summary>
    /// Start a full reindex operation and stream progress updates.
    /// </summary>
    /// <param name="clear">Whether to clear existing data before reindexing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of progress updates.</returns>
    public async IAsyncEnumerable<ReindexProgress> StartReindexAsync(
        bool clear = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsOperationRunning)
        {
            _logger.LogWarning("Cannot start reindex: operation already in progress");
            yield break;
        }

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _operationCts.Token;

        UpdateState(UIOperationState.Reindexing("Preparing...", 0, 0));

        IRepoQlClient? client = null;
        Exception? error = null;
        bool completed = false;

        // Get client outside of iteration
        try
        {
            client = await _connectionManager.GetClientAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            UpdateState(UIOperationState.Idle("Reindex cancelled"));
            _logger.LogInformation("Reindex cancelled by user");
            _operationCts?.Dispose();
            _operationCts = null;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reindex failed to connect");
            UpdateState(UIOperationState.Error($"Reindex failed: {ex.Message}"));
            _operationCts?.Dispose();
            _operationCts = null;
            throw;
        }

        // Stream results - errors handled by caller
        var enumerator = client.ReindexAllAsync(clear, cancellationToken: linkedToken).GetAsyncEnumerator(linkedToken);
        try
        {
            while (true)
            {
                ReindexProgress progress;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        completed = true;
                        break;
                    }
                    progress = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    UpdateState(UIOperationState.Idle("Reindex cancelled"));
                    _logger.LogInformation("Reindex cancelled by user");
                    error = null; // Signal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reindex failed");
                    UpdateState(UIOperationState.Error($"Reindex failed: {ex.Message}"));
                    error = ex;
                    break;
                }

                var phaseName = GetPhaseName(progress.Phase);
                var percentage = progress.TotalItems > 0
                    ? (int)(progress.ProcessedItems * 100 / progress.TotalItems)
                    : 0;

                UpdateState(UIOperationState.Reindexing(
                    phaseName,
                    (int)progress.ProcessedItems,
                    (int)progress.TotalItems,
                    percentage,
                    progress.Phase));

                yield return progress;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }

        if (completed)
        {
            UpdateState(UIOperationState.Idle("Reindex completed"));
            _logger.LogInformation("Reindex completed successfully");
        }
        else if (error is not null)
        {
            throw error;
        }
    }

    /// <summary>
    /// Import an external repository and wait for all files to be indexed with structure embeddings.
    /// </summary>
    /// <param name="uri">Repository URI (e.g., github://owner/repo).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<RepoQL.Protocol.ImportResult> ImportRepositoryAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (IsOperationRunning)
        {
            throw new InvalidOperationException("Cannot start import: operation already in progress");
        }

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _operationCts.Token;

        UpdateState(UIOperationState.Importing(uri, "Waiting for indexing..."));

        try
        {
            var client = await _connectionManager.GetClientAsync(linkedToken).ConfigureAwait(false);
            var result = await client.ImportRepositoryAsync(uri, cancellationToken: linkedToken).ConfigureAwait(false);

            UpdateState(UIOperationState.Idle($"Import of {uri} completed"));
            _logger.LogInformation("Import of {Uri} completed", uri);

            return result;
        }
        catch (OperationCanceledException)
        {
            UpdateState(UIOperationState.Idle("Import cancelled"));
            _logger.LogInformation("Import cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import of {Uri} failed", uri);
            UpdateState(UIOperationState.Error($"Import failed: {ex.Message}"));
            throw;
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>
    /// Get current pipeline status without blocking.
    /// </summary>
    public async Task<PipelineStatus> GetPipelineStatusAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetPipelineStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Request graceful shutdown of the host.
    /// </summary>
    public async Task ShutdownHostAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Requesting host shutdown");
        UpdateState(UIOperationState.ShuttingDown());

        try
        {
            var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);
            // Execute raw SQL that triggers shutdown (since IRepoQlClient doesn't expose ShutdownHost directly)
            // We'll need to call the gRPC method directly through the client
            // For now, we'll call the raw query that triggers shutdown behavior
            // Actually, looking at the proto, ShutdownHost is a separate RPC - we need to add it to IRepoQlClient
            // For now, simulate with a message
            _logger.LogWarning("ShutdownHost not yet implemented in IRepoQlClient - would need to add the method");
            UpdateState(UIOperationState.Error("Shutdown not yet implemented"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutdown request failed");
            UpdateState(UIOperationState.Error($"Shutdown failed: {ex.Message}"));
            throw;
        }
    }

    /// <summary>
    /// Cancel the current operation if one is running.
    /// </summary>
    public void CancelCurrentOperation()
    {
        _operationCts?.Cancel();
    }

    private void UpdateState(UIOperationState state)
    {
        lock (_stateLock)
        {
            _currentState = state with { UpdatedAt = DateTimeOffset.UtcNow };
        }
        StateChanged?.Invoke(this, _currentState);
    }

    private static string GetPhaseName(ReindexPhase phase) => phase switch
    {
        ReindexPhase.Preparing => "Preparing",
        ReindexPhase.Enumerating => "Discovering files",
        ReindexPhase.Queueing => "Queueing files",
        ReindexPhase.HotPath => "Parsing & indexing",
        ReindexPhase.Pruning => "Cleaning up",
        ReindexPhase.VectorRefresh => "Computing embeddings",
        ReindexPhase.MultifileAnalysis => "Cross-file analysis",
        ReindexPhase.IndexRebuild => "Rebuilding indexes",
        ReindexPhase.Completed => "Completed",
        _ => "Processing"
    };
}

/// <summary>Operation status.</summary>
internal enum OperationStatus
{
    Idle,
    Reindexing,
    Importing,
    ShuttingDown,
    Error
}

/// <summary>Current UI operation state snapshot.</summary>
internal sealed record UIOperationState(
    OperationStatus Status,
    string Message,
    int ProcessedItems,
    int TotalItems,
    int PercentComplete,
    ReindexPhase? CurrentPhase,
    string? TargetUri,
    DateTimeOffset UpdatedAt)
{
    public static UIOperationState Idle(string? message = null) =>
        new(OperationStatus.Idle, message ?? "Idle", 0, 0, 0, null, null, DateTimeOffset.UtcNow);

    public static UIOperationState Reindexing(string message, int processed, int total, int percent = 0, ReindexPhase? phase = null) =>
        new(OperationStatus.Reindexing, message, processed, total, percent, phase, null, DateTimeOffset.UtcNow);

    public static UIOperationState Importing(string uri, string message) =>
        new(OperationStatus.Importing, message, 0, 0, 0, null, uri, DateTimeOffset.UtcNow);

    public static UIOperationState ShuttingDown() =>
        new(OperationStatus.ShuttingDown, "Shutting down...", 0, 0, 0, null, null, DateTimeOffset.UtcNow);

    public static UIOperationState Error(string message) =>
        new(OperationStatus.Error, message, 0, 0, 0, null, null, DateTimeOffset.UtcNow);
}
