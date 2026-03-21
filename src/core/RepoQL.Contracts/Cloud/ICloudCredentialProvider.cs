namespace RepoQL.Contracts.Cloud;

/// <summary>
/// Purpose: Provide bearer tokens for RepoQL cloud clients.
/// Complexity: Implementations may return a static token or refresh OAuth credentials on demand.
/// </summary>
public interface ICloudCredentialProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default);
}
