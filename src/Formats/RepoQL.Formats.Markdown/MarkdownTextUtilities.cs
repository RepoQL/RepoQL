using System.Text;

namespace RepoQL.Formats.Markdown;

internal static class MarkdownTextUtilities
{
    public static string Slug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var prevHyphen = false;
        foreach (var ch in text.ToLowerInvariant())
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
        var s = sb.ToString().Trim('-');
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }
        return s;
    }
}
