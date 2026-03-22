using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;

namespace RepoQL.Client.Auth;

/// <summary>
/// Purpose: Run RepoQL's interactive cloud login/logout/whoami workflows for both CLI and command surfaces.
/// Complexity: Handles PKCE/device OAuth exchanges, loopback callback listening, local credential persistence, and user-facing auth summaries.
/// </summary>
public sealed class CloudAuthService : IDisposable
{
    private static readonly TimeSpan BrowserCallbackTimeout = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ResolvedConfig _config;
    private readonly CloudAuthSessionStore _sessionStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;

    public CloudAuthService(
        ResolvedConfig config,
        CloudAuthSessionStore sessionStore,
        ILogger<CloudAuthService>? logger = null)
        : this(
            config,
            sessionStore,
            logger,
            httpClient: null,
            timeProvider: null)
    {
    }

    internal CloudAuthService(
        ResolvedConfig config,
        CloudAuthSessionStore sessionStore,
        ILogger? logger = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _logger = logger ?? NullLogger<CloudAuthService>.Instance;
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
    }

    public Task<LoginResult> LoginAsync(
        bool useDeviceCode,
        IProgress<AuthProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return useDeviceCode
            ? LoginWithDeviceCodeAsync(progress, cancellationToken)
            : LoginWithBrowserAsync(progress, cancellationToken);
    }

