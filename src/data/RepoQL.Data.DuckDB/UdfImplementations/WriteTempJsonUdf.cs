using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for writing JSON text to a temporary file for use with read_json_auto.
/// Returns the path to the temp file, enabling dynamic column detection.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b> Bridge between inline JSON/JSONL text and DuckDB's read_json_auto which
/// requires file paths. This enables MCP macros to return dynamic columns based on JSON structure.
/// Also handles conversion of JSON arrays to JSONL format for read_json_auto compatibility.</para>
/// <para><b>Complexity:</b> Manages temp file lifecycle. Files are written to system temp
/// directory with unique names. Cleanup happens via periodic pruning of old files.</para>
/// </remarks>
[UdfClass]
public class WriteTempJsonUdf
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "repoql_parse");
    private static readonly object CleanupLock = new();
    private static DateTime _lastCleanup = DateTime.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FileMaxAge = TimeSpan.FromMinutes(10);

    static WriteTempJsonUdf()
    {
        // Ensure temp directory exists
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>
    /// Writes JSON text to a temporary file and returns the path.
    /// The path can be passed directly to read_json_auto for dynamic column detection.
    /// Converts JSON arrays to JSONL format for better read_json_auto compatibility.
    /// </summary>
    /// <remarks>
    /// Temp files are automatically cleaned up after 10 minutes.
    /// Files use a hash-based name to enable caching of repeated calls.
    /// </remarks>
    [ScalarUdf("_write_temp_json", IsPure = true, Description = "Write JSON text to temp file, return path for read_json_auto")]
    public string WriteTempJson(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            // Return path to empty file for graceful handling
            var emptyPath = Path.Combine(TempDir, "empty.json");
            if (!File.Exists(emptyPath))
                File.WriteAllText(emptyPath, "[]");
            return emptyPath;
        }

        // Periodic cleanup of old temp files
        CleanupOldFiles();

        // Normalize the JSON for read_json_auto compatibility
        var normalizedText = NormalizeForJsonAuto(text);

        // Use content hash for filename to enable caching
        var hash = ComputeHash(normalizedText);
        var fileName = $"mcp_{hash}.json";
        var filePath = Path.Combine(TempDir, fileName);

        // Write only if not already cached
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, normalizedText);
        }
        else
        {
            // Touch the file to update access time
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
        }

        return filePath;
    }

    /// <summary>
    /// Normalizes JSON text for read_json_auto compatibility.
    /// - JSON arrays are converted to JSONL (one object per line)
    /// - Single objects are wrapped in array brackets
    /// - JSONL is passed through as-is
    /// </summary>
    private static string NormalizeForJsonAuto(string text)
    {
        var trimmed = text.Trim();

        // Already JSONL (multiple lines starting with {)?
        if (trimmed.Contains('\n') && !trimmed.StartsWith('['))
        {
            var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.All(l => l.TrimStart().StartsWith('{')))
            {
                // Already JSONL format - pass through
                return trimmed;
            }
        }

        // JSON array? Convert to JSONL for better column detection
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        lines.Add(element.GetRawText());
                    }
                    return string.Join("\n", lines);
                }
            }
            catch
            {
                // If parsing fails, return as-is
            }
        }

        // Single object? Keep as-is - read_json_auto handles single objects
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        // Unknown format - return as-is and let read_json_auto handle it
        return text;
    }

    private static string ComputeHash(string text)
    {
        // Simple hash for filename - doesn't need to be cryptographic
        var hash = text.GetHashCode();
        return hash.ToString("x8");
    }

    private static void CleanupOldFiles()
    {
        var now = DateTime.UtcNow;

        // Check if cleanup is needed (non-blocking check)
        if (now - _lastCleanup < CleanupInterval)
            return;

        // Try to acquire cleanup lock (non-blocking)
        if (!Monitor.TryEnter(CleanupLock))
            return;

        try
        {
            // Double-check after acquiring lock
            if (now - _lastCleanup < CleanupInterval)
                return;

            _lastCleanup = now;

            if (!Directory.Exists(TempDir))
                return;

            foreach (var file in Directory.GetFiles(TempDir, "mcp_*.json"))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (now - lastWrite > FileMaxAge)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore errors during cleanup - file may be in use
                }
            }
        }
        finally
        {
            Monitor.Exit(CleanupLock);
        }
    }
}
