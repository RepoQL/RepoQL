using System.Globalization;
using System.Text.RegularExpressions;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Parse relative and absolute time filters for telemetry queries.
/// Complexity: Small parser for duration suffixes and ISO timestamps.
/// </summary>
internal static class HarnessTimeParser
{
    private static readonly Regex RelativePattern = new(@"^\s*(\d+)\s*([smh])\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(string? value, DateTimeOffset now, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var match = RelativePattern.Match(trimmed);
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                return false;

            var unit = match.Groups[2].Value.ToLowerInvariant();
            var offset = unit switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.Zero
            };

            if (offset == TimeSpan.Zero && amount != 0)
                return false;

            timestamp = now - offset;
            return true;
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        return false;
    }
}
