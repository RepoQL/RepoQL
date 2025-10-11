using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Formats.Mermaid.Rules;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Language;
using RepoQL.Grammar.Rules;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Formats.Mermaid;

public sealed class  MermaidAnalyzer : IFormatAnalyzer
{
    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "mermaid.doc", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!Supports(document.MediaType)) yield break;

        var language = new MermaidLanguage();
        var tree = document.SyntaxTree as ISyntaxTree ?? language.Parse(document.Text);
        if (document.SyntaxTree is null)
        {
            // ensure SyntaxTree stored if not already set
            tree = language.Parse(document.Text);
        }

        var ruleSet = new MermaidRuleSet();
        var ctx = new RuleContext
        {
            Language = language,
            Tree = tree,
            FilePath = document.Uri.AbsoluteUri,
            Cancel = cancellationToken
        };

        foreach (var rule in ruleSet.Rules)
        {
            foreach (var diagnostic in rule.Analyze(ctx))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                var ruleSettings = context.Settings.GetRule(diagnostic.Id);
                if (ruleSettings.Severity == AnalysisSeverity.None)
                    continue;
                var severity = CombineSeverity(ruleSettings.Severity, diagnostic.Severity);
                yield return ToResult(document.Uri.AbsoluteUri, diagnostic, severity);
            }
        }
        await Task.CompletedTask;
    }

    public IAsyncEnumerable<AnalysisResult> AnalyzeEmbeddedAsync(EmbeddedFragment fragment, AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        return AnalyzeEmbeddedCore(fragment, cancellationToken);

        async IAsyncEnumerable<AnalysisResult> AnalyzeEmbeddedCore(EmbeddedFragment frag, [EnumeratorCancellation] CancellationToken ct)
        {
            var language = new MermaidLanguage();
            var tree = language.Parse(frag.Text, new LanguageParseOptions { Tolerant = true });
            var ruleSet = new MermaidRuleSet();
            var ctx = new RuleContext
            {
                Language = language,
                Tree = tree,
                FilePath = frag.ParentUri.AbsoluteUri,
                Cancel = ct
            };

            foreach (var rule in ruleSet.Rules)
            {
                foreach (var diagnostic in rule.Analyze(ctx))
                {
                    if (ct.IsCancellationRequested)
                        yield break;
                    var ruleSettings = context.Settings.GetRule(diagnostic.Id);
                    if (ruleSettings.Severity == AnalysisSeverity.None)
                        continue;
                    var severity = CombineSeverity(ruleSettings.Severity, diagnostic.Severity);
                    yield return ToResult(frag.ParentUri.AbsoluteUri, diagnostic, severity);
                }
            }

            await Task.CompletedTask;
        }
    }

    private static AnalysisResult ToResult(string containerUri, Diagnostic diagnostic, AnalysisSeverity severity)
    {
        IReadOnlyList<AnalysisFix>? fixes = null;
        if (diagnostic.Fixes is { Count: > 0 })
        {
            var list = new List<AnalysisFix>(diagnostic.Fixes.Count);
            foreach (var fix in diagnostic.Fixes)
            {
                var replacements = fix.Edits.Select(edit => new AnalysisReplacement
                {
                    Region = new AnalysisRegion
                    {
                        StartChar = edit.Span.Start,
                        EndChar = edit.Span.End
                    },
                    NewText = edit.NewText
                }).ToList();
                list.Add(new AnalysisFix { Uri = containerUri, Description = fix.Title, Replacements = replacements });
            }
            fixes = list;
        }

        return new AnalysisResult
        {
            SemanticKey = $"{containerUri}#rule:{diagnostic.Id}",
            RuleId = diagnostic.Id,
            Source = "RepoQL.Mermaid",
            Kind = "lint",
            Severity = severity,
            Message = diagnostic.Message,
            Data = new JsonObject(),
            Target = new AnalysisTarget { TargetUri = containerUri },
            Fixes = fixes,
            AutoFixable = fixes is { Count: > 0 }
        };
    }

    private static AnalysisSeverity CombineSeverity(AnalysisSeverity configured, Severity diagnosticSeverity)
    {
        if (configured != AnalysisSeverity.Warning)
            return configured;

        return diagnosticSeverity switch
        {
            Severity.Info => AnalysisSeverity.Suggestion,
            Severity.Warning => AnalysisSeverity.Warning,
            Severity.Error => AnalysisSeverity.Error,
            _ => AnalysisSeverity.Warning
        };
    }
}
