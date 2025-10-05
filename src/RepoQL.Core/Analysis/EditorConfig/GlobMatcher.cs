using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoQL.Core.Analysis.EditorConfig;

internal static class GlobMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string text, string pattern)
    {
        text = text.Replace('\\', '/');
        var regex = Cache.GetOrAdd(pattern, CreateRegex);
        return regex.IsMatch(text);
    }

    private static Regex CreateRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var regexPattern = ConvertToRegex(normalized);
        return new Regex("^" + regexPattern + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static string ConvertToRegex(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                {
                    var isDouble = (i + 1) < pattern.Length && pattern[i + 1] == '*';
                    if (isDouble)
                    {
                        sb.Append(".*");
                        i += 2;
                        if (i < pattern.Length && pattern[i] == '/')
                            i++;
                        continue;
                    }
                    sb.Append("[^/]*");
                    i++;
                    continue;
                }
                case '?':
                    sb.Append("[^/]");
                    i++;
                    continue;
            }

            if ("+()^$.{}[]|\\".IndexOf(c) >= 0)
                sb.Append('\\');

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
