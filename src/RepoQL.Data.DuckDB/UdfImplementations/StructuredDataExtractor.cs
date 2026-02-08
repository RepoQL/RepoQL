using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Extracts structured data from text that may contain various formats.
/// Detects and parses: JSON, JSONL, TSV, CSV, YAML, and embedded structured data in prose.
/// All formats are normalized to JSON for uniform SQL consumption.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b> Convert diverse text formats into JSON for DuckDB querying.
/// Used by the parse_structured UDF and MCP response processing.</para>
/// <para><b>Complexity:</b> Multi-format detection with priority ordering to minimize false positives.
/// The detection order (JSON -> JSONL -> TSV -> CSV -> YAML -> Embedded -> Structured Text) is carefully
/// chosen based on format distinctiveness and false positive risk.</para>
/// </remarks>
public static partial class StructuredDataExtractor
{
    // Cached YAML deserializer for performance
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Extracts structured data from text and converts to JSON.
    /// Attempts detection in priority order: JSON, JSONL, TSV, CSV, YAML, Embedded, Structured Text.
    /// When unwrap is true (default), JSON objects containing arrays of objects are unwrapped
    /// to return the array directly — e.g. {"data": {"results": [{...}]}} returns [{...}].
    /// </summary>
    /// <param name="text">The text to parse</param>
    /// <param name="unwrap">When true, unwraps envelope objects to find the best array of objects</param>
    /// <returns>JSON string (array or object), or wrapped text if no format detected</returns>
    public static string Extract(string? text, bool unwrap = true)
    {
        if (string.IsNullOrEmpty(text))
            return "null";

        var trimmed = text.TrimStart();

        // 1. Pure JSON (fast path) - starts with [ or {, validates with JsonDocument
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            if (IsValidJson(trimmed))
            {
                if (unwrap && trimmed.StartsWith('{'))
                    return UnwrapJsonEnvelope(trimmed);
                return trimmed;
            }
        }

        // 2. JSONL - multiple lines, each a valid JSON object
        var jsonl = TryParseAsJsonl(text);
        if (jsonl != null) return jsonl;

        // 3. TSV - tabs present, >=2 columns, >=2 data rows
        var tsv = TryParseAsTsv(text);
        if (tsv != null) return tsv;

        // 4. CSV - commas (no tabs), >=2 columns, >=2 data rows
        var csv = TryParseAsCsv(text);
        if (csv != null) return csv;

        // 5. YAML - starts with ---\n OR >=2 consecutive key: value lines
        var yaml = TryParseAsYaml(text);
        if (yaml != null) return yaml;

        // 6. Embedded structured data in prose (JSON/YAML/CSV blocks)
        var embedded = TryParseEmbeddedStructuredData(text);
        if (embedded != null) return embedded;

        // 7. Structured text (- Key: Value format with delimiters)
        var structured = TryParseStructuredText(text);
        if (structured != null) return structured;

