using System.Text.Json;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Extracts JSON from MCP responses that may contain markdown with embedded JSON.
/// MCP servers often return responses formatted for LLM consumption (markdown + JSON).
/// </summary>
public static class JsonResponseExtractor
{
    /// <summary>
    /// Extracts JSON from a response that may contain markdown with embedded JSON.
    /// Returns the JSON portion if found, otherwise wraps the text as an error object.
    /// </summary>
    public static string Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "null";

        var trimmed = text.TrimStart();

        // Already valid JSON? Return as-is
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            if (IsValidJson(trimmed))
                return trimmed;
        }

        // Try to extract JSON array or object using bracket matching
        var extracted = ExtractByBracketMatching(text, '[', ']')
                     ?? ExtractByBracketMatching(text, '{', '}');

        if (extracted != null)
            return extracted;

        // No valid JSON found - wrap as error so macro can still parse it
        return WrapAsError(text);
    }

    /// <summary>
    /// Wraps a non-JSON response as a JSON error object.
    /// </summary>
    public static string WrapAsError(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
        return $"{{\"error\": \"{escaped}\"}}";
    }

    /// <summary>
    /// Extracts a JSON structure by finding matching brackets.
    /// Handles nested structures and strings correctly.
    /// </summary>
    public static string? ExtractByBracketMatching(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0)
            return null;

        // Count brackets to find the matching close
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
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    // Found matching bracket - extract and validate
                    var candidate = text[start..(i + 1)];
                    if (IsValidJson(candidate))
                        return candidate;

                    // Not valid JSON, try next occurrence
                    return ExtractByBracketMatching(text[(start + 1)..], open, close);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a string is valid JSON.
    /// </summary>
    public static bool IsValidJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
