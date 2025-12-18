using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace RepoQL.Formats.CSS;

/// <summary>
/// ANTLR client for parsing CSS and LESS files using the CSS3 grammar.
/// </summary>
public sealed class CSSAntlrClient
{
    public CSSParseResult Parse(string text)
    {
        var inputStream = new AntlrInputStream(text);
        var lexer = new css3Lexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new css3Parser(tokenStream);

        // Suppress console error output
        lexer.RemoveErrorListeners();
        parser.RemoveErrorListeners();

        var tree = parser.stylesheet();
        var visitor = new CSSVisitor();
        visitor.Visit(tree);

        return visitor.Result;
    }

    private sealed class CSSVisitor : css3ParserBaseVisitor<object?>
    {
        public CSSParseResult Result { get; } = new();

        public override object? VisitKnownRuleset(css3Parser.KnownRulesetContext context)
        {
            // Get selector text from known ruleset (has selectorGroup)
            var selectorGroup = context.selectorGroup();
            var selector = selectorGroup?.GetText() ?? "";

            // Clean up selector (remove extra whitespace)
            selector = NormalizeWhitespace(selector);

            Result.Rulesets.Add(new CSSRulesetInfo
            {
                Selector = selector,
                Span = GetSpan(context)
            });

            return base.VisitKnownRuleset(context);
        }

        public override object? VisitUnknownRuleset(css3Parser.UnknownRulesetContext context)
        {
            // Unknown ruleset - try to get text before the brace
            var selector = "";
            for (var i = 0; i < context.ChildCount; i++)
            {
                var child = context.GetChild(i);
                if (child.GetText() == "{") break;
                selector += child.GetText();
            }

            selector = NormalizeWhitespace(selector);
            if (!string.IsNullOrWhiteSpace(selector))
            {
                Result.Rulesets.Add(new CSSRulesetInfo
                {
                    Selector = selector,
                    Span = GetSpan(context)
                });
            }

            return base.VisitUnknownRuleset(context);
        }

        public override object? VisitMedia(css3Parser.MediaContext context)
        {
            var queryList = context.mediaQueryList()?.GetText() ?? "";
            queryList = NormalizeWhitespace(queryList);

            Result.MediaRules.Add(new CSSMediaInfo
            {
                Condition = queryList,
                Span = GetSpan(context)
            });

            // Don't visit children - we already captured the media rule
            return null;
        }

        public override object? VisitKeyframesRule(css3Parser.KeyframesRuleContext context)
        {
            var name = context.ident()?.GetText() ?? "";

            Result.Keyframes.Add(new CSSKeyframesInfo
            {
                Name = name,
                Span = GetSpan(context)
            });

            return null; // Don't visit children
        }

        public override object? VisitFontFaceRule(css3Parser.FontFaceRuleContext context)
        {
            Result.FontFaces.Add(new CSSFontFaceInfo
            {
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitSupportsRule(css3Parser.SupportsRuleContext context)
        {
            var condition = context.supportsCondition()?.GetText() ?? "";
            condition = NormalizeWhitespace(condition);

            Result.SupportsRules.Add(new CSSSupportsInfo
            {
                Condition = condition,
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitGoodImport(css3Parser.GoodImportContext context)
        {
            var path = context.String_()?.GetText() ?? context.url()?.GetText() ?? "";
            path = StripQuotes(path);

            Result.Imports.Add(new CSSImportInfo
            {
                Path = path,
                Span = GetSpan(context)
            });

            return base.VisitGoodImport(context);
        }

        public override object? VisitGoodCharset(css3Parser.GoodCharsetContext context)
        {
            var charset = StripQuotes(context.String_()?.GetText() ?? "");

            Result.Charsets.Add(new CSSCharsetInfo
            {
                Charset = charset,
                Span = GetSpan(context)
            });

            return base.VisitGoodCharset(context);
        }

        public override object? VisitGoodNamespace(css3Parser.GoodNamespaceContext context)
        {
            var prefix = context.namespacePrefix()?.GetText() ?? "";
            var uri = context.String_()?.GetText() ?? context.url()?.GetText() ?? "";
            uri = StripQuotes(uri);

            Result.Namespaces.Add(new CSSNamespaceInfo
            {
                Prefix = prefix,
                Uri = uri,
                Span = GetSpan(context)
            });

            return base.VisitGoodNamespace(context);
        }

        public override object? VisitPage(css3Parser.PageContext context)
        {
            var pseudoPage = context.pseudoPage()?.GetText() ?? "";

            Result.Pages.Add(new CSSPageInfo
            {
                PseudoPage = pseudoPage,
                Span = GetSpan(context)
            });

            return null;
        }

        private static CSSSpan GetSpan(ParserRuleContext context)
        {
            var start = context.Start;
            var stop = context.Stop ?? start;
            return new CSSSpan(start.StartIndex, stop.StopIndex + 1);
        }

        private static string StripQuotes(string value)
        {
            if (value.Length >= 2)
            {
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                {
                    return value[1..^1];
                }
            }
            return value;
        }

        private static string NormalizeWhitespace(string text)
        {
            // Replace multiple whitespace with single space
            return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        }
    }
}
