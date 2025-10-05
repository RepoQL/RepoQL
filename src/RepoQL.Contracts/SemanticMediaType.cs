using System.Text;

namespace RepoQL.Contracts;

/// <summary>
///     Semantic media type:
///     type "/" subtype [ "+" suffix ] *( ";" param )
///     Reserved params (lowercase keys): kind, profile, schema, version, charset.
///     - kind    : what the data represents (dot-notation, e.g. "openapi", "cs.class", "py.module").
///     - profile : URI identifying a constrained profile/vocabulary (RFC 6906). Quote if needed.
///     - schema  : URI of a validating schema or IDL. Quote if needed.
///     - version : representation version label.
///     - charset : standard parameter for text encodings.
/// </summary>
public sealed class SemanticMediaType
{
    private readonly SortedDictionary<string, string?> _parameters;

    private SemanticMediaType(string type, string subtype, string? suffix, IDictionary<string, string?>? parameters)
    {
        Type = Lower(type);
        Subtype = Lower(subtype);
        Suffix = suffix is null ? null : Lower(suffix);
        _parameters = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        if (parameters == null)
            return;
        foreach (var (k, v) in parameters)
            _parameters[Lower(k)] = v;
    }

    public string Type { get; }
    public string Subtype { get; }
    public string? Suffix { get; }
    public IReadOnlyDictionary<string, string?> Parameters => _parameters;

    public string? Kind => Get("kind");
    public string? Version => Get("version");
    public string? Charset => Get("charset");
    public Uri? Profile => TryGetUri("profile");
    public Uri? Schema => TryGetUri("schema");

    public static SemanticMediaType Create(string type, string subtype, string? suffix = null,
        IDictionary<string, string?>? parameters = null)
    {
        return new SemanticMediaType(type, subtype, suffix, parameters);
    }

    public SemanticMediaType With(string key, string? value)
    {
        var copy = new SemanticMediaType(Type, Subtype, Suffix, _parameters);
        if (value is null) copy._parameters.Remove(Lower(key));
        else copy._parameters[Lower(key)] = value;
        return copy;
    }

    public SemanticMediaType WithKind(string? kind)
    {
        return With("kind", kind);
    }

    public SemanticMediaType WithVersion(string? version)
    {
        return With("version", version);
    }

    public SemanticMediaType WithCharset(string? charset)
    {
        return With("charset", charset);
    }

    public SemanticMediaType WithProfile(Uri? uri)
    {
        return With("profile", uri?.ToString());
    }

    public SemanticMediaType WithSchema(Uri? uri)
    {
        return With("schema", uri?.ToString());
    }

    public static bool TryParse(string s, out SemanticMediaType? mt)
    {
        mt = null;
        if (string.IsNullOrWhiteSpace(s)) return false;

        // head: type/subtype[+suffix]
        var (head, rest) = SplitHeadAndParams(s);
        var slash = head.IndexOf('/');
        if (slash <= 0 || slash == head.Length - 1) return false;

        var type = head[..slash].Trim();
        var sub = head[(slash + 1)..].Trim();

        string? suffix = null;
        var plus = sub.LastIndexOf('+');
        if (plus > 0 && plus < sub.Length - 1)
        {
            suffix = sub[(plus + 1)..];
            sub = sub[..plus];
        }

        var dict = ParseParameters(rest);
        mt = new SemanticMediaType(type, sub, suffix, dict);
        return true;
    }

    public static SemanticMediaType Parse(string s)
    {
        return TryParse(s, out var mt) ? mt! : throw new FormatException($"Invalid media type: {s}");
    }

    public override string ToString()
    {
        var head = $"{Type}/{Subtype}{(Suffix is null ? "" : "+" + Suffix)}";
        if (_parameters.Count == 0) return head;

        var sb = new StringBuilder(head);
        foreach (var (k, v) in _parameters)
        {
            sb.Append(';').Append(k);
            if (v != null) sb.Append('=').Append(FormatParamValue(v));
        }

        return sb.ToString();
    }

    // ---------- helpers ----------

    private static string Lower(string s)
    {
        return s.Trim().ToLowerInvariant();
    }

    private string? Get(string key)
    {
        return _parameters.TryGetValue(Lower(key), out var v) ? v : null;
    }

    private Uri? TryGetUri(string key)
    {
        var v = Get(key);
        try
        {
            return v != null && Uri.TryCreate(UnquoteIfQuoted(v), UriKind.RelativeOrAbsolute, out var u)
                ? u.IsAbsoluteUri
                    ? u
                    : new Uri($"file://{u.ToString()}", UriKind.Absolute)
                : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static (string head, string rest) SplitHeadAndParams(string s)
    {
        var inQuotes = false;
        var esc = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuotes)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inQuotes = false;
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ';')
                    return (s[..i].Trim(), s[(i + 1)..]);
            }
        }

        return (s.Trim(), "");
    }

    private static Dictionary<string, string?> ParseParameters(string rest)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitParams(rest))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                dict[Lower(part)] = null;
                continue;
            }

            var key = Lower(part[..eq].Trim());
            var val = part[(eq + 1)..].Trim();

            if (val is ['"', _, ..] && val[^1] == '"')
                dict[key] = Unquote(val);
            else
                dict[key] = val;
        }

        return dict;
    }

    private static IEnumerable<string> SplitParams(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var inQuotes = false;
        var esc = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuotes)
            {
                if (esc)
                {
                    esc = false;
                }
                else
                {
                    switch (c)
                    {
                        case '\\':
                            esc = true;
                            break;
                        case '"':
                            inQuotes = false;
                            break;
                    }
                }
            }
            else
            {
                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ';':
                        yield return s[start..i];
                        start = i + 1;
                        break;
                }
            }
        }

        if (start < s.Length) yield return s[start..];
    }

    private static string FormatParamValue(string value)
    {
        return IsToken(value) ? value : Quote(value);
    }

    private static bool IsToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
        {
            if (ch is >= 'A' and <= 'Z' || ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9')
                continue;
            switch (ch)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    continue;
                default: return false;
            }
        }

        return true;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            if (ch == '\\' || ch == '"') sb.Append('\\');
            sb.Append(ch);
        }

        return sb.Append('"').ToString();
    }

    private static string Unquote(string quoted)
    {
        var sb = new StringBuilder(quoted.Length - 2);
        for (var i = 1; i < quoted.Length - 1; i++)
        {
            var ch = quoted[i];
            if (ch == '\\' && i + 1 < quoted.Length - 1)
            {
                i++;
                sb.Append(quoted[i]);
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string UnquoteIfQuoted(string v) => v is ['"', _, ..] && v[^1] == '"' ? Unquote(v) : v;
}