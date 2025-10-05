using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Minimal analyzer for *.csproj: flags unpinned packages (no Version or floating).
/// </summary>
public sealed class CsProjAnalyzer : IFormatAnalyzer
{
    private const string RuleId = "csproj/unpinned-package";
    private const string Source = "RepoQL.CsProj";

    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "dotnet.csproj", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Supports(document.MediaType)) yield break;
        var state = document.GetMetadata<CsProjState>(CsProjLoader.StateKey);
        if (state is null) yield break;

        // Basic rule: PackageReference without Version or with floating (contains '*') → warning
        foreach (var pkg in state.Packages)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            var version = pkg.Version?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(version) || version.Contains('*'))
            {
                var sev = context.Settings.GetRule(RuleId).Severity;
                if (sev == AnalysisSeverity.None) continue;
                yield return new AnalysisResult
                {
                    SemanticKey = $"{document.Uri}#rule:{RuleId}@pkg:{pkg.Id}",
                    RuleId = RuleId,
                    Source = Source,
                    Kind = "lint",
                    Severity = sev,
                    Message = string.IsNullOrWhiteSpace(version)
                        ? $"PackageReference '{pkg.Id}' is not pinned to a version."
                        : $"PackageReference '{pkg.Id}' uses a floating version '{version}'.",
                    Data = new JsonObject { ["id"] = pkg.Id, ["version"] = string.IsNullOrWhiteSpace(version) ? null : version },
                    Target = new AnalysisTarget
                    {
                        TargetUri = document.Uri.AbsoluteUri,
                        // Best-effort: line anchor
                        SpanId = null
                    }
                };
            }
        }
        await Task.CompletedTask;
    }
}
