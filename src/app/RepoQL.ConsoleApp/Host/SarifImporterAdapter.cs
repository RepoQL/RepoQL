using System.Text;
using RepoQL.Contracts;
using RepoQL.Import;
using SarifImportResult = RepoQL.Import.SarifImportResult;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Adapt the host-level SARIF import service to the <see cref="ISarifImporter"/> interface.
/// Complexity: Resolves the file path from the URI (relative → absolute, platform normalization)
/// and delegates to <see cref="ISarifImportService"/>.
/// </summary>
internal sealed class SarifImporterAdapter(
    RepoQL.Sarif.ISarifImportService sarifService,
    RepositoryConfiguration repoConfig) : ISarifImporter
{
    public async Task<SarifImportResult> ImportAsync(string filePath, CancellationToken cancel)
    {
        var resolved = ResolveSarifFilePath(filePath);
        var result = await sarifService.ImportAsync(resolved, cancel).ConfigureAwait(false);
        return new SarifImportResult
        {
            RulesImported = result.Sources.Sum(s => s.Total),
            AnnotationsCreated = result.Sources.Sum(s => s.New),
            AnnotationsRemoved = result.Sources.Sum(s => s.Expired),
            Message = FormatSarifImportMessage(result)
        };
    }

    private string ResolveSarifFilePath(string path)
    {
        var decodedPath = Uri.UnescapeDataString(path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decodedPath))
            throw new InvalidOperationException("sarif:// URI must include a file path.");

        var normalized = decodedPath.Replace('\\', '/');
        if (normalized.StartsWith("/./", StringComparison.Ordinal))
            normalized = normalized[3..];
        else if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        // file:///C:/... style path embedded in sarif URI absolute path.
        if (normalized.Length >= 3 && normalized[0] == '/' && char.IsLetter(normalized[1]) && normalized[2] == ':')
            normalized = normalized[1..];

        var candidate = normalized.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(candidate))
            return Path.GetFullPath(candidate);

        return Path.GetFullPath(Path.Combine(repoConfig.Path, candidate));
    }

    private static string FormatSarifImportMessage(RepoQL.Sarif.Models.SarifImportResult result)
    {
        var sb = new StringBuilder();
        if (result.Sources.Count == 1)
            sb.AppendLine($"Imported {result.TotalFindings} findings from {result.Sources[0].Source}");
        else
            sb.AppendLine($"Imported {result.TotalFindings} findings from {result.Sources.Count} sources");

        foreach (var source in result.Sources.OrderBy(s => s.Source, StringComparer.Ordinal))
        {
            sb.AppendLine($"{source.Source}: {source.Total} findings");
            sb.AppendLine($"  {source.Resolved} resolved to indexed files, {source.Unresolved} unresolved");
            sb.AppendLine($"  {source.New} new, {source.Updated} updated, {source.Unchanged} unchanged, {source.Expired} expired");
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
                sb.AppendLine($"- {warning}");
        }

        return sb.ToString().TrimEnd();
    }
}
