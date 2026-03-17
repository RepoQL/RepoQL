using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Cloud;

/// <summary>
/// Purpose: Resolve valid bearer tokens for RepoQL cloud clients using local access-token cache,
/// refresh-token exchange, and cross-process coordination.
/// Complexity: Coordinates in-memory cache, access-token disk persistence, refresh-token secure storage,
/// OAuth refresh, and best-effort cross-process locking.
/// </summary>
public sealed partial class CloudCredentialProvider : ICloudCredentialProvider, IDisposable
{
    internal const string NotAuthenticatedMessage = "Not authenticated. Run: repoql login";
    internal const string SessionExpiredMessage = "Session expired. Run: repoql login";
    internal const string NetworkErrorMessage = "Cannot reach authentication service. Check your connection.";

    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);
    private readonly ResolvedConfig _config;
    private readonly CloudAuthSessionStore _sessionStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _authLockPath;
    private readonly TimeSpan _lockTimeout;
    private readonly Uri _tokenEndpoint;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _backgroundRefreshSync = new();
    private readonly bool _ownsHttpClient;

    private Task? _backgroundRefreshTask;
    private AccessTokenState? _cachedToken;

    public CloudCredentialProvider(
        ResolvedConfig config,
        CloudAuthSessionStore sessionStore,
        ILogger<CloudCredentialProvider>? logger = null)
        : this(
            config,
            sessionStore,
            logger,
            httpClient: null,
            timeProvider: null,
            authLockPath: null,
            lockTimeout: null,
            tokenEndpoint: null)
    {
    }

    internal CloudCredentialProvider(
        ResolvedConfig config,
        CloudAuthSessionStore sessionStore,
        ILogger<CloudCredentialProvider>? logger = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        string? authLockPath = null,
        TimeSpan? lockTimeout = null,
        Uri? tokenEndpoint = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _logger = logger ?? NullLogger<CloudCredentialProvider>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _authLockPath = authLockPath ?? Path.Combine(_config.UserConfigDir, ".auth-lock");
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        _tokenEndpoint = tokenEndpoint ?? ResolveAuthenticateEndpoint(_config.Settings.Cloud);
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var cached = _cachedToken;
        if (cached is not null)
        {
            if (cached.HasMoreThan(RefreshWindow, now))
                return cached.AccessToken;

            if (!cached.IsExpired(now))
            {
                StartBackgroundRefresh();
                return cached.AccessToken;
            }
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = UtcNow;
            cached = _cachedToken;
            if (cached is not null)
            {
                if (cached.HasMoreThan(RefreshWindow, now))
                    return cached.AccessToken;

                if (!cached.IsExpired(now))
                {
                    StartBackgroundRefresh();
                    return cached.AccessToken;
                }
            }

            cached = await LoadCurrentTokenAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _cachedToken = cached;
                if (cached.HasMoreThan(RefreshWindow, now))
                    return cached.AccessToken;

                if (!cached.IsExpired(now))
                {
                    StartBackgroundRefresh();
                    return cached.AccessToken;
                }
            }

            return await RefreshOrFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<string> RefreshOrFallbackAsync(CancellationToken cancellationToken)
    {
        if (await HasCredentialMaterialAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var refreshed = await RefreshTokenWithLockAsync(cancellationToken).ConfigureAwait(false);
                _cachedToken = refreshed;
                return refreshed.AccessToken;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // OAuth material exists but refresh failed (stale session, revoked token, etc.).
                // Fall through to API key if available rather than locking the user out.
                var apiKey = _config.Settings.Cloud.ApiKey;
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.LogDebug(ex, "OAuth refresh failed; falling back to cloud.api_key.");
                    return apiKey.Trim();
                }

                throw;
            }
        }

        // No OAuth credentials at all — fall back to legacy API key.
        var fallbackKey = _config.Settings.Cloud.ApiKey;
        if (!string.IsNullOrWhiteSpace(fallbackKey))
            return fallbackKey.Trim();

        throw new InvalidOperationException(NotAuthenticatedMessage);
    }

    internal async Task<bool> HasCredentialMaterialAsync(CancellationToken cancellationToken = default)
    {
        var settings = _config.Settings.Cloud;
        if (!string.IsNullOrWhiteSpace(settings.AuthToken) || !string.IsNullOrWhiteSpace(settings.RefreshToken))
            return true;

        var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null)
            return true;

        return await _sessionStore.HasRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private void StartBackgroundRefresh()
    {
        lock (_backgroundRefreshSync)
        {
            if (_backgroundRefreshTask is { IsCompleted: false })
                return;

            _backgroundRefreshTask = Task.Run(RefreshInBackgroundAsync);
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = UtcNow;
                var current = _cachedToken ?? await LoadCurrentTokenAsync(CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.HasMoreThan(RefreshWindow, now))
                {
                    _cachedToken = current;
                    return;
                }

                _cachedToken = await RefreshTokenWithLockAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background cloud token refresh failed.");
        }
    }

    private async Task<AccessTokenState?> LoadCurrentTokenAsync(CancellationToken cancellationToken)
    {
        var diskToken = await ReadAccessTokenFromDiskAsync(cancellationToken).ConfigureAwait(false);
        if (diskToken is not null)
            return diskToken;

        var configuredToken = _config.Settings.Cloud.AuthToken;
        if (string.IsNullOrWhiteSpace(configuredToken))
            return null;

        var configuredState = CreateAccessTokenState(configuredToken, expiresAt: null);
        if (configuredState is null)
            return null;

        await PersistAccessTokenAsync(configuredState, cancellationToken).ConfigureAwait(false);
        return configuredState;
    }

    private async Task<AccessTokenState> RefreshTokenWithLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_config.UserConfigDir);

        await using var authLock = await TryAcquireAuthLockAsync(cancellationToken).ConfigureAwait(false);

        var sharedToken = await ReadAccessTokenFromDiskAsync(cancellationToken).ConfigureAwait(false);
        if (sharedToken is not null && !sharedToken.IsExpired(UtcNow))
            return sharedToken;

        return await ExchangeRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileStream?> TryAcquireAuthLockAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < _lockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    _authLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Timed out acquiring auth lock at {LockPath}; re-reading shared token without the lock.", _authLockPath);
        return null;
    }

    private async Task<AccessTokenState> ExchangeRefreshTokenAsync(CancellationToken cancellationToken)
    {
        var refreshToken = await GetRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(NotAuthenticatedMessage);

        var settings = _config.Settings.Cloud;
        var clientId = string.IsNullOrWhiteSpace(settings.ClientId)
            ? RepoQlConfig.CloudSettings.DefaultClientId
            : settings.ClientId.Trim();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["refresh_token"] = refreshToken
                    })
                };

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw await CreateRefreshFailureAsync(response.StatusCode, payload, cancellationToken).ConfigureAwait(false);

                var refreshResponse = JsonSerializer.Deserialize(payload, CloudCredentialJsonContext.Default.RefreshResponse);
                if (refreshResponse is null || string.IsNullOrWhiteSpace(refreshResponse.AccessToken))
                    throw new InvalidOperationException("Authentication refresh failed. Response did not include an access token.");

                var refreshed = CreateAccessTokenState(
                    refreshResponse.AccessToken,
                    expiresAt: refreshResponse.ExpiresIn > 0 ? UtcNow.AddSeconds(refreshResponse.ExpiresIn) : null);

                if (refreshed is null)
                    throw new InvalidOperationException("Authentication refresh failed. Response included an invalid access token.");

                var rotatedRefreshToken = string.IsNullOrWhiteSpace(refreshResponse.RefreshToken)
                    ? refreshToken
                    : refreshResponse.RefreshToken;

                var existingSession = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                await _sessionStore.SaveAsync(
                    new CloudAuthSession(
                        refreshed.AccessToken,
                        refreshed.ExpiresAt,
                        IdToken: string.IsNullOrWhiteSpace(refreshResponse.IdToken)
                            ? existingSession?.IdToken
                            : refreshResponse.IdToken,
                        RefreshToken: rotatedRefreshToken),
                    cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Refreshed RepoQL cloud access token.");
                return refreshed;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex, cancellationToken) && attempt == 0)
            {
                _logger.LogDebug(ex, "Cloud token refresh attempt failed; retrying once.");
            }
            catch (SessionExpiredException)
            {
                await InvalidateCachedCredentialsAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(SessionExpiredMessage);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex, cancellationToken))
            {
                throw new InvalidOperationException(NetworkErrorMessage, ex);
            }
        }

        throw new InvalidOperationException(NetworkErrorMessage);
    }

    private async Task<Exception> CreateRefreshFailureAsync(
        HttpStatusCode statusCode,
        string payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            var description = root.TryGetProperty("error_description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;

            if (string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                (description?.Contains("revoked", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (description?.Contains("expired", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return new SessionExpiredException();
            }
        }
        catch (JsonException)
        {
            // Non-JSON failure payloads are handled by status code below.
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new SessionExpiredException();

        if ((int)statusCode >= 500)
            return new HttpRequestException($"Authentication service returned {(int)statusCode}.");

        await InvalidateCachedCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return new InvalidOperationException($"Authentication refresh failed: {(int)statusCode}.");
    }

    private async Task InvalidateCachedCredentialsAsync(CancellationToken cancellationToken)
    {
        _cachedToken = null;
        await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken)
    {
        var stored = await _sessionStore.GetRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        var configured = _config.Settings.Cloud.RefreshToken;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        await _sessionStore.SetRefreshTokenAsync(configured, cancellationToken).ConfigureAwait(false);
        return configured;
    }

    private async Task<AccessTokenState?> ReadAccessTokenFromDiskAsync(CancellationToken cancellationToken)
    {
        var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return session is null
            ? null
            : CreateAccessTokenState(session.AccessToken, session.ExpiresAt);
    }

    private async Task PersistAccessTokenAsync(AccessTokenState token, CancellationToken cancellationToken)
    {
        var existingSession = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _sessionStore.SaveAsync(
            new CloudAuthSession(token.AccessToken, token.ExpiresAt, existingSession?.IdToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientNetworkError(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return false;

        return exception is HttpRequestException or TaskCanceledException;
    }

    internal static AccessTokenState? CreateAccessTokenState(string accessToken, DateTimeOffset? expiresAt)
    {
        var effectiveExpiry = expiresAt
            ?? (JwtPayloadReader.TryReadClaims(accessToken, out var claims) ? claims?.ExpiresAt : null);
        if (effectiveExpiry is null)
            return null;

        return new AccessTokenState(accessToken, effectiveExpiry.Value);
    }

    private static Uri ResolveAuthenticateEndpoint(RepoQlConfig.CloudSettings settings)
    {
#if DEBUG
        var configured = settings.AuthenticateEndpoint?.Trim();
        var effective = string.IsNullOrWhiteSpace(configured)
            ? RepoQlConfig.CloudSettings.DefaultAuthenticateEndpoint
            : configured;
        return new Uri(effective, UriKind.Absolute);
#else
        _ = settings;
        return new Uri(RepoQlConfig.CloudSettings.DefaultAuthenticateEndpoint, UriKind.Absolute);
#endif
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal sealed record AccessTokenState(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
        public bool HasMoreThan(TimeSpan threshold, DateTimeOffset now) => ExpiresAt - now > threshold;
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class SessionExpiredException : Exception;

    [JsonSerializable(typeof(RefreshResponse))]
    private sealed partial class CloudCredentialJsonContext : JsonSerializerContext;
}

/// <summary>
/// Purpose: Register a shared cloud credential provider for RepoQL client-side cloud services.
/// Complexity: Always registers CloudCredentialProvider which evaluates credentials lazily —
/// OAuth session is preferred at call time, with API key as fallback.
/// </summary>
public static class CloudCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddCloudCredentialProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<CloudAuthSessionStore>();
        services.TryAddSingleton<ICloudCredentialProvider?>(sp =>
            new CloudCredentialProvider(
                sp.GetRequiredService<ResolvedConfig>(),
                sp.GetRequiredService<CloudAuthSessionStore>(),
                sp.GetService<ILogger<CloudCredentialProvider>>()));

        return services;
    }
}

internal interface IRefreshTokenStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken);
    Task SetAsync(string refreshToken, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    Task<bool> HasAnyAsync(CancellationToken cancellationToken);
}
