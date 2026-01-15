using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for repository URI manipulation and fragment parsing.
///
/// Purpose: Provides SQL-callable functions for extracting and constructing
/// parts of repository URIs (container, fragment, line numbers, symbols).
///
/// Complexity: Pure string manipulation with URI parsing. No external dependencies.
/// </summary>
[UdfClass]
public class RepoUriUdf
{
    /// <summary>
    /// Extracts the container (base URI without fragment) from a repository URI.
    /// Example: "file:///src/Foo.cs#line=1" → "file:///src/Foo.cs"
    /// </summary>
    [ScalarUdf("repository_uri_container", IsPure = true)]
    public string? GetContainer(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var hash = uri.IndexOf('#');
        return hash < 0 ? uri : uri[..hash];
    }

    /// <summary>
    /// Extracts the fragment portion from a repository URI.
    /// Example: "file:///src/Foo.cs#line=1,10" → "line=1,10"
    /// </summary>
    [ScalarUdf("repository_uri_fragment", IsPure = true)]
    public string? GetFragment(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var hash = uri.IndexOf('#');
        if (hash < 0 || hash == uri.Length - 1)
            return null;

        return uri[(hash + 1)..];
    }

    /// <summary>
    /// Joins a container URI with a fragment.
    /// Example: ("file:///src/Foo.cs", "line=1") → "file:///src/Foo.cs#line=1"
    /// </summary>
    [ScalarUdf("repository_uri_join", IsPure = true)]
    public string? Join(string? container, [UdfDefault("NULL")] string? fragment)
    {
        if (string.IsNullOrWhiteSpace(container))
            return null;

        if (string.IsNullOrEmpty(fragment))
            return container;

        return container + "#" + fragment;
    }

    /// <summary>
    /// Classifies the type of fragment: "empty", "json_pointer", "line", "char", "parameters", or "anchor".
    /// </summary>
    [ScalarUdf("repository_uri_fragment_kind", IsPure = true)]
    public string GetFragmentKind(string? uri)
    {
        var frag = ExtractFragment(uri);
        if (string.IsNullOrEmpty(frag))
            return "empty";

        if (frag.StartsWith("/"))
            return "json_pointer";
        if (frag.StartsWith("line=", StringComparison.OrdinalIgnoreCase))
            return "line";
        if (frag.StartsWith("char=", StringComparison.OrdinalIgnoreCase))
            return "char";
        if (frag.Contains('='))
            return "parameters";

        return "anchor";
    }

    /// <summary>
    /// Extracts the filename from a repository URI.
    /// Example: "file:///src/Foo.cs#line=1" → "Foo.cs"
    /// </summary>
    [ScalarUdf("repository_uri_file_name", IsPure = true)]
    public string? GetFileName(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var hash = uri.IndexOf('#');
        var container = hash >= 0 ? uri[..hash] : uri;

        try
        {
            if (Uri.TryCreate(container, UriKind.Absolute, out var u))
            {
                var path = u.IsFile ? u.LocalPath : u.AbsolutePath;
                return Path.GetFileName(path);
            }

            // Fallback: treat as path
            return Path.GetFileName(container);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the start line from a #line=START,END fragment.
    /// Returns the line number as a string for SQL compatibility.
    /// </summary>
    [ScalarUdf("repository_uri_line_start", IsPure = true)]
    public string? GetLineStart(string? uri)
    {
        var frag = ExtractFragment(uri);
        var payload = frag is null ? null : ExtractKeyPayload(frag, "line", "line=");
        if (payload is null)
            return null;

        var parts = payload.Split(',', 2);
        return int.TryParse(parts[0], out var start) ? start.ToString() : null;
    }

    /// <summary>
    /// Parses the end line from a #line=START,END fragment.
    /// Returns the line number as a string for SQL compatibility.
    /// </summary>
    [ScalarUdf("repository_uri_line_end", IsPure = true)]
    public string? GetLineEnd(string? uri)
    {
        var frag = ExtractFragment(uri);
        var payload = frag is null ? null : ExtractKeyPayload(frag, "line", "line=");
        if (payload is null)
            return null;

        var parts = payload.Split(',', 2);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var end))
            return null;

        return end.ToString();
    }

