namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Normalize log severity labels for telemetry queries and output.
/// Complexity: Small mapping helper for common severity aliases.
/// </summary>
internal static class HarnessLogLevel
{
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = Normalize(value);
        return normalized is "debug" or "info" or "warning" or "error";
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "warn" => "warning",
            "information" => "info",
            "err" => "error",
            "fatal" => "error",
            "critical" => "error",
            _ => trimmed
        };
    }

    public static string? FromSeverityNumber(int severityNumber)
    {
        return severityNumber switch
        {
            <= 4 => "debug",
            <= 8 => "debug",
            <= 12 => "info",
            <= 16 => "warning",
            <= 20 => "error",
            <= 24 => "error",
            _ => null
        };
    }
}
