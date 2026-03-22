using System.Runtime.CompilerServices;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Formats.Cpp.Analysis;
using RepoQL.Formats.Cpp.TreeSitter;

namespace RepoQL.Formats.Cpp;

/// <summary>
/// C/C++ descriptor analyzer used outside indexing pipelines.
///
/// Purpose: Surface macro interference and syntax diagnostics through the IFormatAnalyzer contract.
///
/// Complexity: Parse + detector orchestration with annotation-to-analysis projection.
/// </summary>
internal sealed class CppAnalyzer(
    CppTreeSitterClient treeSitterClient,
    MacroInterferenceDetector macroInterferenceDetector)
    : IFormatAnalyzer
{
    private readonly CppTreeSitterClient _treeSitterClient = treeSitterClient ?? throw new ArgumentNullException(nameof(treeSitterClient));
    private readonly MacroInterferenceDetector _macroInterferenceDetector = macroInterferenceDetector ?? throw new ArgumentNullException(nameof(macroInterferenceDetector));

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return CppMediaTypes.IsSupportedKind(mediaType.Kind);
    }

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        DocumentModel document,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (!Supports(document.MediaType))
        {
            yield break;
        }

        using var parse = _treeSitterClient.Parse(document.Text);
        var rootNode = parse.RootNode;
        if (!parse.GrammarAvailable || !parse.HasTree || rootNode is null || rootNode.Id == IntPtr.Zero)
        {
            yield break;
        }

        var findings = _macroInterferenceDetector.Detect(rootNode, document, Guid.NewGuid(), DateTimeOffset.UtcNow);
        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AnalysisResult
            {
                SemanticKey = $"{document.Uri}#{finding.RuleId}:{finding.Id:N}",
                RuleId = finding.RuleId ?? CppAnnotationRuleIds.SyntaxError,
                Source = finding.Source,
                Kind = finding.Kind,
                Severity = ConvertSeverity(finding.Severity),
                Message = finding.Message,
                Data = finding.Data,
                Target = new AnalysisTarget
                {
                    NodeId = finding.TargetNodeId,
                    SpanId = finding.TargetSpanId,
                    TargetUri = document.Uri
                }
            };
        }

        await Task.CompletedTask;
    }

    private static AnalysisSeverity ConvertSeverity(string? severity)
    {
        if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisSeverity.Error;
        }

        if (string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisSeverity.Warning;
        }

        return AnalysisSeverity.Suggestion;
    }
}
