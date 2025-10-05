namespace RepoQL.Core.Analysis.Markdown;

internal static class MarkdownSlug
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        var prevHyphen = false;
        foreach (var ch in trimmed.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevHyphen = false;
            }
            else if ((char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '.') && !prevHyphen)
            {
                sb.Append('-');
                prevHyphen = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug;
    }
}
