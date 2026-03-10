using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace RepoQL.Inference.Service;

/// <summary>
/// Purpose: Reject unauthenticated gRPC calls before inference work starts.
/// Complexity: Validates bearer tokens for unary and duplex streaming handlers.
/// </summary>
internal sealed class ApiKeyAuthInterceptor : Interceptor
{
    private readonly HashSet<string> _validKeyHashes;
    private readonly ILogger<ApiKeyAuthInterceptor> _logger;

    public ApiKeyAuthInterceptor(IOptions<AuthOptions> options, ILogger<ApiKeyAuthInterceptor> logger)
    {
        _logger = logger;
        _validKeyHashes = new HashSet<string>(options.Value.ApiKeyHashes, StringComparer.OrdinalIgnoreCase);
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    private void Validate(ServerCallContext context)
    {
        if (_validKeyHashes.Count == 0)
            return;

        var authHeader = context.RequestHeaders.GetValue("authorization");
        if (string.IsNullOrWhiteSpace(authHeader))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing authorization header"));

        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authorization header must use Bearer scheme"));

        var token = authHeader[bearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Empty bearer token"));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (_validKeyHashes.Contains(hash))
            return;

        _logger.LogWarning("Rejected API key with hash prefix {HashPrefix}", hash[..8]);
        throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key"));
    }
}
