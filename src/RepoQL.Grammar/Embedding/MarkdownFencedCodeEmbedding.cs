using System.Text.RegularExpressions;
using RepoQL.Grammar.Core;

namespace RepoQL.Grammar.Embedding;

/// <summary>
/// Finds triple-backtick fenced code blocks and resolves their language.
/// Example: ```cs ... ``` → returns span of the inner code and associated language.
/// </summary>
public sealed class MarkdownFencedCodeEmbedding(ILanguageResolver resolver) : IEmbedding
{
    private static readonly Regex Fence = new(
        pattern: @"(?ms)^(?<fence>```+)\s*(?<lang>[A-Za-z0-9_+\.-]*)[^\n]*\n(?<code>.*?)(?:\n\k<fence>\s*$)",
        options: RegexOptions.Compiled);

    private readonly ILanguageResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public IEnumerable<EmbeddingRegion> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Fence.Matches(text))
        {
            var lang = m.Groups["lang"].Value;
            if (string.IsNullOrWhiteSpace(lang)) continue;
            var language = _resolver.Resolve(lang);
            if (language is null) continue;
            var code = m.Groups["code"];
            yield return new EmbeddingRegion(language, new TextSpan(code.Index, code.Length));
        }
    }
}

