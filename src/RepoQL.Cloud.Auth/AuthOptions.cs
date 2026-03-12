namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Bind server-side auth configuration shared by the cloud gRPC services.
/// Complexity: Stores legacy API key hashes plus the WorkOS JWKS, issuer, and audience settings for session tokens.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// SHA-256 hashes of valid API keys, hex-encoded.
    /// Compare: SHA256(incoming bearer token) against these values.
    /// </summary>
    public string[] ApiKeyHashes { get; set; } = [];

    /// <summary>
    /// JWKS endpoint used to validate WorkOS session tokens locally.
    /// </summary>
    public string JwksUri { get; set; } = "";

    /// <summary>
    /// Expected audience for access tokens. For WorkOS, this is the client ID.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Expected issuer for access tokens.
    /// </summary>
    public string Issuer { get; set; } = "";
}
