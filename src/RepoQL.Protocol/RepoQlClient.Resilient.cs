using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Provide resilient RepoQL client operations with recovery and diagnostics.
/// Complexity: Adds channel state checks, health watch monitoring, and circuit breaker behavior on top of the connection client.
/// </summary>
public sealed class RepoQlClient : RepoQlConnectionClient
{
    private readonly object _healthWatchSync = new();
    private readonly ConnectionCircuitBreaker _circuitBreaker = new(3, TimeSpan.FromMinutes(5));
    private CancellationTokenSource? _healthWatchCts;
    private GrpcChannel? _watchedChannel;
    private HealthCheckResponse.Types.ServingStatus? _lastHealthStatus;
    private bool _healthWatchFaulted;
    private bool? _lastHealthProbeServing;
    private bool _leaseFaulted;

    private enum HealthProbeResult
    {
        Unknown,
        Serving,
        Unreachable
    }

    private RepoQlClient(GrpcChannel channel, TimeSpan? defaultTimeout, ILogger? logger = null)
        : base(channel, defaultTimeout, logger)
    {
    }

    private RepoQlClient(RepoQlClientOptions options, string repoPath, string? socketPath, ILogger? logger = null)
        : base(options, repoPath, socketPath, logger)
    {
    }

    /// <summary>
    /// Create a client from an existing <see cref="GrpcChannel"/> (useful for in-memory tests with TestServer).
    /// </summary>
    public static RepoQlClient FromChannel(GrpcChannel channel, TimeSpan? defaultTimeout = null, ILogger? logger = null)
        => new(channel, defaultTimeout, logger);

    /// <summary>
    /// Create a client connected to the repository's RepoQL server over a Unix domain socket.
    /// </summary>
    /// <param name="options">Optional configuration for socket discovery and default timeouts.</param>
    /// <param name="logger">Optional logger for connection diagnostics.</param>
    /// <param name="cancellationToken"></param>
    public static async Task<IRepoQlClient> CreateAsync(RepoQlClientOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        options ??= new RepoQlClientOptions();
        if (!RepoLocator.TryFindRepoRoot(options.RepositoryPath, out var repoPath, out var searchedFrom))
        {
            throw new RepoRootNotFoundException(searchedFrom ?? Directory.GetCurrentDirectory());
        }

        logger ??= NullLogger.Instance;
        logger.LogInformation("RepoQlClient: creating managed connection (repoRoot='{RepoRoot}', socketOverride='{SocketOverride}').",
            repoPath,
            options.SocketPath ?? "<null>");

        repoPath = repoPath ?? throw new InvalidOperationException("Repo root could not be resolved.");
        var client = new RepoQlClient(options, repoPath, options.SocketPath, logger);
        await client.EnsureConnectedAsync(forceReconnect: true, cancellationToken).ConfigureAwait(false);
        client.EnsureHealthWatchActive();
        return client;
    }

