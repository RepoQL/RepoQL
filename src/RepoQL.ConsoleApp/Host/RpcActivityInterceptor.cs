using Grpc.Core;
using Grpc.Core.Interceptors;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Track request lifetimes across all gRPC handlers.
/// Complexity: Wraps each gRPC call type with a disposable tracking scope.
/// </summary>
internal sealed class RpcActivityInterceptor(RpcActivityTracker tracker) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = tracker.BeginScope(context.Method);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = tracker.BeginScope(context.Method);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = tracker.BeginScope(context.Method);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        using var scope = tracker.BeginScope(context.Method);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }
}
