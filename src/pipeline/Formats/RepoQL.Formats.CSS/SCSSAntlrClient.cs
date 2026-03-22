using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace RepoQL.Formats.CSS;

/// <summary>
/// ANTLR client for parsing SCSS files using the SCSS grammar.
/// </summary>
public sealed class SCSSAntlrClient
{
    public CSSParseResult Parse(string text)
    {
        var inputStream = new AntlrInputStream(text);
        var lexer = new ScssLexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new ScssParser(tokenStream);

        // Suppress console error output
        lexer.RemoveErrorListeners();
        parser.RemoveErrorListeners();

        var tree = parser.stylesheet();
        var visitor = new SCSSVisitor();
        visitor.Visit(tree);

        return visitor.Result;
    }

    private sealed class SCSSVisitor : ScssParserBaseVisitor<object?>
    {
        public CSSParseResult Result { get; } = new();

        // Override default visitor to traverse all children
        protected override object? DefaultResult => null;

        protected override object? AggregateResult(object? aggregate, object? nextResult) => nextResult;

        public override object? VisitChildren(IRuleNode node)
        {
            // Visit all children to ensure we capture all constructs
            for (var i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child is IRuleNode ruleNode)
                    Visit(ruleNode);
            }
            return null;
        }

        public override object? VisitRuleset(ScssParser.RulesetContext context)
        {
            var selectorGroup = context.selectorGroup();
            var selector = selectorGroup?.GetText() ?? "";
            selector = NormalizeWhitespace(selector);

            Result.Rulesets.Add(new CSSRulesetInfo
            {
                Selector = selector,
                Span = GetSpan(context)
            });

            // Visit nested statements within the ruleset block
            return base.VisitRuleset(context);
        }

        public override object? VisitVariableDeclaration(ScssParser.VariableDeclarationContext context)
        {
            var name = context.variableName()?.GetText() ?? "";
            var value = context.variableValue()?.GetText() ?? "";

            // Clean up the variable name (remove $ prefix for headline)
            var displayName = name.TrimStart('$');

            Result.Variables.Add(new SCSSVariableInfo
            {
                Name = name,
                Value = NormalizeWhitespace(value),
                Span = GetSpan(context)
            });

            return base.VisitVariableDeclaration(context);
        }

        public override object? VisitMixinDeclaration(ScssParser.MixinDeclarationContext context)
        {
            var identifier = context.identifier();
            var name = identifier?.GetText() ?? "";

            Result.Mixins.Add(new SCSSMixinInfo
            {
                Name = name,
                Span = GetSpan(context)
            });

            return null; // Don't visit children
        }

        public override object? VisitIncludeDeclaration(ScssParser.IncludeDeclarationContext context)
        {
            var identifier = context.identifier();
            var functionCall = context.functionCall();
            var name = identifier?.GetText() ?? functionCall?.identifier()?.GetText() ?? "";

            Result.Includes.Add(new SCSSIncludeInfo
            {
                Name = name,
                Span = GetSpan(context)
            });

            return base.VisitIncludeDeclaration(context);
        }

        public override object? VisitExtendDeclaration(ScssParser.ExtendDeclarationContext context)
        {
            // Get the extended selector - these can return arrays, so take first or combine
            var extended = "";
            var classNames = context.className();
            var ids = context.id();
            var typeSelectors = context.typeSelector();

            if (classNames != null && classNames.Length > 0)
                extended = classNames[0].GetText();
            else if (ids != null && ids.Length > 0)
                extended = ids[0].GetText();
            else if (typeSelectors != null && typeSelectors.Length > 0)
                extended = typeSelectors[0].GetText();

            Result.Extends.Add(new SCSSExtendInfo
            {
                Extended = extended,
                Span = GetSpan(context)
            });

            return base.VisitExtendDeclaration(context);
        }

        public override object? VisitFunctionDeclaration(ScssParser.FunctionDeclarationContext context)
        {
            var identifier = context.identifier();
            var name = identifier?.GetText() ?? "";

            Result.Functions.Add(new SCSSFunctionInfo
            {
                Name = name,
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitMediaDeclaration(ScssParser.MediaDeclarationContext context)
        {
            var queryList = context.mediaQueryList()?.GetText() ?? "";
            queryList = NormalizeWhitespace(queryList);

            Result.MediaRules.Add(new CSSMediaInfo
            {
                Condition = queryList,
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitKeyframesDeclaration(ScssParser.KeyframesDeclarationContext context)
        {
            var name = context.identifier()?.GetText() ?? "";

            Result.Keyframes.Add(new CSSKeyframesInfo
            {
                Name = name,
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitFontFaceDeclaration(ScssParser.FontFaceDeclarationContext context)
        {
            Result.FontFaces.Add(new CSSFontFaceInfo
            {
                Span = GetSpan(context)
            });

            return null;
        }

        public override object? VisitImportDeclaration(ScssParser.ImportDeclarationContext context)
        {
            var importPath = context.importPath();
            var path = importPath?.String_()?.GetText() ?? importPath?.uri()?.GetText() ?? "";
            path = StripQuotes(path);

            Result.Imports.Add(new CSSImportInfo
            {
                Path = path,
                Span = GetSpan(context)
            });

            return base.VisitImportDeclaration(context);
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
            return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        }
    }
}