    /// <summary>
    /// Extracts JSON pointer from fragment if present (starts with /).
    /// Example: "file:///data.json#/path/to/property" → "/path/to/property"
    /// </summary>
    [ScalarUdf("repository_uri_json_pointer", IsPure = true)]
    public string? GetJsonPointer(string? uri)
    {
        var frag = ExtractFragment(uri);
        if (!string.IsNullOrEmpty(frag) && frag.StartsWith("/"))
            return frag;

        return null;
    }

    /// <summary>
    /// Extracts plain anchor text from fragment (non-special fragments like #SectionName).
    /// Returns null if fragment is a JSON pointer, line/char range, or parameterized.
    /// </summary>
    [ScalarUdf("repository_uri_anchor", IsPure = true)]
    public string? GetAnchor(string? uri)
    {
        var frag = ExtractFragment(uri);
        if (string.IsNullOrEmpty(frag))
            return null;

        if (frag.StartsWith('/') ||
            frag.StartsWith("line=", StringComparison.OrdinalIgnoreCase) ||
            frag.StartsWith("char=", StringComparison.OrdinalIgnoreCase) ||
            frag.Contains('='))
        {
            return null;
        }

        return frag;
    }

    /// <summary>
    /// Extracts the symbol parameter from fragment, URL-decoded.
    /// Example: "file:///Foo.cs#symbol=MyClass.Method" → "MyClass.Method"
    /// </summary>
    [ScalarUdf("repository_uri_symbol", IsPure = true)]
    public string? GetSymbol(string? uri)
    {
        var frag = ExtractFragment(uri);
        var payload = frag is null ? null : ExtractKeyPayload(frag, "symbol", "symbol=");
        if (payload is null)
            return null;

        try
        {
            return Uri.UnescapeDataString(payload);
        }
        catch
        {
            return payload;
        }
    }

    /// <summary>
    /// Constructs a line range fragment from start and end line numbers.
    /// Example: (10, 20) → "line=10,20"
    /// </summary>
    [ScalarUdf("fragment_from_line_range", IsPure = true)]
    public string? BuildLineFragment(
        [UdfDefault("NULL")] string? startLine,
        [UdfDefault("NULL")] string? endLine)
    {
        int? start = int.TryParse(startLine, out var s) ? s : null;
        int? end = int.TryParse(endLine, out var e) ? e : null;

        if (start is null && end is null)
            return null;

        if (end is null) return $"line={start}";
        if (start is null) return $"line=,{end}";
        return $"line={start},{end}";
    }

    /// <summary>
    /// Constructs a char range fragment from start and end byte offsets.
    /// Example: (100, 200) → "char=100,200"
    /// </summary>
    [ScalarUdf("fragment_from_char_range", IsPure = true)]
    public string? BuildCharFragment(
        [UdfDefault("NULL")] string? startChar,
        [UdfDefault("NULL")] string? endChar)
    {
        long? start = long.TryParse(startChar, out var s) ? s : null;
        long? end = long.TryParse(endChar, out var e) ? e : null;

        if (start is null && end is null)
            return null;

        if (end is null) return $"char={start}";
        if (start is null) return $"char=,{end}";
        return $"char={start},{end}";
    }

    /// <summary>
    /// Constructs a symbol fragment from a qualified name, URL-encoded.
    /// Example: "MyClass.Method" → "symbol=MyClass.Method"
    /// </summary>
    [ScalarUdf("fragment_from_symbol", IsPure = true)]
    public string? BuildSymbolFragment(string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return null;

        return "symbol=" + Uri.EscapeDataString(qualifiedName);
    }

    #region Helper Methods

    private static string? ExtractFragment(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var hash = uri.IndexOf('#');
        if (hash < 0 || hash == uri.Length - 1)
            return null;

        return uri[(hash + 1)..];
    }

    private static string? ExtractKeyPayload(string fragment, string key, string prefix)
    {
        if (fragment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var payload = fragment[prefix.Length..];
            // Handle parameterized fragments: #line=12,20&symbol=Foo → extract "12,20"
            var nextParam = payload.IndexOf('&');
            return nextParam >= 0 ? payload[..nextParam] : payload;
        }

        if (fragment.IndexOf('=') >= 0)
        {
            var pairs = fragment.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in pairs)
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 0) continue;
                if (kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv.Length == 2 ? kv[1] : string.Empty;
            }
        }

        return null;
    }

    #endregion
}
