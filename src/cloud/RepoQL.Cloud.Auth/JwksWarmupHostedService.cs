using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Prime auth state at service startup so session validation is ready before the first call.
/// Complexity: Warms the JWKS cache and emits explicit startup warnings for degraded or bypassed auth modes.
/// </summary>
public sealed class JwksWarmupHostedService : IHostedService
{
    private readonly AuthValidationService _validationService;
    private readonly ILogger<JwksWarmupHostedService> _logger;

    public JwksWarmupHostedService(
        AuthValidationService validationService,
        ILogger<JwksWarmupHostedService> logger)
    {
        _validationService = validationService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        if (_validationService.IsDebugBypassEnabled)
        {
            _logger.LogWarning(
                "AUTH BYPASS ENABLED (DEBUG only). No API key hashes or JWKS URI are configured; incoming gRPC calls will skip auth.");
            return;
        }
#endif

        if (!_validationService.HasJwksConfigured)
        {
            if (!_validationService.HasLegacyApiKeysConfigured)
            {
                _logger.LogError(
                    "No server auth mechanism is configured. Configure Auth:ApiKeyHashes or Auth:JwksUri/Auth:ClientId.");
            }

            return;
        }

        try
        {
            await _validationService.WarmJwksCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to warm JWKS cache at startup. Session auth is unavailable until keys can be fetched; legacy API keys still work.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