        // 8. Fallback: wrap as text object
        return WrapAsText(text);
    }

    #region Format Detectors

    /// <summary>
    /// Attempts to parse text as JSONL (JSON Lines) format.
    /// Requires at least 2 lines where each non-empty line is a valid JSON object.
    /// </summary>
    public static string? TryParseAsJsonl(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        var objects = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!trimmed.StartsWith('{')) return null;  // All lines must be objects
            if (!IsValidJson(trimmed)) return null;
            objects.Add(trimmed);
        }

        if (objects.Count < 2) return null;
        return $"[{string.Join(",", objects)}]";
    }

    /// <summary>
    /// Attempts to parse text as TSV (Tab-Separated Values).
    /// Requires tabs, at least 2 columns, and at least 2 data rows.
    /// </summary>
    public static string? TryParseAsTsv(string text)
    {
        if (!text.Contains('\t')) return null;
        return TryParseDelimited(text, '\t');
    }

    /// <summary>
    /// Attempts to parse text as CSV (Comma-Separated Values).
    /// Requires at least 2 columns, at least 2 data rows, and no tabs.
    /// </summary>
    public static string? TryParseAsCsv(string text)
    {
        // Skip if has tabs (prefer TSV detection)
        if (text.Contains('\t')) return null;
        return TryParseDelimited(text, ',');
    }

    /// <summary>
    /// Attempts to parse text as YAML.
    /// Requires either a YAML document marker (---) or at least 2 consecutive key: value lines.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "YAML deserialization required for structured data parsing")]
    public static string? TryParseAsYaml(string text)
    {
        var trimmed = text.TrimStart();

        // Check for YAML document marker or key: value pattern
        if (!trimmed.StartsWith("---\n") && !trimmed.StartsWith("---\r\n") && !LooksLikeYaml(trimmed))
            return null;

        try
        {
            var yaml = YamlDeserializer.Deserialize<object>(new StringReader(text));
            if (yaml == null) return null;
            // Convert YAML object to proper JSON with type preservation
            var converted = ConvertYamlToJsonTypes(yaml);
            return JsonSerializer.Serialize(converted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively converts YAML deserialized objects to proper JSON-compatible types.
    /// YamlDotNet deserializes to Dictionary{object,object} with string values - we need to
    /// convert numbers and booleans to their proper types.
    /// </summary>
    private static object? ConvertYamlToJsonTypes(object? value)
    {
        return value switch
        {
            null => null,
            Dictionary<object, object> dict => dict.ToDictionary(
                kvp => kvp.Key.ToString()!,
                kvp => ConvertYamlToJsonTypes(kvp.Value)),
            List<object> list => list.Select(ConvertYamlToJsonTypes).ToList(),
            string s => ParseYamlScalar(s),
            _ => value
        };
    }

    /// <summary>
    /// Parses a YAML scalar string into the appropriate type (bool, int, double, or string).
    /// </summary>
    private static object ParseYamlScalar(string s)
    {
        // Boolean
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        // Null
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("~", StringComparison.Ordinal)) return null!;

        // Integer
        if (long.TryParse(s, out var longVal)) return longVal;

        // Float
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var doubleVal)) return doubleVal;

        // String (default)
        return s;
    }

    /// <summary>
    /// Attempts to extract structured data embedded in prose.
    /// Looks for JSON brackets, YAML blocks (---), or CSV code blocks.
    /// </summary>
    public static string? TryParseEmbeddedStructuredData(string text)
    {
        // 1. Try embedded JSON (bracket matching)
        var json = ExtractByBracketMatching(text, '[', ']')
                ?? ExtractByBracketMatching(text, '{', '}');
        if (json != null) return json;

        // 2. Try embedded YAML (look for --- markers)
        var yamlMatch = EmbeddedYamlRegex().Match(text);
        if (yamlMatch.Success)
        {
            var yaml = TryParseAsYaml(yamlMatch.Groups[1].Value);
            if (yaml != null) return yaml;
        }

        // 3. Try embedded CSV/TSV (look for code blocks)
        var csvMatch = EmbeddedCsvRegex().Match(text);
        if (csvMatch.Success)
        {
            var content = csvMatch.Groups[1].Value;
            // Try TSV first, then CSV
            var parsed = TryParseAsTsv(content) ?? TryParseAsCsv(content);
            if (parsed != null) return parsed;
        }

        return null;
    }

    /// <summary>
    /// Tries to parse structured text with "- Key: Value" format separated by delimiters.
    /// Returns JSON array if successful, null otherwise.
    /// </summary>
    public static string? TryParseStructuredText(string text)
    {
        // Look for delimiter-separated sections (e.g., "----------")
        var sections = DelimiterRegex().Split(text);
        if (sections.Length < 2)
            return null;

        var items = new List<Dictionary<string, object?>>();

        foreach (var section in sections)
        {
            var trimmed = section.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Parse "- Key: Value" lines
            var item = new Dictionary<string, object?>();
            foreach (Match match in KeyValueRegex().Matches(trimmed))
            {
                var key = NormalizeKey(match.Groups[1].Value.Trim());
                var value = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    // Infer types for numbers and booleans
                    item[key] = ParseYamlScalar(value);
                }
            }

            if (item.Count > 0)
            {
                items.Add(item);
            }
        }

        if (items.Count == 0)
            return null;

        return JsonSerializer.Serialize(items);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Wraps a non-structured response as a JSON text object.
    /// </summary>
    public static string WrapAsText(string text)
    {
        var escaped = EscapeJsonString(text);
        return $"{{\"text\": \"{escaped}\"}}";
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

    /// <summary>
    /// Extracts a JSON structure by finding matching brackets.
    /// Handles nested structures and strings correctly.
    /// </summary>
    public static string? ExtractByBracketMatching(string text, char open, char close)
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
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
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
    /// Normalizes a key to a valid JSON/SQL identifier (lowercase, underscores).
    /// </summary>
    internal static string NormalizeKey(string key)
    {
        return key
            .Replace("Context7-compatible ", "")
            .Replace(" ", "_")
            .Replace("-", "_")
            .ToLowerInvariant();
    }

    internal static string EscapeJsonString(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Determines if text looks like YAML based on consecutive key: value lines.
    /// Requires at least 2 consecutive key-value pairs to avoid false positives on prose.
    /// </summary>
    private static bool LooksLikeYaml(string text)
    {
        var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        if (lines.Length < 2) return false;

        var keyValueCount = 0;
        foreach (var line in lines.Take(5))  // Check first 5 non-empty lines
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('-')) return false;  // List item = structured text format
            if (YamlKeyValueRegex().IsMatch(trimmed))
                keyValueCount++;
            else
                break;  // Non-matching line breaks the streak
        }

        return keyValueCount >= 2;  // Require at least 2 key-value pairs
    }

    /// <summary>
    /// Core parser for delimited text (CSV/TSV).
    /// Requires header + at least 2 data rows, and at least 2 columns.
    /// </summary>
    private static string? TryParseDelimited(string text, char delimiter)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3) return null;  // Need header + at least 2 data rows

        var header = ParseDelimitedLine(lines[0].Trim(), delimiter);
        if (header.Count < 2) return null;  // Need at least 2 columns

        var records = new List<Dictionary<string, object?>>();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var values = ParseDelimitedLine(line, delimiter);
            if (values.Count != header.Count) return null;  // Inconsistent columns = not tabular

            var record = new Dictionary<string, object?>();
            for (int j = 0; j < header.Count; j++)
            {
                // Infer types for numbers and booleans
                record[NormalizeKey(header[j])] = ParseYamlScalar(values[j]);
            }
            records.Add(record);
        }

        // Require at least 2 data rows to confirm tabular structure
        if (records.Count < 2) return null;
        return JsonSerializer.Serialize(records);
    }

    /// <summary>
    /// Parses a single delimited line, handling quoted fields (RFC 4180 compliant).
    /// </summary>
    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;  // Skip escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    #endregion

    #region JSON Unwrapping

    /// <summary>
    /// Unwraps a JSON envelope object to find the best array of objects for tabular consumption.
    /// Uses "largest array" heuristic: finds all arrays of objects by recursing through object
    /// properties (never into array elements), then picks the largest.
    /// </summary>
    /// <param name="json">Valid JSON string</param>
    /// <returns>The best array JSON, or the original JSON if no suitable array found</returns>
    public static string UnwrapJsonEnvelope(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Already an array — no unwrapping needed
            if (root.ValueKind == JsonValueKind.Array)
                return json;

            // Not an object — return as-is
            if (root.ValueKind != JsonValueKind.Object)
                return json;

            // Search for the best array of objects
            var candidates = new List<ArrayCandidate>();
            FindArrayCandidates(root, 0, candidates);

            if (candidates.Count == 0)
                return json;

            // Pick largest; break ties with deepest
            var best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Size > best.Size || (c.Size == best.Size && c.Depth > best.Depth))
                    best = c;
            }

            return best.Array.GetRawText();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private readonly record struct ArrayCandidate(JsonElement Array, int Size, int Depth);

    /// <summary>
    /// Recursively collects arrays of objects from JSON object properties.
    /// Only recurses through object fields — never into array elements.
    /// This prevents nested data (e.g. tags[] inside each user) from winning over the
    /// actual results array.
    /// </summary>
    private static void FindArrayCandidates(JsonElement element, int depth, List<ArrayCandidate> candidates)
    {
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;

            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
            {
                // Check if first element is an object (array of objects = table candidate)
                if (value[0].ValueKind == JsonValueKind.Object)
                    candidates.Add(new ArrayCandidate(value, value.GetArrayLength(), depth));
                // Do NOT recurse into array elements
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                FindArrayCandidates(value, depth + 1, candidates);
            }
        }
    }

    #endregion

    #region Regex Patterns

    [GeneratedRegex(@"\r?\n-{5,}\r?\n")]
    private static partial Regex DelimiterRegex();

    [GeneratedRegex(@"^-\s*([^:]+):\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*\s*:")]
    private static partial Regex YamlKeyValueRegex();

    [GeneratedRegex(@"---\s*\r?\n([\s\S]+?)\r?\n---", RegexOptions.Multiline)]
    private static partial Regex EmbeddedYamlRegex();

    [GeneratedRegex(@"```(?:csv|tsv)?\s*\r?\n([\s\S]+?)\r?\n```", RegexOptions.Multiline)]
    private static partial Regex EmbeddedCsvRegex();

    #endregion
}
