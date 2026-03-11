namespace RepoQL.Cloud.Service.Auth;

/// <summary>
/// Purpose: Bind the accepted client API key hashes for gRPC request authentication.
/// Complexity: Stores SHA-256 hex digests compared against incoming bearer tokens.
/// </summary>
internal sealed class AuthOptions
{
    /// <summary>
    /// SHA-256 hashes of valid API keys, hex-encoded.
    /// Compare: SHA256(incoming bearer token) against these values.
    /// </summary>
    public string[] ApiKeyHashes { get; set; } = [];
}