    public async Task<string> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var hasSession = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;
        var hasRefreshToken = await _sessionStore.HasRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!hasSession && !hasRefreshToken)
            return "Not logged in";

        await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        return "Logged out";
    }

    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessionStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            JwtPayloadReader.TryReadClaims(session.AccessToken, out var accessClaims);
            JwtPayloadReader.TryReadClaims(session.IdToken, out var idClaims);

            var email = idClaims?.Email ?? accessClaims?.Email ?? idClaims?.Subject ?? accessClaims?.Subject ?? "unknown";
            var userId = idClaims?.Subject ?? accessClaims?.Subject ?? "unknown";
            var expiresAt = accessClaims?.ExpiresAt ?? session.ExpiresAt;

            return string.Join(
                Environment.NewLine,
                [
                    $"Email: {email}",
                    $"User ID: {userId}",
                    "Auth method: session",
                    $"Token expiry: {expiresAt:O}"
                ]);
        }

        var apiKey = _config.Settings.Cloud.ApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            return $"Authenticated via API key (hash prefix: {ComputeHashPrefix(apiKey)})";

        return "Not logged in. Run: repoql login";
    }

    private async Task<LoginResult> LoginWithBrowserAsync(
        IProgress<AuthProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var clientId = GetClientId();

        if (IsRunningInWsl())
        {
            progress?.Report(new AuthProgressUpdate(
                AuthProgressKind.Info,
                "WSL detected. If browser login is unavailable here, run: repoql login --device-code"));
        }

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = CreateNonce();

        await using var listener = new LoopbackCallbackListener(_logger);
        var redirectUri = listener.RedirectUri;

        var authorizationUrl = BuildUrl(
            GetAuthorizationEndpoint(),
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri.ToString(),
                ["response_type"] = "code",
                ["provider"] = "authkit",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state
            });

        var launch = OpenBrowser(authorizationUrl);
        if (!launch.Opened)
        {
            progress?.Report(new AuthProgressUpdate(
                AuthProgressKind.Warning,
                $"Open this URL to authenticate: {authorizationUrl}"));
        }

        progress?.Report(new AuthProgressUpdate(AuthProgressKind.Waiting, "Waiting for authentication..."));

        var callback = await listener.WaitForCallbackAsync(state, BrowserCallbackTimeout, cancellationToken).ConfigureAwait(false);
        if (callback.IsTimeout)
            throw new InvalidOperationException("Authentication timed out. Run: repoql login to try again");

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            if (string.Equals(callback.Error, "access_denied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Authentication cancelled. Run: repoql login to try again");

            throw new InvalidOperationException(callback.ErrorDescription ?? callback.Error);
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
            throw new InvalidOperationException("Authentication failed. No authorization code was received.");

        var tokenResponse = await ExchangeAuthorizationCodeAsync(
            clientId,
            callback.Code,
            verifier,
            redirectUri,
            cancellationToken).ConfigureAwait(false);

        return await PersistLoginAsync(tokenResponse, cancellationToken).ConfigureAwait(false);
    }

    private PendingDeviceFlow? _pendingDeviceFlow;

    /// <summary>Whether a device code flow is in progress and waiting for the user to authenticate.</summary>
    internal bool HasPendingDeviceFlow => _pendingDeviceFlow is { Expired: false };

    /// <summary>
    /// Phase 1: Request a device code from the auth server, store it, and return immediately
    /// with the URL and user code. The caller shows these to the user, then calls
    /// <see cref="CompleteDeviceCodeAsync"/> to poll for completion.
    /// </summary>
    internal async Task<DeviceCodeInfo> BeginDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var clientId = GetClientId();
        using var request = new HttpRequestMessage(HttpMethod.Post, GetDeviceAuthorizationEndpoint())
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId
            })
        };

        DeviceAuthorizationResponse deviceResponse;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateWorkOsException(payload, "Device authorization failed.");

            deviceResponse = JsonSerializer.Deserialize<DeviceAuthorizationResponse>(payload, JsonOptions)
                ?? throw new InvalidOperationException("Device authorization failed. Response was empty.");
        }
        catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
        {
            throw new InvalidOperationException("Cannot reach authentication service. Check your connection and try again", ex);
        }

        if (string.IsNullOrWhiteSpace(deviceResponse.DeviceCode) ||
            string.IsNullOrWhiteSpace(deviceResponse.UserCode) ||
            string.IsNullOrWhiteSpace(deviceResponse.VerificationUriComplete))
        {
            throw new InvalidOperationException("Device authorization failed. Response did not include the required verification details.");
        }

        _pendingDeviceFlow = new PendingDeviceFlow(
            clientId,
            deviceResponse.DeviceCode,
            _timeProvider.GetUtcNow().AddSeconds(Math.Max(deviceResponse.ExpiresIn, 1)),
            Math.Max(deviceResponse.Interval, 1));

        return new DeviceCodeInfo(deviceResponse.VerificationUriComplete, deviceResponse.UserCode);
    }

    /// <summary>
    /// Phase 2: Poll for completion of a previously started device code flow.
    /// Returns the login result on success.
    /// </summary>
    internal async Task<LoginResult> CompleteDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var flow = _pendingDeviceFlow
            ?? throw new InvalidOperationException("No pending device code flow. Call auth.login first.");

        if (flow.Expired)
        {
            _pendingDeviceFlow = null;
            throw new InvalidOperationException("Code expired. Call auth.login to start a new flow.");
        }

        var intervalSeconds = flow.IntervalSeconds;

        while (_timeProvider.GetUtcNow() < flow.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

            TokenExchangeResponse? tokenResponse;
            try
            {
                tokenResponse = await PollDeviceCodeAsync(flow.ClientId, flow.DeviceCode, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceFlowPendingException)
            {
                continue;
            }
            catch (DeviceFlowSlowDownException)
            {
                intervalSeconds += 5;
                continue;
            }
            catch (DeviceFlowExpiredException)
            {
                _pendingDeviceFlow = null;
                throw new InvalidOperationException("Code expired. Call auth.login to start a new flow.");
            }

            if (tokenResponse is not null)
            {
                _pendingDeviceFlow = null;
                return await PersistLoginAsync(tokenResponse, cancellationToken).ConfigureAwait(false);
            }
        }

        _pendingDeviceFlow = null;
        throw new InvalidOperationException("Code expired. Call auth.login to start a new flow.");
    }

    /// <summary>
    /// Combined flow for CLI use: begins the device code flow, reports progress, and polls to completion.
    /// </summary>
    private async Task<LoginResult> LoginWithDeviceCodeAsync(
        IProgress<AuthProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var info = await BeginDeviceCodeAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new AuthProgressUpdate(
            AuthProgressKind.Info,
            $"To authenticate, visit: {info.VerificationUrl}"));
        progress?.Report(new AuthProgressUpdate(
            AuthProgressKind.Info,
            $"Enter code: {info.UserCode}"));
        progress?.Report(new AuthProgressUpdate(AuthProgressKind.Waiting, "Waiting for authentication..."));

        return await CompleteDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed record DeviceCodeInfo(string VerificationUrl, string UserCode);

    private sealed record PendingDeviceFlow(string ClientId, string DeviceCode, DateTimeOffset ExpiresAt, int IntervalSeconds)
    {
        public bool Expired => TimeProvider.System.GetUtcNow() >= ExpiresAt;
    }

    private async Task<TokenExchangeResponse> ExchangeAuthorizationCodeAsync(
        string clientId,
        string code,
        string codeVerifier,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GetAuthenticateEndpoint())
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier
            })
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateWorkOsException(payload, "Authentication failed.");

            return JsonSerializer.Deserialize<TokenExchangeResponse>(payload, JsonOptions)
                ?? throw new InvalidOperationException("Authentication failed. Token response was empty.");
        }
        catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
        {
            throw new InvalidOperationException("Cannot reach authentication service. Check your connection and try again", ex);
        }
    }

    private async Task<TokenExchangeResponse?> PollDeviceCodeAsync(
        string clientId,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GetAuthenticateEndpoint())
        {
            Content = new FormUrlEncodedContent(CreateDeviceCodePollForm(clientId, deviceCode))
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<TokenExchangeResponse>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("Authentication failed. Token response was empty.");
            }

            var error = ParseWorkOsError(payload);
            switch (error.Error?.ToLowerInvariant())
            {
                case "authorization_pending":
                    throw new DeviceFlowPendingException();
                case "slow_down":
                    throw new DeviceFlowSlowDownException();
                case "expired_token":
                    throw new DeviceFlowExpiredException();
                case "access_denied":
                    throw new InvalidOperationException("Authentication cancelled. Run: repoql login --device-code to try again");
                default:
                    throw CreateWorkOsException(payload, "Authentication failed.");
            }
        }
        catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
        {
            throw new InvalidOperationException("Cannot reach authentication service. Check your connection and try again", ex);
        }
    }

    private async Task<LoginResult> PersistLoginAsync(TokenExchangeResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken))
            throw new InvalidOperationException("Authentication failed. Response did not include an access token.");

        if (string.IsNullOrWhiteSpace(response.RefreshToken))
            throw new InvalidOperationException("Authentication failed. Response did not include a refresh token.");

        var expiresAt = response.ExpiresIn > 0
            ? _timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn)
            : JwtPayloadReader.TryReadClaims(response.AccessToken, out var accessClaims)
                ? accessClaims?.ExpiresAt
                : null;
        if (expiresAt is null)
            throw new InvalidOperationException("Authentication failed. Response did not include a usable token expiry.");

        await _sessionStore.SaveAsync(
            new CloudAuthSession(
                response.AccessToken,
                expiresAt.Value,
                response.IdToken,
                response.RefreshToken),
            cancellationToken).ConfigureAwait(false);

        JwtPayloadReader.TryReadClaims(response.IdToken, out var idClaims);
        JwtPayloadReader.TryReadClaims(response.AccessToken, out var accessTokenClaims);
        var email = idClaims?.Email ?? accessTokenClaims?.Email ?? idClaims?.Subject ?? accessTokenClaims?.Subject ?? "unknown";
        return new LoginResult(email);
    }

    private string GetClientId()
    {
        var configured = _config.Settings.Cloud.ClientId?.Trim();
        return string.IsNullOrWhiteSpace(configured)
            ? RepoQlConfig.CloudSettings.DefaultClientId
            : configured;
    }

    internal Dictionary<string, string> CreateDeviceCodePollForm(string clientId, string deviceCode)
    {
        return new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = clientId
        };
    }

    internal Uri GetAuthorizationEndpoint()
    {
#if DEBUG
        return ResolveEndpoint(_config.Settings.Cloud.AuthorizationEndpoint, RepoQlConfig.CloudSettings.DefaultAuthorizationEndpoint);
#else
        return new Uri(RepoQlConfig.CloudSettings.DefaultAuthorizationEndpoint, UriKind.Absolute);
#endif
    }

    internal Uri GetAuthenticateEndpoint()
    {
#if DEBUG
        return ResolveEndpoint(_config.Settings.Cloud.AuthenticateEndpoint, RepoQlConfig.CloudSettings.DefaultAuthenticateEndpoint);
#else
        return new Uri(RepoQlConfig.CloudSettings.DefaultAuthenticateEndpoint, UriKind.Absolute);
#endif
    }

    internal Uri GetDeviceAuthorizationEndpoint()
    {
#if DEBUG
        return ResolveEndpoint(_config.Settings.Cloud.DeviceAuthorizationEndpoint, RepoQlConfig.CloudSettings.DefaultDeviceAuthorizationEndpoint);
#else
        return new Uri(RepoQlConfig.CloudSettings.DefaultDeviceAuthorizationEndpoint, UriKind.Absolute);
#endif
    }

