using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for writing CSV text to a temporary file for use with read_csv_auto.
/// Returns the path to the temp file, enabling dynamic column detection.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b> Bridge between inline CSV text and DuckDB's read_csv_auto which
/// requires file paths. This enables the parse() macro to return dynamic columns based
/// on CSV header row.</para>
/// <para><b>Complexity:</b> Manages temp file lifecycle. Files are written to system temp
/// directory with unique names. Cleanup happens via periodic pruning of old files.</para>
/// </remarks>
[UdfClass]
public class WriteTempCsvUdf
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "repoql_parse");
    private static readonly object CleanupLock = new();
    private static DateTime _lastCleanup = DateTime.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FileMaxAge = TimeSpan.FromMinutes(10);

    static WriteTempCsvUdf()
    {
        // Ensure temp directory exists
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>
    /// Writes CSV text to a temporary file and returns the path.
    /// The path can be passed directly to read_csv_auto for dynamic column detection.
    /// </summary>
    /// <remarks>
    /// Temp files are automatically cleaned up after 10 minutes.
    /// Files use a hash-based name to enable caching of repeated parse() calls.
    /// </remarks>
    [ScalarUdf("_write_temp_csv", IsPure = true, Description = "Write CSV text to temp file, return path for read_csv_auto")]
    public string WriteTempCsv(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            // Return path to empty file for graceful handling
            var emptyPath = Path.Combine(TempDir, "empty.csv");
            if (!File.Exists(emptyPath))
                File.WriteAllText(emptyPath, "");
            return emptyPath;
        }

        // Periodic cleanup of old temp files
        CleanupOldFiles();

        // Use content hash for filename to enable caching
        var hash = ComputeHash(text);
        var fileName = $"parse_{hash}.csv";
        var filePath = Path.Combine(TempDir, fileName);

        // Write only if not already cached
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, text);
        }
        else
        {
            // Touch the file to update access time
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
        }

        return filePath;
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

            foreach (var file in Directory.GetFiles(TempDir, "parse_*.csv"))
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
