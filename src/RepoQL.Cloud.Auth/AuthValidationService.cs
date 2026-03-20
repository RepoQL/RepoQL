using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Validate incoming bearer credentials for every server-side cloud gRPC request.
/// Complexity: Routes legacy API keys and WorkOS session JWTs through their respective validation paths and attaches AuthIdentity.
/// </summary>
public sealed class AuthValidationService
{
    private readonly HashSet<string> _validKeyHashes;
    private readonly ILogger<AuthValidationService> _logger;
    private readonly AuthOptions _options;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;

    public AuthValidationService(
        IOptions<AuthOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthValidationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _logger = logger;
        _options = options.Value;
        _validKeyHashes = new HashSet<string>(_options.ApiKeyHashes, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Auth config: JwksUri={JwksUri}, Issuer={Issuer}, ClientId={ClientId}, ApiKeyHashes={KeyCount}",
            string.IsNullOrWhiteSpace(_options.JwksUri) ? "(empty)" : _options.JwksUri,
            string.IsNullOrWhiteSpace(_options.Issuer) ? "(empty)" : _options.Issuer,
            string.IsNullOrWhiteSpace(_options.ClientId) ? "(empty)" : _options.ClientId,
            _options.ApiKeyHashes.Length);

        if (!string.IsNullOrWhiteSpace(_options.JwksUri))
        {
            var httpClient = httpClientFactory.CreateClient(nameof(AuthValidationService));
            var documentRetriever = new HttpDocumentRetriever(httpClient)
            {
#if DEBUG
                RequireHttps = false
#else
                RequireHttps = true
#endif
            };

            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                _options.JwksUri,
                new JwksOpenIdConnectConfigurationRetriever(),
                documentRetriever);
        }
    }

    public bool HasLegacyApiKeysConfigured => _validKeyHashes.Count > 0;

    public bool HasJwksConfigured => _configurationManager is not null;

#if DEBUG
    public bool IsDebugBypassEnabled => !HasLegacyApiKeysConfigured && !HasJwksConfigured;
#else
    public bool IsDebugBypassEnabled => false;
#endif

    public async Task<bool> WarmJwksCacheAsync(CancellationToken cancellationToken)
    {
        if (_configurationManager is null)
            return false;

        await _configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task ValidateAsync(ServerCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = await AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        context.SetAuthIdentity(identity);
    }

    private async Task<AuthIdentity> AuthenticateAsync(ServerCallContext context, CancellationToken cancellationToken)
    {
#if DEBUG
        if (IsDebugBypassEnabled)
        {
            return new AuthIdentity(
                UserId: "debug-auth-bypass",
                Method: AuthMethod.ApiKey,
                DisplayName: "debug-auth-bypass");
        }
#endif

        var token = ExtractBearerToken(context);

        if (LooksLikeJwt(token))
            return await ValidateJwtAsync(token, cancellationToken).ConfigureAwait(false);

        if (token.StartsWith("rql_", StringComparison.OrdinalIgnoreCase))
            return ValidateApiKey(token);

        throw Unauthenticated("Unrecognized token format. Run: repoql login");
    }

    private AuthIdentity ValidateApiKey(string token)
    {
        if (_validKeyHashes.Count == 0)
            throw Unauthenticated("Invalid API key");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (_validKeyHashes.Contains(hash))
        {
            var prefix = hash[..Math.Min(8, hash.Length)];
            return new AuthIdentity(
                UserId: "api-key",
                Method: AuthMethod.ApiKey,
                DisplayName: prefix);
        }

        _logger.LogWarning("Rejected API key with hash prefix {HashPrefix}", hash[..Math.Min(8, hash.Length)]);
        throw Unauthenticated("Invalid API key");
    }

    private async Task<AuthIdentity> ValidateJwtAsync(string token, CancellationToken cancellationToken)
    {
        if (_configurationManager is null)
            throw Unauthenticated("Invalid session. Run: repoql login");

        var validationResult = await ValidateJwtCoreAsync(token, cancellationToken).ConfigureAwait(false);
        if (!validationResult.IsValid)
            throw ToRpcException(validationResult.Exception);

        var claimsIdentity = validationResult.ClaimsIdentity;
        var subject = claimsIdentity?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw Unauthenticated("Invalid session. Run: repoql login");

        var organizationId = claimsIdentity?.FindFirst("org_id")?.Value;
        return new AuthIdentity(
            UserId: subject,
            Method: AuthMethod.Session,
            DisplayName: subject,
            OrganizationId: organizationId);
    }

    private async Task<TokenValidationResult> ValidateJwtCoreAsync(string token, CancellationToken cancellationToken)
    {
        var validationParameters = new TokenValidationParameters
        {
            ClockSkew = TimeSpan.FromMinutes(5),
            ConfigurationManager = _configurationManager,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            TryAllIssuerSigningKeys = true,
            // WorkOS access tokens don't include an audience claim — disable audience validation.
            // The issuer already encodes the client ID (e.g. .../user_management/{client_id}).
            ValidateAudience = false,
            ValidateIssuer = !string.IsNullOrWhiteSpace(_options.Issuer),
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidateWithLKG = true,
            ValidAudience = _options.ClientId,
            // WorkOS issuer includes client path: https://auth.repoql.ai/user_management/{client_id}
            // Use prefix matching so the configured base URL matches the full issuer.
            IssuerValidator = !string.IsNullOrWhiteSpace(_options.Issuer)
                ? (issuer, _, _) => issuer.StartsWith(_options.Issuer, StringComparison.OrdinalIgnoreCase)
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' does not match '{_options.Issuer}'")
                : null
        };

        var result = await _tokenHandler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);
        if (result.IsValid || result.Exception is not SecurityTokenSignatureKeyNotFoundException || _configurationManager is null)
            return result;

        _configurationManager.RequestRefresh();
        return await _tokenHandler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);
    }

    private static string ExtractBearerToken(ServerCallContext context)
    {
        var authHeader = context.RequestHeaders.GetValue("authorization");
        if (string.IsNullOrWhiteSpace(authHeader))
            throw Unauthenticated("Missing authorization header. Run: repoql login");

        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw Unauthenticated("Authorization header must use Bearer scheme");

        var token = authHeader[bearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
            throw Unauthenticated("Empty bearer token. Run: repoql login");

        return token;
    }

    private static bool LooksLikeJwt(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var segments = token.Split('.');
        return segments.Length == 3 && Array.TrueForAll(segments, static segment => segment.Length > 0);
    }

    private static RpcException ToRpcException(Exception? exception)
    {
        if (IsExpiredException(exception))
            return Unauthenticated("Session expired. Run: repoql login");

        return exception switch
        {
            SecurityTokenException => Unauthenticated("Invalid session. Run: repoql login"),
            _ => Unauthenticated("Invalid session. Run: repoql login")
        };
    }

    private static bool IsExpiredException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException)
                return true;
        }

        return false;
    }

    private static RpcException Unauthenticated(string message)
    {
        return new(new Status(StatusCode.Unauthenticated, message));
    }
}
