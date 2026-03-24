using RepoQL.Contracts.Cloud;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Cloud;

/// <summary>
/// Purpose: Resolve whether RepoQL currently has authenticated paid cloud access using only local config and session state.
/// Complexity: Merges API-key config, persisted OAuth session metadata, refresh-token presence, and decoded JWT claims.
/// </summary>
public sealed class CloudAuthStatusProvider : ICloudAuthStatusProvider
{
    private readonly ResolvedConfig _config;
    private readonly CloudAuthSessionStore _sessionStore;

    public CloudAuthStatusProvider(ResolvedConfig config, CloudAuthSessionStore sessionStore)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    }

    public async ValueTask<CloudAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _config.Settings.Cloud.ApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            return new CloudAuthStatus(
                IsAuthenticated: true,
                IsPayingCustomer: true,
                AccessMethod: CloudAccessMethod.ApiKey);

        var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var hasRefreshToken = !string.IsNullOrWhiteSpace(_config.Settings.Cloud.RefreshToken)
                              || await _sessionStore.HasRefreshTokenAsync(cancellationToken).ConfigureAwait(false);

        var accessToken = session?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            accessToken = _config.Settings.Cloud.AuthToken;

        if (string.IsNullOrWhiteSpace(accessToken) && !hasRefreshToken)
            return default;

        JwtPayloadReader.TryReadClaims(accessToken, out var accessClaims);
        JwtPayloadReader.TryReadClaims(session?.IdToken, out var idClaims);

        var organizationId = idClaims?.OrganizationId ?? accessClaims?.OrganizationId;
        var userId = idClaims?.Subject ?? accessClaims?.Subject;
        var email = idClaims?.Email ?? accessClaims?.Email;
        var expiresAt = accessClaims?.ExpiresAt ?? session?.ExpiresAt;

        return new CloudAuthStatus(
            IsAuthenticated: !string.IsNullOrWhiteSpace(accessToken) || hasRefreshToken,
            IsPayingCustomer: !string.IsNullOrWhiteSpace(organizationId),
            AccessMethod: CloudAccessMethod.Session,
            UserId: userId,
            Email: email,
            OrganizationId: organizationId,
            ExpiresAt: expiresAt);
    }
}
