using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoQL.Core;

/// <summary>
/// Provides Git-style glob matching for RepoQL URIs. Normalizes inputs, infers
/// default schemes, and caches compiled regular expressions for reuse.
/// </summary>
internal static class RepoUriGlobMatcher
{
    private const string DefaultScheme = "file:///";

    private static readonly ConcurrentDictionary<GlobCacheKey, Regex> RegexCache = new();

    /// <summary>
    /// Returns true if <paramref name="uri"/> matches the glob <paramref name="pattern"/>.
    /// Returns null when either input is blank to preserve SQL three-valued logic.
    /// </summary>
    public static bool? IsMatch(string? uri, string? pattern, bool ignoreCase = true, string? defaultScheme = null)
    {
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(pattern))
            return null;

        var scheme = NormalizeDefaultScheme(defaultScheme ?? DefaultScheme);
        var normalizedUri = NormalizeUri(uri, ignoreCase);
        var normalizedPattern = NormalizePattern(pattern, scheme, ignoreCase);

        var cacheKey = new GlobCacheKey(normalizedPattern, ignoreCase);
        var regex = RegexCache.GetOrAdd(cacheKey, static key =>
        {
            var regexPattern = ConvertGlobToRegex(key.Pattern);
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (key.IgnoreCase)
                options |= RegexOptions.IgnoreCase;
            return new Regex(regexPattern, options);
        });

        return regex.IsMatch(normalizedUri);
    }

    private static string NormalizeUri(string value, bool ignoreCase)
    {
        var trimmed = value.Trim();
        var normalized = CollapseSlashes(trimmed.Replace('\\', '/'));
        return ignoreCase ? normalized.ToLowerInvariant() : normalized;
    }

    private static string NormalizePattern(string value, string defaultScheme, bool ignoreCase)
    {
        var trimmed = value.Trim();
        var normalized = trimmed.Replace('\\', '/');

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = defaultScheme + normalized.TrimStart('/');
        }

        normalized = CollapseSlashes(normalized);
        return ignoreCase ? normalized.ToLowerInvariant() : normalized;
    }

    private static string NormalizeDefaultScheme(string value)
    {
        var trimmed = value.Trim();
        var normalized = trimmed.Replace('\\', '/');

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('/') + "://";
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += '/';
        }

        return CollapseSlashes(normalized);
    }

    private static string CollapseSlashes(string value)
    {
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
            return Collapse(value);

        var prefix = value[..(schemeIndex + 3)];
        var remainder = value[(schemeIndex + 3)..];
        return prefix + Collapse(remainder);

        static string Collapse(string input)
        {
            if (input.Length <= 1)
                return input;

            var sb = new StringBuilder(input.Length);
            var previousSlash = false;
            foreach (var c in input)
            {
                if (c == '/')
                {
                    if (!previousSlash)
                    {
                        sb.Append(c);
                        previousSlash = true;
                    }
                }
                else
                {
                    previousSlash = false;
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }

    private static string ConvertGlobToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        sb.Append('^');

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                {
                    var starCount = 1;
                    while (i + starCount < pattern.Length && pattern[i + starCount] == '*')
                        starCount++;

                    var nextIndex = i + starCount;
                    var nextIsSlash = nextIndex < pattern.Length && pattern[nextIndex] == '/';

                    if (starCount >= 2)
                    {
                        if (nextIsSlash)
                        {
                            sb.Append("(?:.*/)?");
                            i = nextIndex;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += starCount - 1;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                }

                case '?':
                    sb.Append("[^/]");
                    break;

                case '[':
                    if (TryParseCharacterClass(pattern, ref i, out var charClass))
                    {
                        sb.Append(charClass);
                    }
                    else
                    {
                        sb.Append("\\[");
                    }
                    break;

                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        i++;
                        sb.Append(Regex.Escape(pattern[i].ToString()));
                    }
                    else
                    {
                        sb.Append("\\\\");
                    }
                    break;

                case '.':
                case '+':
                case '(':
                case ')':
                case '$':
                case '^':
                case '{':
                case '}':
                case ']':
                case '|':
                    sb.Append('\\').Append(c);
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static bool TryParseCharacterClass(string pattern, ref int index, out string result)
    {
        var length = pattern.Length;
        var cursor = index + 1;
        if (cursor >= length)
        {
            result = "\\[";
            return false;
        }

        var negate = false;
        if (pattern[cursor] is '!' or '^')
        {
            negate = true;
            cursor++;
        }

        var content = new StringBuilder();
        var closed = false;

        if (cursor < length && pattern[cursor] == ']')
        {
            content.Append("\\]");
            cursor++;
        }

        while (cursor < length)
        {
            var c = pattern[cursor];
            if (c == ']')
            {
                closed = true;
                break;
            }

            if (c == '\\' && cursor + 1 < length)
            {
                cursor++;
                content.Append(EscapeForCharClass(pattern[cursor]));
            }
            else
            {
                content.Append(EscapeForCharClass(c));
            }

            cursor++;
        }

        if (!closed || content.Length == 0)
        {
            result = "\\[";
            return false;
        }

        index = cursor;
        var sb = new StringBuilder();
        sb.Append('[');
        if (negate)
            sb.Append('^');
        sb.Append(content);
        sb.Append(']');
        result = sb.ToString();
        return true;
    }

    private static string EscapeForCharClass(char c)
    {
        return c switch
        {
            '\\' => "\\\\",
            ']' => "\\]",
            '^' => "\\^",
            _ => c.ToString()
        };
    }

    private readonly record struct GlobCacheKey(string Pattern, bool IgnoreCase);
}
