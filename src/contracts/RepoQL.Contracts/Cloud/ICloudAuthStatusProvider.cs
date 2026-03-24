namespace RepoQL.Contracts.Cloud;

/// <summary>
/// Purpose: Answer RepoQL cloud authentication and paid-customer status from locally available credential state.
/// Complexity: Provides one application-wide capability check so callers do not infer cloud access from raw tokens.
/// </summary>
public interface ICloudAuthStatusProvider
{
    ValueTask<CloudAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public enum CloudAccessMethod
{
    None = 0,
    Session = 1,
    ApiKey = 2
}

public readonly record struct CloudAuthStatus(
    bool IsAuthenticated,
    bool IsPayingCustomer,
    CloudAccessMethod AccessMethod,
    string? UserId = null,
    string? Email = null,
    string? OrganizationId = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool CanUsePaidCloudFeatures => IsAuthenticated && IsPayingCustomer;
}
