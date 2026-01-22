using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Emit a single warning to clients when services are degraded.
/// Complexity: Intercepts gRPC calls and logs once per host lifecycle.
/// </summary>
internal sealed class DegradationWarningInterceptor : Interceptor
{
    private readonly HostState _hostState;
    private readonly ILogger<DegradationWarningInterceptor> _logger;

    public DegradationWarningInterceptor(
        HostState hostState,
        ILogger<DegradationWarningInterceptor>? logger = null)
    {
        _hostState = hostState ?? throw new ArgumentNullException(nameof(hostState));
        _logger = logger ?? NullLogger<DegradationWarningInterceptor>.Instance;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        WarnIfDegraded();
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        WarnIfDegraded();
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        WarnIfDegraded();
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        WarnIfDegraded();
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    private void WarnIfDegraded()
    {
        if (_hostState.Degradation.TryGetWarningMessage(out var message))
            _logger.LogWarning(message);
    }
}
