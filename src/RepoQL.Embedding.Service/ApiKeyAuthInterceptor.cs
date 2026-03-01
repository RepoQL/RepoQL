using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace RepoQL.Embedding.Service;

/// <summary>
/// gRPC server interceptor that validates API keys from the Authorization header.
/// Strategy pattern: V1 compares SHA-256 hashes against config. Future: JWT validation.
/// </summary>
internal sealed class ApiKeyAuthInterceptor : Interceptor
{
    private readonly HashSet<string> _validKeyHashes;
    private readonly ILogger<ApiKeyAuthInterceptor> _logger;

    public ApiKeyAuthInterceptor(IOptions<AuthOptions> options, ILogger<ApiKeyAuthInterceptor> logger)
    {
        _logger = logger;
        _validKeyHashes = new HashSet<string>(
            options.Value.ApiKeyHashes,
            StringComparer.OrdinalIgnoreCase);
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return await continuation(request, context);
    }

    private void Validate(ServerCallContext context)
    {
        // Skip auth if no keys are configured (development mode).
        if (_validKeyHashes.Count == 0)
            return;

        var authHeader = context.RequestHeaders.GetValue("authorization");
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Missing authorization header — set embedding.remote.apiKey in repoql.json"));
        }

        // Expect "Bearer <token>"
        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Authorization header must use Bearer scheme"));
        }

        var token = authHeader[bearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Empty bearer token — check embedding.remote.apiKey in repoql.json"));
        }

        var hash = HashToken(token);
        if (!_validKeyHashes.Contains(hash))
        {
            _logger.LogWarning("Rejected API key with hash prefix {HashPrefix}", hash[..8]);
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Invalid API key — check embedding.remote.apiKey in repoql.json"));
        }
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
