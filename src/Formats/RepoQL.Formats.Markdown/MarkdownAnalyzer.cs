using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.Markdown;

public sealed class MarkdownAnalyzer : IFormatAnalyzer, IAnnotationSourceProvider
{
    private const string RuleId = "markdown/broken-link";
    private const string Source = "RepoQL.Markdown";

    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "markdown.doc", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        DocumentModel document,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (!Supports(document.MediaType)) yield break;

        var state = document.GetMetadataOrDefault<MarkdownDocumentState>(MarkdownLoader.StateMetadataKey);
        if (state is null) yield break;

        var ruleSettings = context.Settings.GetRule(RuleId);
        if (ruleSettings.Severity == AnalysisSeverity.None)
            yield break;

        // Check local anchors only
        var localSlugs = new HashSet<string>(
            state.Surface.Headings.Select(h => h.Slug),
            StringComparer.OrdinalIgnoreCase);

        foreach (var link in state.Surface.Links)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (link.IsImage)
                continue;

            var href = link.Href?.Trim();
            if (string.IsNullOrEmpty(href))
                continue;

            // Only check local anchor references (starts with #)
            if (!href.StartsWith('#'))
                continue;

            var slug = MarkdownTextUtilities.Slug(href.TrimStart('#'));
            if (!localSlugs.Contains(slug))
            {
                yield return BuildResult(document, link, ruleSettings.Severity, $"Anchor '#{slug}' not found");
            }
        }

        await Task.CompletedTask; // Keep async signature
    }

    private static AnalysisResult BuildResult(DocumentModel document, LinkInfo link, AnalysisSeverity severity, string message)
        => new()
        {
            SemanticKey = $"{document.Uri}#rule:{RuleId}@node:{link.NodeId}",
            RuleId = RuleId,
            Source = Source,
            Kind = "lint",
            Severity = severity,
            Message = message,
            Data = new JsonObject { ["href"] = link.Href },
            Target = new AnalysisTarget
            {
                NodeId = link.NodeId,
                SpanId = link.SpanId,
                TargetUri = document.Uri
            }
        };

    public IEnumerable<string> GetAnalyzerSources(DocumentModel document, AnalyzerContext context)
    {
        yield return Source;
    }
}
