using Grpc.Core;
using Grpc.Core.Interceptors;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Reject unauthenticated gRPC calls before service handlers execute.
/// Complexity: Runs the shared auth validator across unary, server-streaming, and duplex-streaming entry points.
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private readonly AuthValidationService _validationService;

    public AuthInterceptor(AuthValidationService validationService)
    {
        _validationService = validationService;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await _validationService.ValidateAsync(context, context.CancellationToken).ConfigureAwait(false);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await _validationService.ValidateAsync(context, context.CancellationToken).ConfigureAwait(false);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await _validationService.ValidateAsync(context, context.CancellationToken).ConfigureAwait(false);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await _validationService.ValidateAsync(context, context.CancellationToken).ConfigureAwait(false);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }
}