#if DEBUG
    private static Uri ResolveEndpoint(string? configuredEndpoint, string defaultEndpoint)
    {
        var effective = string.IsNullOrWhiteSpace(configuredEndpoint)
            ? defaultEndpoint
            : configuredEndpoint.Trim();
        return new Uri(effective, UriKind.Absolute);
    }
#endif

    internal static bool IsRunningInWsl()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/version"))
            return false;

        try
        {
            var version = File.ReadAllText("/proc/version");
            return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                   || version.Contains("wsl", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string GenerateCodeVerifier()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Base64UrlEncode(random);
    }

    internal static string GenerateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    internal static string ComputeHashPrefix(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash)[..8];
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static string CreateNonce()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Base64UrlEncode(random);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static Uri BuildUrl(Uri baseUri, IReadOnlyDictionary<string, string> query)
    {
        var builder = new StringBuilder(baseUri.ToString());
        builder.Append('?');

        var first = true;
        foreach (var (key, value) in query)
        {
            if (!first)
                builder.Append('&');

            first = false;
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return new Uri(builder.ToString());
    }

    private static BrowserLaunchResult OpenBrowser(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            return BrowserLaunchResult.Success();
        }
        catch (Exception ex)
        {
            return BrowserLaunchResult.Failure(ex.Message);
        }
    }

    private static bool IsNetworkError(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return false;

        return ex is HttpRequestException or TaskCanceledException;
    }

    private static InvalidOperationException CreateWorkOsException(string payload, string fallbackMessage)
    {
        var error = ParseWorkOsError(payload);
        if (string.Equals(error.Error, "access_denied", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("Authentication cancelled. Run: repoql login to try again");

        return new InvalidOperationException(error.ErrorDescription ?? error.Error ?? fallbackMessage);
    }

    private static WorkOsError ParseWorkOsError(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new WorkOsError();

        try
        {
            return JsonSerializer.Deserialize<WorkOsError>(payload, JsonOptions) ?? new WorkOsError();
        }
        catch (JsonException)
        {
            return new WorkOsError();
        }
    }

    public sealed record LoginResult(string DisplayName);

    public sealed record AuthProgressUpdate(AuthProgressKind Kind, string Message);

    public enum AuthProgressKind
    {
        Info,
        Warning,
        Waiting
    }

    private sealed class BrowserLaunchResult(bool opened, string? error)
    {
        public bool Opened { get; } = opened;
        public string? Error { get; } = error;

        public static BrowserLaunchResult Success() => new(true, null);
        public static BrowserLaunchResult Failure(string error) => new(false, error);
    }

    private sealed class TokenExchangeResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class DeviceAuthorizationResponse
    {
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; init; }

        [JsonPropertyName("user_code")]
        public string? UserCode { get; init; }

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; init; }

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; }
    }

    private sealed class WorkOsError
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }

    private sealed class DeviceFlowPendingException : Exception;
    private sealed class DeviceFlowSlowDownException : Exception;
    private sealed class DeviceFlowExpiredException : Exception;

}