    protected override async Task<T> InvokeWithReconnectAsync<T>(Func<Contracts.RepoQL.RepoQLClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var maxAttempts = IsManaged ? 2 : 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var recoveryAttempted = attempt > 0;
            try
            {
                await PrepareForCallAsync(recoveryAttempted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsManaged && !IsUserError(ex))
            {
                ThrowDiagnostics(ex, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            var client = Client ?? throw new InvalidOperationException("RepoQL client is not connected.");

            try
            {
                var result = await operation(client, cancellationToken).ConfigureAwait(false);
                _circuitBreaker.RecordSuccess(DateTime.UtcNow);
                _leaseFaulted = false;
                return result;
            }
            catch (Exception ex) when (attempt == 0 && IsManaged && ShouldAttemptReconnect(ex) && !IsUserError(ex))
            {
                Logger.LogWarning(ex, "RepoQlClient: first attempt failed; disposing channel and retrying.");
                DisposeChannel();
                continue;
            }
            catch (Exception ex) when (!IsUserError(ex) && IsManaged)
            {
                ThrowDiagnostics(ex, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }
        }

        throw new InvalidOperationException("RepoQL client operation failed.");
    }

    public override async IAsyncEnumerable<RawQueryRow> ExecuteRawQueryStreamAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxAttempts = IsManaged ? 2 : 1;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            var recoveryAttempted = attempt > 0;
            try
            {
                await PrepareForCallAsync(recoveryAttempted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsManaged && !IsUserError(ex))
            {
                ThrowDiagnostics(ex, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            var client = Client ?? throw new InvalidOperationException("RepoQL client is not connected.");
            var req = BuildRawQueryRequest(sql, parameters, rowLimit);
            var deadline = ComputeDeadline();
            using var call = client.ExecuteRawQueryStream(req, deadline: deadline, cancellationToken: cancellationToken);
            var emitted = false;
            Exception failure = new InvalidOperationException("RepoQL stream failed unexpectedly.");

            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    _circuitBreaker.RecordSuccess(DateTime.UtcNow);
                    _leaseFaulted = false;
                    yield break;
                }

                emitted = true;
                yield return call.ResponseStream.Current;
            }

            if (!emitted && attempt == 0 && IsManaged && ShouldAttemptReconnect(failure) && !IsUserError(failure))
            {
                Logger.LogWarning(failure, "RepoQlClient: stream attempt failed; disposing channel and retrying.");
                DisposeChannel();
                attempt++;
                continue;
            }

            if (!IsUserError(failure) && IsManaged)
            {
                ThrowDiagnostics(failure, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            throw failure;
        }
    }

    public override async IAsyncEnumerable<ReindexProgress> ReindexAllAsync(
        bool clear = false,
        string? scope = null,
        TimeSpan? timeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxAttempts = IsManaged ? 2 : 1;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            var recoveryAttempted = attempt > 0;
            try
            {
                await PrepareForCallAsync(recoveryAttempted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsManaged && !IsUserError(ex))
            {
                ThrowDiagnostics(ex, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            var client = Client ?? throw new InvalidOperationException("RepoQL client is not connected.");
            var deadline = ComputeDeadline(timeout);
            var request = new ReindexRequest { Clear = clear };
            if (!string.IsNullOrWhiteSpace(scope))
                request.Scope = scope;
            using var call = client.ReindexAll(request, deadline: deadline, cancellationToken: cancellationToken);
            var emitted = false;
            Exception failure = new InvalidOperationException("RepoQL stream failed unexpectedly.");

            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    _circuitBreaker.RecordSuccess(DateTime.UtcNow);
                    _leaseFaulted = false;
                    yield break;
                }

                emitted = true;
                yield return call.ResponseStream.Current;
            }

            if (!emitted && attempt == 0 && IsManaged && ShouldAttemptReconnect(failure) && !IsUserError(failure))
            {
                Logger.LogWarning(failure, "RepoQlClient: stream attempt failed; disposing channel and retrying.");
                DisposeChannel();
                attempt++;
                continue;
            }

            if (!IsUserError(failure) && IsManaged)
            {
                ThrowDiagnostics(failure, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            throw failure;
        }
    }

    public override async IAsyncEnumerable<StatusEvent> WatchStatusAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxAttempts = IsManaged ? 2 : 1;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            var recoveryAttempted = attempt > 0;
            try
            {
                await PrepareForCallAsync(recoveryAttempted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsManaged && !IsUserError(ex))
            {
                ThrowDiagnostics(ex, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            var client = Client ?? throw new InvalidOperationException("RepoQL client is not connected.");
            using var call = client.WatchStatus(new WatchStatusRequest(), cancellationToken: cancellationToken);
            var emitted = false;
            Exception failure = new InvalidOperationException("RepoQL status stream failed unexpectedly.");

            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!moved)
                {
                    _circuitBreaker.RecordSuccess(DateTime.UtcNow);
                    _leaseFaulted = false;
                    yield break;
                }

                emitted = true;
                yield return call.ResponseStream.Current;
            }

            if (!emitted && attempt == 0 && IsManaged && ShouldAttemptReconnect(failure) && !IsUserError(failure))
            {
                Logger.LogWarning(failure, "RepoQlClient: status stream failed; disposing channel and retrying.");
                DisposeChannel();
                attempt++;
                continue;
            }

            if (!IsUserError(failure) && IsManaged)
            {
                ThrowDiagnostics(failure, recoveryAttempted, circuitBreakerOpen: _circuitBreaker.IsOpen(DateTime.UtcNow));
            }

            throw failure;
        }
    }

    public override ValueTask DisposeAsync()
    {
        StopHealthWatch();
        return base.DisposeAsync();
    }

    protected override void OnLeaseFaulted(Exception ex)
    {
        _leaseFaulted = true;
        Logger.LogWarning(ex, "RepoQlClient: lease stream faulted; will reconnect on next call.");
    }

    protected override void DisposeChannel()
    {
        StopHealthWatch();
        base.DisposeChannel();
    }

    private async Task PrepareForCallAsync(bool forceReconnect, CancellationToken cancellationToken)
    {
        if (!IsManaged)
        {
            await EnsureConnectedAsync(forceReconnect: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var shouldReconnect = forceReconnect || await ShouldReconnectBeforeCallAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var requiresConnect = shouldReconnect || Client is null;
        if (requiresConnect && _circuitBreaker.IsOpen(now))
        {
            ThrowDiagnostics(new InvalidOperationException("RepoQL host repeatedly failed to start."),
                recoveryAttempted: shouldReconnect,
                circuitBreakerOpen: true);
        }

        await EnsureConnectedAsync(forceReconnect: shouldReconnect, cancellationToken).ConfigureAwait(false);
        EnsureHealthWatchActive();
    }

    private async Task<bool> ShouldReconnectBeforeCallAsync(CancellationToken cancellationToken)
    {
        if (ChannelInternal is null)
            return false;

        if (!IsChannelSuspect())
            return false;

        var probeResult = await ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        switch (probeResult)
        {
            case HealthProbeResult.Serving:
                Logger.LogWarning("RepoQlClient: health probe succeeded; refreshing channel.");
                break;
            case HealthProbeResult.Unreachable:
                Logger.LogWarning("RepoQlClient: health probe failed; reconnecting to host.");
                break;
            default:
                Logger.LogWarning("RepoQlClient: health probe unavailable; reconnecting to host.");
                break;
        }

        return true;
    }

    private bool IsChannelSuspect()
    {
        if (_leaseFaulted)
            return true;

        if (_healthWatchFaulted)
            return true;

        if (_lastHealthStatus is { } status && status != HealthCheckResponse.Types.ServingStatus.Serving)
            return true;

        return false;
    }

    private async Task<HealthProbeResult> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsManaged)
            return HealthProbeResult.Unknown;

        var socketPath = ResolveSocketPathForHealthProbe();
        if (string.IsNullOrWhiteSpace(socketPath))
            return HealthProbeResult.Unknown;

        var ok = await TryHealthCheckAsync(socketPath, cancellationToken).ConfigureAwait(false);
        lock (_healthWatchSync)
        {
            _lastHealthProbeServing = ok;
        }

        return ok ? HealthProbeResult.Serving : HealthProbeResult.Unreachable;
    }

    private string? ResolveSocketPathForHealthProbe()
    {
        if (!IsManaged)
            return null;

        if (!string.IsNullOrWhiteSpace(ActiveSocketPath))
            return ActiveSocketPath;

        if (!string.IsNullOrWhiteSpace(ConfiguredSocketPath))
            return ConfiguredSocketPath;

        if (RepoRoot is null)
            return null;

        using var accessor = new RepoDirectoryAccessor(RepoRoot);
        return accessor.ResolveSocketPath();
    }

    private void EnsureHealthWatchActive()
    {
        if (!IsManaged)
            return;

        var channel = ChannelInternal;
        if (channel is null)
            return;

        lock (_healthWatchSync)
        {
            if (ReferenceEquals(channel, _watchedChannel))
                return;

            StopHealthWatchLocked();
            _watchedChannel = channel;
            _healthWatchCts = new CancellationTokenSource();
            var token = _healthWatchCts.Token;
            _ = Task.Run(() => RunHealthWatchAsync(channel, token), token);
        }
    }

    private async Task RunHealthWatchAsync(GrpcChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var client = new Health.HealthClient(channel);
            using var call = client.Watch(new HealthCheckRequest { Service = string.Empty }, cancellationToken: cancellationToken);
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var status = call.ResponseStream.Current.Status;
                lock (_healthWatchSync)
                {
                    _lastHealthStatus = status;
                    _healthWatchFaulted = false;
                }

                if (status != HealthCheckResponse.Types.ServingStatus.Serving)
                {
                    Logger.LogWarning("RepoQlClient: health watch reported {Status}.", status);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            lock (_healthWatchSync)
            {
                _healthWatchFaulted = true;
            }
            Logger.LogWarning(ex, "RepoQlClient: health watch failed.");
        }
    }

    private void StopHealthWatch()
    {
        lock (_healthWatchSync)
        {
            StopHealthWatchLocked();
        }
    }

    private void StopHealthWatchLocked()
    {
        _healthWatchCts?.Cancel();
        _healthWatchCts?.Dispose();
        _healthWatchCts = null;
        _watchedChannel = null;
        _lastHealthStatus = null;
        _healthWatchFaulted = false;
    }

    private void ThrowDiagnostics(Exception ex, bool recoveryAttempted, bool circuitBreakerOpen)
    {
        _circuitBreaker.RecordFailure(DateTime.UtcNow);
        var diagnostics = BuildDiagnostics(recoveryAttempted, circuitBreakerOpen);
        diagnostics.Throw(ex.Message, ex);
    }

    private RepoQlDiagnostics BuildDiagnostics(bool recoveryAttempted, bool circuitBreakerOpen)
    {
        var repoRoot = RepoRoot;
        var socketPath = ActiveSocketPath ?? ConfiguredSocketPath;
        string? channelState = null;
        var healthStatus = _lastHealthStatus?.ToString();
        if (healthStatus is null && _healthWatchFaulted)
            healthStatus = "WatchFaulted";

        if (healthStatus is null && _lastHealthProbeServing.HasValue)
            healthStatus = _lastHealthProbeServing.Value ? "Serving" : "Unreachable";
        if (healthStatus is null && _healthWatchFaulted)
            healthStatus = "WatchFaulted";

        var host = GetHostDiagnostics();
        return new RepoQlDiagnostics(
            RepoRoot: repoRoot,
            SocketPath: socketPath,
            ChannelState: channelState,
            HealthStatus: healthStatus,
            HealthWatchFaulted: _healthWatchFaulted,
            Host: host,
            RecoveryAttempted: recoveryAttempted,
            CircuitBreakerOpen: circuitBreakerOpen,
            CircuitBreakerFailures: _circuitBreaker.FailureCount,
            CircuitBreakerWindow: _circuitBreaker.Window);
    }


    private static bool IsUserError(Exception ex)
    {
        if (ex is RepoQlDiagnosticsException)
            return false;

        if (ex is RpcException rpc)
        {
            if (rpc.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange)
                return true;

            if (IsSqlError(rpc.Status.Detail) || IsSqlError(rpc.Message))
                return true;
        }

        return ex is ArgumentException;
    }

    private static bool IsSqlError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Parser Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Binder Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Catalog Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Conversion Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid Input Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Constraint Error", StringComparison.OrdinalIgnoreCase);
    }
}
