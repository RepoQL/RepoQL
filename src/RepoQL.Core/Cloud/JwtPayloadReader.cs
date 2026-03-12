using System.Text.Json;

namespace RepoQL.Core.Cloud;

/// <summary>
/// Purpose: Decode JWT payload claims without validating the signature for local display and cache metadata.
/// Complexity: Base64url decoding plus lightweight JSON parsing of common claims.
/// </summary>
public static class JwtPayloadReader
{
    public static bool TryReadClaims(string? token, out JwtPayloadClaims? claims)
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

            var subject = TryReadString(root, "sub");
            var email = TryReadString(root, "email")
                        ?? TryReadString(root, "preferred_username")
                        ?? TryReadString(root, "upn");
            var expiresAt = TryReadExpiry(root);

            claims = new JwtPayloadClaims(subject, email, expiresAt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static byte[] Base64UrlDecode(string text)
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
    {
        return payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>
/// Purpose: Represent the JWT claims RepoQL needs for auth UX and cache expiry decisions.
/// Complexity: Immutable value object for a small subset of payload fields.
/// </summary>
public sealed record JwtPayloadClaims(string? Subject, string? Email, DateTimeOffset? ExpiresAt);