/// <summary>
/// Purpose: Wait for one OAuth redirect on an OS-assigned loopback port and return the parsed callback data.
/// Complexity: Minimal HTTP over TcpListener with timeout handling and success/error HTML responses.
/// </summary>
internal sealed class LoopbackCallbackListener : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ILogger _logger;
    private bool _disposed;

    public Uri RedirectUri { get; }

    public LoopbackCallbackListener(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
        RedirectUri = StartListener();
    }

    public async Task<LoopbackCallbackResult> WaitForCallbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var client = await _listener.AcceptTcpClientAsync(timeoutCts.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                await WriteHtmlResponseAsync(stream, HttpStatusCode.BadRequest, "Authentication failed. Try again.", timeoutCts.Token)
                    .ConfigureAwait(false);
                return new LoopbackCallbackResult(Error: "invalid_request", ErrorDescription: "Authentication failed. No callback data was received.");
            }

            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false)))
            {
            }

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var callbackUri))
            {
                await WriteHtmlResponseAsync(stream, HttpStatusCode.BadRequest, "Authentication failed. Try again.", timeoutCts.Token)
                    .ConfigureAwait(false);
                return new LoopbackCallbackResult(Error: "invalid_request", ErrorDescription: "Authentication failed. Callback URI was invalid.");
            }

            var query = HttpUtility.ParseQueryString(callbackUri.Query);
            var error = query["error"];
            var errorDescription = query["error_description"];
            var state = query["state"];
            var code = query["code"];

            if (!string.IsNullOrWhiteSpace(error))
            {
                await WriteHtmlResponseAsync(stream, HttpStatusCode.BadRequest, "Authentication failed. You can close this tab.", timeoutCts.Token)
                    .ConfigureAwait(false);
                return new LoopbackCallbackResult(Error: error, ErrorDescription: errorDescription);
            }

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await WriteHtmlResponseAsync(stream, HttpStatusCode.BadRequest, "Authentication failed. Invalid state.", timeoutCts.Token)
                    .ConfigureAwait(false);
                return new LoopbackCallbackResult(Error: "invalid_state", ErrorDescription: "Authentication failed: invalid state. Try again.");
            }

            await WriteHtmlResponseAsync(stream, HttpStatusCode.OK, "Authentication successful. You can close this tab.", timeoutCts.Token)
                .ConfigureAwait(false);
            return new LoopbackCallbackResult(Code: code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LoopbackCallbackResult.Timeout();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Loopback OAuth callback listener failed.");
            return new LoopbackCallbackResult(Error: "listener_error", ErrorDescription: ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private Uri StartListener()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        return new Uri($"http://localhost:{endpoint.Port}/callback");
    }

    private static async Task WriteHtmlResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        var html = $$"""
            <html>
            <body style="font-family: system-ui; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0;">
                <div style="text-align: center;">
                    <h1>{{WebUtility.HtmlEncode(message)}}</h1>
                </div>
            </body>
            </html>
            """;

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record LoopbackCallbackResult(
    string? Code = null,
    string? Error = null,
    string? ErrorDescription = null,
    bool IsTimeout = false)
{
    public static LoopbackCallbackResult Timeout() => new(IsTimeout: true);
}
