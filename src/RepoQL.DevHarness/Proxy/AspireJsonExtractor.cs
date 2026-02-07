using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Extract JSON payloads from Aspire MCP tool responses.
/// Complexity: Aspire wraps JSON in text; bracket-matching finds the payload.
/// </summary>
internal static class AspireJsonExtractor
{
    public static bool TryExtract(string? text, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimStart();
        if ((trimmed.StartsWith('[') || trimmed.StartsWith('{')) && IsValidJson(trimmed))
        {
            json = trimmed;
            return true;
        }

        var candidate = ExtractByBracketMatching(text, '[', ']') ?? ExtractByBracketMatching(text, '{', '}');
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            json = candidate;
            return true;
        }

        return false;
    }

    public static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractByBracketMatching(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    var candidate = text[start..(i + 1)];
                    if (IsValidJson(candidate))
                        return candidate;

                    return ExtractByBracketMatching(text[(start + 1)..], open, close);
                }
            }
        }

        return null;
    }
}
