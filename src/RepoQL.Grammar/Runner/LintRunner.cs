namespace RepoQL.Grammar;

public static class LintRunner
{
    public static IEnumerable<Diagnostic> LintFile(
        ILanguage lang,
        string text,
        string uri,
        IRuleSet rules,
        IEmbedding? embedding = null,
        CancellationToken cancel = default)
    {
        var diags = new List<Diagnostic>();

        if (embedding is null)
        {
            var tree = lang.Parse(text, new LanguageParseOptions { Tolerant = true });
            var ctx  = new RuleContext { Language = lang, Tree = tree, FilePath = uri, Cancel = cancel };
            diags.AddRange(new Linter(rules.Rules.ToArray()).Run(ctx));
        }
        else
        {
            foreach (var (language, span) in embedding.Find(text))
            {
                var slice = text.Substring(span.Start, span.Length);
                var tree  = language.Parse(slice, new LanguageParseOptions { Tolerant = true });
                var ctx   = new RuleContext { Language = language, Tree = tree, FilePath = uri, Cancel = cancel };
                var linter= new Linter(rules.Rules.ToArray());
                foreach (var d in linter.Run(ctx))
                {
                    var remapped = d with
                    {
                        Span = TextSpan.FromBounds(span.Start + d.Span.Start, span.Start + d.Span.End),
                        File = uri
                    };
                    diags.Add(remapped);
                }
            }
        }

        return diags;
    }
}

