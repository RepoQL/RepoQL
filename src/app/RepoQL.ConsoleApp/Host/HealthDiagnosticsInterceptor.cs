using System.Globalization;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Health.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Attach lightweight readiness context to gRPC health checks via trailers.
/// Complexity: Uses interceptors to add optional metadata without altering protocol payloads.
/// </summary>
internal sealed class HealthDiagnosticsInterceptor : Interceptor
{
    private const string HealthCheckMethod = "/grpc.health.v1.Health/Check";
    private readonly HostState _hostState;
    private readonly ServiceDegradationTracker _degradation;
    private readonly RpcActivityTracker _rpcActivity;
    private readonly ILogger<HealthDiagnosticsInterceptor> _logger;

    public HealthDiagnosticsInterceptor(
        HostState hostState,
        ServiceDegradationTracker degradation,
        RpcActivityTracker rpcActivity,
        ILogger<HealthDiagnosticsInterceptor>? logger = null)
    {
        _hostState = hostState ?? throw new ArgumentNullException(nameof(hostState));
        _degradation = degradation ?? throw new ArgumentNullException(nameof(degradation));
        _rpcActivity = rpcActivity ?? throw new ArgumentNullException(nameof(rpcActivity));
        _logger = logger ?? NullLogger<HealthDiagnosticsInterceptor>.Instance;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var response = await continuation(request, context).ConfigureAwait(false);
        if (context.Method == HealthCheckMethod && response is HealthCheckResponse health)
        {
            TryAddTrailers(request, context, health);
        }

        return response;
    }

    private void TryAddTrailers<TRequest>(TRequest request, ServerCallContext context, HealthCheckResponse response)
    {
        try
        {
            if (request is HealthCheckRequest healthRequest)
            {
                var trailers = context.ResponseTrailers;
                var degraded = _degradation.Entries
                    .Select(entry => entry.Kind.ToString().ToLowerInvariant())
                    .ToArray();
                if (degraded.Length > 0)
                    trailers.Add("repoql-degraded", string.Join(',', degraded));

                if (IsBaseService(healthRequest.Service) && response.Status != HealthCheckResponse.Types.ServingStatus.Serving)
                {
                    var reason = _hostState.InitialIndexingCompleted ? "unhealthy" : "initial_indexing";
                    trailers.Add("repoql-reason", reason);
                }

                AddRpcActivityTrailers(trailers);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to append health diagnostics trailers");
        }
    }

    private void AddRpcActivityTrailers(Metadata trailers)
    {
        var snapshot = _rpcActivity.CaptureSnapshot(DateTime.UtcNow);
        trailers.Add("repoql-rpc-active", snapshot.ActiveCount.ToString(CultureInfo.InvariantCulture));
        trailers.Add("repoql-rpc-hanging", snapshot.HangingCount.ToString(CultureInfo.InvariantCulture));
        trailers.Add("repoql-rpc-hang-threshold-ms", snapshot.HangThresholdMs.ToString(CultureInfo.InvariantCulture));

        if (snapshot.OldestRequestAgeMs is { } oldestAgeMs)
            trailers.Add("repoql-rpc-oldest-ms", oldestAgeMs.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(snapshot.OldestRequestMethod))
            trailers.Add("repoql-rpc-oldest-method", snapshot.OldestRequestMethod);
    }

    private static bool IsBaseService(string? service)
        => string.IsNullOrWhiteSpace(service) || string.Equals(service, "repoql.v1.RepoQL", StringComparison.Ordinal);
}
