using System.Text.Json;
using RepoQL.Contracts;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Expose host-side cloud authentication, inference, and contextual embedding diagnostics through SQL.
/// Complexity: Coordinates credential checks, JWT/session inspection, lightweight embedding reachability probing, and registry summary formatting.
/// </summary>
[UdfClass]
public sealed class CloudDiagnosticsUdf(
    ICloudCredentialProvider? cloudCredentialProvider = null,
    IInferenceProvider? inferenceProvider = null,
    IContextualEmbeddingProvider? contextualEmbeddingProvider = null,
    UriRegistry? uriRegistry = null,
    RepoQlConfig? config = null)
{
    private readonly ICloudCredentialProvider? _cloudCredentialProvider = cloudCredentialProvider;
    private readonly IInferenceProvider? _inferenceProvider = inferenceProvider;
    private readonly IContextualEmbeddingProvider? _contextualEmbeddingProvider = contextualEmbeddingProvider;
    private readonly UriRegistry? _uriRegistry = uriRegistry;
    private readonly RepoQlConfig _config = config ?? new RepoQlConfig();

    /// <summary>
    /// Returns cloud authentication, inference, and embedding diagnostics as key-value text.
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_cloud_diagnostics_internal", MacroName = "cloud_diagnostics", Description = "Returns cloud auth, inference, and embedding diagnostics as key-value text", IsPure = false)]
    public string GetDiagnostics([UdfDefault("''")] string? _dummy)
    {
        var lines = new List<string>();

        AppendAuthentication(lines);
        AppendInference(lines);
        AppendEmbeddings(lines);

        return string.Join(Environment.NewLine, lines);
    }

    private void AppendAuthentication(List<string> lines)
    {
        try
        {
            if (_cloudCredentialProvider is null)
            {
                lines.Add("auth: not authenticated");
                lines.Add("auth_error: Cloud credential provider not configured.");
                return;
            }

            var token = _cloudCredentialProvider.GetTokenAsync().GetAwaiter().GetResult();
            TryReadClaims(token, out var claims);

            var identity = claims?.Email ?? claims?.Subject;

            lines.Add("auth: authenticated");
            if (!string.IsNullOrWhiteSpace(identity))
                lines.Add($"identity: {identity}");

            lines.Add($"token: {FormatTokenStatus(claims?.ExpiresAt)}");
        }
        catch (Exception ex)
        {
            lines.Add("auth: not authenticated");
            lines.Add($"auth_error: {NormalizeAuthError(ex.Message)}");
        }
    }

    private void AppendInference(List<string> lines)
    {
        if (_inferenceProvider?.Available != true)
        {
            lines.Add("inference: not configured");
            return;
        }

        lines.Add("inference: available");
        if (!string.IsNullOrWhiteSpace(_config.Inference.ServiceUrl))
            lines.Add($"inference_url: {_config.Inference.ServiceUrl}");
    }

    private void AppendEmbeddings(List<string> lines)
    {
        if (_contextualEmbeddingProvider?.Enabled != true)
        {
            lines.Add("embedding: disabled");
            return;
        }

        lines.Add("embedding: enabled");
        lines.Add($"embedding_provider: {_contextualEmbeddingProvider.GetType().Name}");

        var reachable = false;
        string? reachabilityError = null;

        try
        {
            _contextualEmbeddingProvider.InitializeAsync().GetAwaiter().GetResult();
            reachable = true;
        }
        catch (Exception ex)
        {
            reachabilityError = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(_contextualEmbeddingProvider.Model))
            lines.Add($"embedding_model: {_contextualEmbeddingProvider.Model}");

        if (_contextualEmbeddingProvider.Dimension > 0)
            lines.Add($"embedding_dimension: {_contextualEmbeddingProvider.Dimension}");

        if (!string.IsNullOrWhiteSpace(_config.Embedding.Remote.Url))
            lines.Add($"embedding_url: {_config.Embedding.Remote.Url}");

        lines.Add($"embedding_reachable: {reachable.ToString().ToLowerInvariant()}");
        if (!reachable && !string.IsNullOrWhiteSpace(reachabilityError))
            lines.Add($"embedding_error: {reachabilityError}");

        if (_uriRegistry is null)
            return;

        var summary = _uriRegistry.GetSummary();
        summary.ByEmbeddingStatus.TryGetValue(EmbeddingStatus.NotApplicable, out var notApplicableCount);

        lines.Add($"embedded_files: {summary.EmbeddedFiles}");
        lines.Add($"total_files: {summary.TotalFiles}");
        lines.Add($"not_applicable: {notApplicableCount}");
    }

    private static string FormatTokenStatus(DateTimeOffset? expiresAt)
    {
        if (expiresAt is null)
            return "valid";

        var remaining = expiresAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "expired";

        return $"valid (expires in {FormatRemainingDuration(remaining)})";
    }

    private static string FormatRemainingDuration(TimeSpan remaining)
    {
        var clamped = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        var roundedMinutes = Math.Max(1, (int)Math.Ceiling(clamped.TotalMinutes));
        var hours = roundedMinutes / 60;
        var minutes = roundedMinutes % 60;

        if (hours > 0)
            return $"{hours}h {minutes}m";

        return $"{roundedMinutes}m";
    }

    private static string NormalizeAuthError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Not logged in. Run auth.login";

        if (message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return "Not logged in. Run auth.login";

        if (message.Contains("session expired", StringComparison.OrdinalIgnoreCase))
            return "Session expired. Run auth.login";

        return message.Replace("repoql login", "auth.login", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static bool TryReadClaims(string? token, out JwtPayloadClaims? claims)
    {
        claims = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var segments = token.Split('.');
        if (segments.Length < 2)
            return false;

        try
        {
            var payloadBytes = Base64UrlDecode(segments[1]);
            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;

            claims = new JwtPayloadClaims(
                Subject: TryReadString(root, "sub"),
                Email: TryReadString(root, "email")
                    ?? TryReadString(root, "preferred_username")
                    ?? TryReadString(root, "upn"),
                ExpiresAt: TryReadExpiry(root));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };

        return Convert.FromBase64String(normalized);
    }

    private static DateTimeOffset? TryReadExpiry(JsonElement payload)
    {
        if (!payload.TryGetProperty("exp", out var expElement))
            return null;

        return expElement.TryGetInt64(out var expSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(expSeconds)
            : null;
    }

    private static string? TryReadString(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record JwtPayloadClaims(string? Subject, string? Email, DateTimeOffset? ExpiresAt);
}
