using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Core.Configuration;

namespace RepoQL.Core.Cloud;

/// <summary>
/// Purpose: Persist RepoQL's local OAuth session state across processes.
/// Complexity: Coordinates access-token file storage, refresh-token secure storage, and file permission tightening.
/// </summary>
public sealed partial class CloudAuthSessionStore
{
    private static readonly AuthSessionJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    private readonly ResolvedConfig _config;
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly ILogger _logger;
    private readonly string _authFilePath;

    public CloudAuthSessionStore(
        ResolvedConfig config,
        ILogger<CloudAuthSessionStore>? logger = null)
        : this(config, logger, refreshTokenStore: null, authFilePath: null)
    {
    }

    internal CloudAuthSessionStore(
        ResolvedConfig config,
        ILogger? logger = null,
        IRefreshTokenStore? refreshTokenStore = null,
        string? authFilePath = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? NullLogger<CloudAuthSessionStore>.Instance;
        _authFilePath = authFilePath ?? Path.Combine(_config.UserConfigDir, "auth.json");
        _refreshTokenStore = refreshTokenStore ?? RefreshTokenStore.CreateDefault(_config.UserConfigDir, _logger);
    }

    public async Task SaveAsync(CloudAuthSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Directory.CreateDirectory(_config.UserConfigDir);

        if (!string.IsNullOrWhiteSpace(session.RefreshToken))
            await _refreshTokenStore.SetAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new AuthFileEntry
        {
            AccessToken = session.AccessToken,
            ExpiresAt = session.ExpiresAt,
            IdToken = session.IdToken
        }, JsonContext.AuthFileEntry);

        var tempPath = _authFilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);
        RestrictFilePermissions(tempPath);
        File.Move(tempPath, _authFilePath, overwrite: true);
        RestrictFilePermissions(_authFilePath);
    }

    public async Task<CloudAuthSession?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_authFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_authFilePath, cancellationToken).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize(json, JsonContext.AuthFileEntry);
            if (entry is null || string.IsNullOrWhiteSpace(entry.AccessToken))
                return null;

            return new CloudAuthSession(entry.AccessToken, entry.ExpiresAt, entry.IdToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Failed to read cached auth token from {AuthPath}.", _authFilePath);
            return null;
        }
    }

    public Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken = default)
        => _refreshTokenStore.GetAsync(cancellationToken);

    public Task SetRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        => _refreshTokenStore.SetAsync(refreshToken, cancellationToken);

    public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default)
        => _refreshTokenStore.HasAnyAsync(cancellationToken);

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _refreshTokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(_authFilePath))
                File.Delete(_authFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove cached auth token file at {AuthPath}.", _authFilePath);
        }
    }

    private void RestrictFilePermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsFilePermissionHelper.RestrictToCurrentUser(path);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to tighten file permissions for {Path}.", path);
        }
    }

    private sealed class AuthFileEntry
    {
        public required string AccessToken { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public string? IdToken { get; init; }
    }

    [JsonSerializable(typeof(AuthFileEntry))]
    private sealed partial class AuthSessionJsonContext : JsonSerializerContext;
}

/// <summary>
/// Purpose: Carry the local session tokens RepoQL persists after an interactive login.
/// Complexity: Simple immutable DTO for access/id/refresh token material and access expiry.
/// </summary>
public sealed record CloudAuthSession(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string? IdToken = null,
    string? RefreshToken = null);
