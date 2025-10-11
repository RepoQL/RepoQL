using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Embedding;
using RepoQL.Grammar.Language;
using RepoQL.Grammar.Rules;

namespace RepoQL.Grammar.Runner;

public static class LintRunner
{
    public static IEnumerable<Diagnostic> LintFile(
        ILanguage lang,
        string text,
        string file,
        IRuleSet rules,
        IEmbedding? embedding = null,
        CancellationToken cancel = default)
    {
        var diags = new List<Diagnostic>();

        if (embedding is null)
        {
            var tree = lang.Parse(text, new LanguageParseOptions { Tolerant = true });
            var ctx  = new RuleContext { Language = lang, Tree = tree, FilePath = file, Cancel = cancel };
            diags.AddRange(new Linter(rules.Rules.ToArray()).Run(ctx));
        }
        else
        {
            foreach (var (language, span) in embedding.Find(text))
            {
                var slice = text.Substring(span.Start, span.Length);
                var tree  = language.Parse(slice, new LanguageParseOptions { Tolerant = true });
                var ctx   = new RuleContext { Language = language, Tree = tree, FilePath = file, Cancel = cancel };
                var linter= new Linter(rules.Rules.ToArray());
                diags.AddRange(linter.Run(ctx)
                    .Select(d => d with
                    {
                        Span = TextSpan.FromBounds(span.Start + d.Span.Start, span.Start + d.Span.End), 
                        File = file
                    }));
            }
        }

        return diags;
    }
}

