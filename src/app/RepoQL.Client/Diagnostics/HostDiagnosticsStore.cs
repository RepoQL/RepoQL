using System.Text.Json;
using RepoQL.Contracts;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Persist and retrieve host-side diagnostic reports for later inspection.
/// Complexity: Centralizes JSON storage in the .repoql diagnostics directory without throwing.
/// </summary>
public static class HostDiagnosticsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static bool TryWriteReport<T>(string repoRoot, string fileName, T report)
    {
        try
        {
            var path = GetReportPath(repoRoot, fileName);
            var json = JsonSerializer.Serialize(report, SerializerOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadReport<T>(string repoRoot, string fileName, out T? report)
    {
        report = default;
        try
        {
            var path = GetReportPath(repoRoot, fileName);
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            report = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            return report is not null;
        }
        catch
        {
            report = default;
            return false;
        }
    }

    private static string GetReportPath(string repoRoot, string fileName)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repoRoot);
        var diagnosticsDir = Path.Combine(repoqlDir, "diagnostics");
        Directory.CreateDirectory(diagnosticsDir);
        return Path.Combine(diagnosticsDir, fileName);
    }
}
