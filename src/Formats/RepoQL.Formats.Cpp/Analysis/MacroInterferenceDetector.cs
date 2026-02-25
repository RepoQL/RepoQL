using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using TsNode = TreeSitter.Node;

namespace RepoQL.Formats.Cpp.Analysis;

/// <summary>
/// Classifies tree-sitter ERROR/MISSING nodes into C/C++ parser interference categories.
///
/// Purpose: Emit actionable lint annotations when macros and preprocessor boundaries hide structure.
///
/// Complexity: Single-pass CST traversal with lexical context heuristics and macro family scoring.
/// </summary>
public sealed partial class MacroInterferenceDetector
{
    // GeneratedRegex declarations are at the bottom of the class.

    private static readonly MacroFamily[] KnownMacroFamilies =
    [
        new("Qt", new Regex(@"^(Q_OBJECT|Q_PROPERTY|Q_SIGNAL|Q_SLOT|Q_EMIT|Q_INVOKABLE)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Windows SDK", new Regex(@"^(__declspec|EXPORT_API|DLLEXPORT|STDMETHODCALLTYPE)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("Google Test", new Regex(@"^(TEST|TEST_F|TEST_P|EXPECT_[A-Z0-9_]+|ASSERT_[A-Z0-9_]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Catch2", new Regex(@"^(TEST_CASE|SECTION|BENCHMARK)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Boost", new Regex(@"^(BOOST_AUTO_TEST_CASE|BOOST_FIXTURE_TEST_CASE)$", RegexOptions.Compiled | RegexOptions.CultureInvariant))
    ];

    public IReadOnlyList<Annotation> Detect(
        TsNode root,
        DocumentModel document,
        Guid scopeDocumentId,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (IsNullNode(root))
        {
            return [];
        }

        var now = createdAt ?? DateTimeOffset.UtcNow;
        var annotations = new List<Annotation>();
        var ancestors = new Stack<string>();
        var source = document.Text;
        var preprocessorStats = new PreprocessorStats(
            source.Contains("extern \"C\"", StringComparison.Ordinal),
            PreprocIfDirectiveRegex().Matches(source).Count,
            PreprocEndifRegex().Matches(source).Count);

        Visit(root);
        if (annotations.Count == 0 && root.HasError)
        {
            EmitFallback(root, source);
        }

        return annotations;

        void Visit(TsNode node)
        {
            if (IsNullNode(node))
            {
                return;
            }

            var isError = node.IsError || string.Equals(node.Type, "ERROR", StringComparison.Ordinal);
            var isMissing = node.IsMissing;
            if (isError || isMissing)
            {
                var start = Math.Clamp(node.StartIndex, 0, source.Length);
                var end = Math.Clamp(node.EndIndex, start, source.Length);
                var preview = ExtractWindow(source, start, end, 96);
                var previousIdentifier = ExtractPreviousIdentifier(source, start);
                var macroFromWindow = DetectKnownMacroInWindow(preview);

                if (string.IsNullOrWhiteSpace(previousIdentifier))
                {
                    previousIdentifier = macroFromWindow;
                }
                else if (!TryMatchKnownMacroFamily(previousIdentifier, out _) && !IsAllCapsIdentifier(previousIdentifier))
                {
                    previousIdentifier = macroFromWindow ?? previousIdentifier;
                }

                var classification = ClassifyNode(
                    isError,
                    isMissing,
                    previousIdentifier,
                    ancestors,
                    source,
                    start,
                    end,
                    preview,
                    preprocessorStats);

                var mapped = document.LineMap.GetSpan(start, end);
                annotations.Add(new Annotation
                {
                    Kind = "lint",
                    Severity = classification.Severity,
                    Source = CppValues.AnalyzerAnnotationSource,
                    RuleId = classification.RuleId,
                    Message = BuildMessage(classification, previousIdentifier),
                    ScopeDocumentId = scopeDocumentId,
                    CreatedAt = now,
                    Data = new JsonObject
                    {
                        [CppPropertyKeys.MacroName] = previousIdentifier ?? string.Empty,
                        [CppPropertyKeys.Context] = classification.Context,
                        [CppPropertyKeys.Confidence] = classification.Confidence,
                        [CppPropertyKeys.StartLine] = mapped.StartLine,
                        [CppPropertyKeys.EndLine] = mapped.EndLine
                    }
                });
            }

            ancestors.Push(node.Type);
            foreach (var child in EnumerateChildren(node))
            {
                Visit(child);
            }
            _ = ancestors.Pop();
        }

        void EmitFallback(TsNode rootNode, string sourceText)
        {
            var preview = sourceText.Length > 4096 ? sourceText[..4096] : sourceText;
            var macroCandidate = DetectKnownMacroInWindow(preview);
            var classification = ClassifyFallback(sourceText, macroCandidate, preprocessorStats, preview);
            var mapped = document.LineMap.GetSpan(0, Math.Max(0, sourceText.Length));
            annotations.Add(new Annotation
            {
                Kind = "lint",
                Severity = classification.Severity,
                Source = CppValues.AnalyzerAnnotationSource,
                RuleId = classification.RuleId,
                Message = BuildMessage(classification, macroCandidate),
                ScopeDocumentId = scopeDocumentId,
                CreatedAt = now,
                Data = new JsonObject
                {
                    [CppPropertyKeys.MacroName] = macroCandidate ?? string.Empty,
                    [CppPropertyKeys.Context] = classification.Context,
                    [CppPropertyKeys.Confidence] = classification.Confidence,
                    [CppPropertyKeys.StartLine] = mapped.StartLine,
                    [CppPropertyKeys.EndLine] = mapped.EndLine
                }
            });
        }
    }

    private static Classification ClassifyNode(
        bool isError,
        bool isMissing,
        string? previousIdentifier,
        IReadOnlyCollection<string> ancestors,
        string source,
        int start,
        int end,
        string preview,
        PreprocessorStats preprocessorStats)
    {
        if ((isMissing || isError) && IsPreprocessorBoundaryContext(source, start, end, preview, preprocessorStats))
        {
            return new Classification(
                CppAnnotationRuleIds.PreprocessorBoundary,
                "info",
                "missing_endif_near_extern_c",
                "high");
        }

        if (isError)
        {
            if (!string.IsNullOrWhiteSpace(previousIdentifier)
                && TryMatchKnownMacroFamily(previousIdentifier, out _))
            {
                return new Classification(
                    CppAnnotationRuleIds.MacroInterference,
                    "info",
                    "known_macro_family",
                    "very_high");
            }

            if (!string.IsNullOrWhiteSpace(previousIdentifier)
                && IsAllCapsIdentifier(previousIdentifier)
                && IsInsideClassContext(ancestors))
            {
                return new Classification(
                    CppAnnotationRuleIds.MacroInterference,
                    "info",
                    "class_body_macro",
                    "high");
            }

            if (!string.IsNullOrWhiteSpace(previousIdentifier)
                && IsAllCapsIdentifier(previousIdentifier)
                && IsBeforeClassDeclaration(source, end))
            {
                return new Classification(
                    CppAnnotationRuleIds.MacroInterference,
                    "info",
                    "visibility_macro_before_class",
                    "high");
            }

            if (IsTemplateContext(ancestors, source, start, end))
            {
                return new Classification(
                    CppAnnotationRuleIds.TemplateComplexity,
                    "warning",
                    "template_context_error",
                    "medium");
            }

            return new Classification(
                CppAnnotationRuleIds.SyntaxError,
                "warning",
                "generic_error_node",
                "low");
        }

        if (isMissing)
        {
            if (!string.IsNullOrWhiteSpace(previousIdentifier) && TryMatchKnownMacroFamily(previousIdentifier, out _))
            {
                return new Classification(
                    CppAnnotationRuleIds.MacroInterference,
                    "info",
                    "known_macro_family",
                    "high");
            }

            if (!string.IsNullOrWhiteSpace(previousIdentifier)
                && IsAllCapsIdentifier(previousIdentifier)
                && (IsInsideClassContext(ancestors) || IsBeforeClassDeclaration(source, end)))
            {
                return new Classification(
                    CppAnnotationRuleIds.MacroInterference,
                    "info",
                    "missing_after_macro",
                    "high");
            }

            return new Classification(
                CppAnnotationRuleIds.SyntaxError,
                "warning",
                "missing_expected_token",
                "medium");
        }

        return new Classification(
            CppAnnotationRuleIds.SyntaxError,
            "warning",
            "unknown_parse_issue",
            "low");
    }

    private static Classification ClassifyFallback(
        string source,
        string? macroCandidate,
        PreprocessorStats preprocessorStats,
        string preview)
    {
        if (IsPreprocessorBoundaryContext(source, 0, preview.Length, preview, preprocessorStats))
        {
            return new Classification(
                CppAnnotationRuleIds.PreprocessorBoundary,
                "info",
                "missing_endif_near_extern_c",
                "high");
        }

        if (!string.IsNullOrWhiteSpace(macroCandidate) && TryMatchKnownMacroFamily(macroCandidate, out _))
        {
            return new Classification(
                CppAnnotationRuleIds.MacroInterference,
                "info",
                "known_macro_family",
                "high");
        }

        return new Classification(
            CppAnnotationRuleIds.SyntaxError,
            "warning",
            "generic_error_node",
            "low");
    }

    private static string BuildMessage(Classification classification, string? macroName)
    {
        if (string.Equals(classification.RuleId, CppAnnotationRuleIds.MacroInterference, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(macroName)
                ? "Macro interference may hide structure from the parser."
                : $"Macro interference detected near '{macroName}'. Hidden members are possible.";
        }

        if (string.Equals(classification.RuleId, CppAnnotationRuleIds.PreprocessorBoundary, StringComparison.Ordinal))
        {
            return "Preprocessor boundary appears incomplete near extern \"C\" (possible missing #endif).";
        }

        if (string.Equals(classification.RuleId, CppAnnotationRuleIds.TemplateComplexity, StringComparison.Ordinal))
        {
            return "Parser error occurred in template context. Template complexity likely exceeded heuristic support.";
        }

        if (string.Equals(classification.Context, "missing_expected_token", StringComparison.Ordinal))
        {
            return "Missing expected token while parsing C/C++ declaration.";
        }

        return "Syntax error encountered while parsing C/C++ source.";
    }

    private static bool TryMatchKnownMacroFamily(string candidate, out string familyName)
    {
        foreach (var family in KnownMacroFamilies)
        {
            if (family.Pattern.IsMatch(candidate))
            {
                familyName = family.Name;
                return true;
            }
        }

        familyName = string.Empty;
        return false;
    }

    private static string? DetectKnownMacroInWindow(string window)
    {
        if (string.IsNullOrWhiteSpace(window))
        {
            return null;
        }

        foreach (var token in IdentifierTokenRegex().Matches(window)
                     .Select(m => m.Value))
        {
            if (TryMatchKnownMacroFamily(token, out _))
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsInsideClassContext(IReadOnlyCollection<string> ancestors)
    {
        return ancestors.Any(t =>
            string.Equals(t, "class_specifier", StringComparison.Ordinal)
            || string.Equals(t, "struct_specifier", StringComparison.Ordinal)
            || string.Equals(t, "field_declaration_list", StringComparison.Ordinal));
    }

    private static bool IsBeforeClassDeclaration(string source, int end)
    {
        var ahead = ExtractForwardWindow(source, end, 96);
        return ClassOrStructKeywordRegex().IsMatch(ahead);
    }

    private static bool IsTemplateContext(IReadOnlyCollection<string> ancestors, string source, int start, int end)
    {
        if (ancestors.Any(t => t.Contains("template", StringComparison.Ordinal)))
        {
            return true;
        }

        var around = ExtractWindow(source, start, end, 80);
        return around.Contains('<') && around.Contains('>');
    }

    private static bool IsPreprocessorBoundaryContext(
        string source,
        int start,
        int end,
        string preview,
        PreprocessorStats preprocessorStats)
    {
        if (!preprocessorStats.HasExternC || !preprocessorStats.HasAnyPreprocIf)
        {
            return false;
        }

        if (preprocessorStats.IfDirectiveCount <= preprocessorStats.EndifCount)
        {
            return false;
        }

        var window = ExtractWindow(source, start, end, 256);
        var hasExternC = preview.Contains("extern \"C\"", StringComparison.Ordinal)
                         || window.Contains("extern \"C\"", StringComparison.Ordinal);
        if (!hasExternC)
        {
            return false;
        }

        return PreprocIfDirectiveRegex().IsMatch(window)
               || PreprocIfDirectiveRegex().IsMatch(preview);
    }

    private static bool IsAllCapsIdentifier(string value)
        => AllCapsIdentifierRegex().IsMatch(value);

    private static string? ExtractPreviousIdentifier(string source, int start)
    {
        if (string.IsNullOrEmpty(source) || start <= 0)
        {
            return null;
        }

        var cursor = Math.Clamp(start - 1, 0, source.Length - 1);
        while (cursor >= 0 && char.IsWhiteSpace(source[cursor]))
        {
            cursor--;
        }

        if (cursor < 0)
        {
            return null;
        }

        var begin = Math.Max(0, cursor - 80);
        var slice = source.Substring(begin, cursor - begin + 1);
        var match = IdentifierTailRegex().Match(slice);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string ExtractWindow(string source, int start, int end, int padding)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var from = Math.Max(0, start - padding);
        var to = Math.Min(source.Length, end + padding);
        return source[from..to];
    }

    private static string ExtractForwardWindow(string source, int start, int length)
    {
        if (string.IsNullOrEmpty(source) || start >= source.Length)
        {
            return string.Empty;
        }

        var safeStart = Math.Max(0, start);
        var safeEnd = Math.Min(source.Length, safeStart + Math.Max(0, length));
        return source[safeStart..safeEnd];
    }

    private static bool IsNullNode(TsNode node)
        => node.Id == IntPtr.Zero;

    private static IEnumerable<TsNode> EnumerateChildren(TsNode node)
    {
        foreach (var child in node.Children)
        {
            if (!IsNullNode(child))
            {
                yield return child;
            }
        }
    }

    private readonly record struct Classification(
        string RuleId,
        string Severity,
        string Context,
        string Confidence);

    private readonly record struct PreprocessorStats(
        bool HasExternC,
        int IfDirectiveCount,
        int EndifCount)
    {
        public bool HasAnyPreprocIf => IfDirectiveCount > 0;
    }

    private readonly record struct MacroFamily(string Name, Regex Pattern);

    // ── Source-generated regex declarations ──────────────────────────────

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllCapsIdentifierRegex();

    [GeneratedRegex(@"(?<id>[A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierTailRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierTokenRegex();

    [GeneratedRegex(@"\b(class|struct)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClassOrStructKeywordRegex();

    [GeneratedRegex(@"#\s*if(n?def)?", RegexOptions.CultureInvariant)]
    private static partial Regex PreprocIfDirectiveRegex();

    [GeneratedRegex(@"#\s*endif", RegexOptions.CultureInvariant)]
    private static partial Regex PreprocEndifRegex();
}
