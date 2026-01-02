using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RepoQL.Contracts;

/// <summary>
/// Repository-aware URI that follows these rules:
/// <list type="bullet">
/// <item><description><b>Container</b> is the full URI without a fragment (e.g., file, jar entry).</description></item>
/// <item><description><b>Fragment</b> encodes sub-resource identity using one of:
/// <c>#line=&lt;start&gt;,&lt;end&gt;</c>, <c>#char=&lt;start&gt;,&lt;end&gt;</c>,
/// <c>#/&lt;json-pointer&gt;</c>, plain anchor <c>#heading-slug</c>, or parameterized form
/// <c>#symbol=&lt;qualname&gt;&amp;line=…</c>.</description></item>
/// </list>
/// <para>
/// This class <b>inherits</b> <see cref="Uri"/> for drop-in use, and adds a parsed <see cref="Location"/>
/// with strongly-typed accessors. All builder methods return a new <see cref="RepoUri"/>.
/// </para>
/// <example>
/// <code>
/// var u1 = RepoUri.FromAnchor(new Uri("file:///repo/README.md"), "installation");
/// // file:///repo/README.md#installation
///
/// var u2 = RepoUri.FromLines(new Uri("file:///repo/app.py"), startLine: 40, endLine: 55);
/// // file:///repo/app.py#line=40,55
///
/// var u3 = RepoUri.FromJsonPointer(new Uri("file:///repo/openapi.yaml"),
///                                  new[] {"components","schemas","User"});
/// // file:///repo/openapi.yaml#/components/schemas/User
///
/// var ok = RepoUri.TryParse("file:///repo/lib.cs#symbol=Foo.Bar&amp;line=12,12", out var parsed);
/// var span = parsed!.Loc.Line; // (12,12)
/// var symbol = parsed.Loc.Symbol; // "Foo.Bar"
/// </code>
/// </example>
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name")]
public sealed class RepoUri : Uri, IEquatable<RepoUri>
{
    /// <summary>Parsed, structured fragment and helpers.</summary>
    public Location Loc { get; }

    /// <summary>Container URI (the base without the fragment).</summary>
    public Uri Container => new(AbsoluteUri.Split('#')[0], UriKind.Absolute);

    private RepoUri(string absoluteUri, Location loc) : base(absoluteUri, UriKind.Absolute)
    {
        Loc = loc;
    }

    public bool Equals(RepoUri? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;
        return UriEquals(other);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
            return true;
        if (obj is RepoUri otherRepo)
            return Equals(otherRepo);
        if (obj is Uri otherUri)
            return UriEquals(otherUri);
        return false;
    }

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(AbsoluteUri);

    public static bool operator ==(RepoUri? left, RepoUri? right)
        => ReferenceEquals(left, right) || left is not null && left.Equals(right);

    public static bool operator !=(RepoUri? left, RepoUri? right)
        => !(left == right);

    private bool UriEquals(Uri other)
        => StringComparer.OrdinalIgnoreCase.Equals(AbsoluteUri, other.AbsoluteUri);

    /// <summary>
    /// Normalize a URI string for storage and comparison. Removes control characters,
    /// trims whitespace, validates scheme, and collapses duplicate path slashes.
    /// </summary>
    public static string Normalize(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return uri ?? string.Empty;

        // Remove newlines (LF, CR), null bytes, and other control characters
        var sb = new StringBuilder(uri.Length);
        foreach (var c in uri)
        {
            // Skip control characters (0x00-0x1F) except tab which might be in some URIs
            if (c < 0x20 && c != '\t')
                continue;
            sb.Append(c);
        }
        var normalized = sb.ToString().Trim();

        // Validate: URI should not be empty after normalization
        if (normalized.Length == 0)
            throw new ArgumentException($"URI is empty after normalization. Original length: {uri.Length}");

        // Validate: URI should have a scheme
        var schemeEnd = normalized.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 1)
            throw new ArgumentException($"URI missing or invalid scheme. Got: '{Truncate(normalized, 100)}'");

        // Validate: For file:// URIs, reject absolute Windows paths (must be repo-relative)
        if (normalized.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            var path = normalized[8..]; // After "file:///"
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                throw new ArgumentException(
                    $"file:// URI must be repo-relative, not absolute. Got drive letter '{path[0]}:' in: '{Truncate(normalized, 100)}'");
            }
        }

        // Normalize: Remove duplicate slashes in path (but not in scheme)
        var pathStart = schemeEnd + 3;
        if (pathStart < normalized.Length)
        {
            var scheme = normalized[..pathStart];
            var path = normalized[pathStart..];

            // Replace multiple consecutive slashes with single slash
            while (path.Contains("//", StringComparison.Ordinal))
                path = path.Replace("//", "/", StringComparison.Ordinal);

            normalized = scheme + path;
        }

        return normalized;
    }

    public static string Normalize(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Normalize(uri.ToString());
    }

    public string NormalizedContainer => Normalize(Container.AbsoluteUri);

    private static string Truncate(string s, int maxLength)
        => s.Length <= maxLength ? s : s[..(maxLength - 3)] + "...";

    public static RepoUri Create(Uri container, Location loc = new())
    {
        if (!container.IsAbsoluteUri) throw new ArgumentException("Container must be absolute.", nameof(container));
        var fragment = RenderFragment(loc);
        var full = fragment is { Length: > 0 }
            ? container.AbsoluteUri + "#" + fragment
            : container.AbsoluteUri;
        return new RepoUri(full, loc);
    }

    // -------- PUBLIC BUILDERS --------

    public static RepoUri FromAnchor(Uri container, string anchor)
        => Create(container, Location.FromAnchor(anchor));

    public static RepoUri FromLines(Uri container, int? startLine, int? endLine)
        => Create(container, Location.FromLineRange(startLine, endLine));

    public static RepoUri FromChars(Uri container, long? startChar, long? endChar)
        => Create(container, Location.FromCharRange(startChar, endChar));

    public static RepoUri FromJsonPointer(Uri container, string pointer)
        => Create(container, Location.FromJsonPointer(pointer));

    public static RepoUri FromJsonPointer(Uri container, IEnumerable<string> pointerSegments)
        => Create(container, Location.FromJsonPointerSegments(pointerSegments));

    public static RepoUri FromSymbol(Uri container, string symbol, int? startLine = null, int? endLine = null)
        => Create(container, Location.FromSymbol(symbol).WithLineRange(startLine, endLine));

    public static RepoUri FromParams(Uri container, IDictionary<string, string?> parameters)
        => Create(container, Location.FromParameters(parameters));

    // -------- PARSE --------

    /// <summary>
    /// Parse a repository URI. Returns false if <paramref name="s"/> is not an absolute URI
    /// or the fragment cannot be interpreted (unknown forms still parse into <see cref="Location.Raw"/>).
    /// </summary>
    public static bool TryParse(string s, [NotNullWhen(true)] out RepoUri? result)
    {
        result = null;
        if (!TryCreate(s, UriKind.Absolute, out _)) return false;

        // Get the raw fragment without URL decoding
        var hashIndex = s.IndexOf('#', StringComparison.Ordinal);
        var rawFrag = hashIndex >= 0 ? s[(hashIndex + 1)..] : string.Empty;
        var loc = ParseFragment(rawFrag);

        // Use the original string up to the fragment, then rebuild with our parsed fragment
        var containerPart = hashIndex >= 0 ? s[..hashIndex] : s;
        var fragment = RenderFragment(loc);
        var fullUri = fragment.Length > 0 ? containerPart + "#" + fragment : containerPart;

        result = new RepoUri(fullUri, loc);
        return true;
    }

    /// <summary>
    ///    Parses a repoUri, throwing if it is invalid
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    /// <exception cref="FormatException"></exception>
    public static RepoUri Parse(string uri) =>
        TryParse(uri, out var result)
            ? result
            : throw new FormatException("Invalid URI");

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public static implicit operator RepoUri(string uri) => Parse(uri);
    
    // -------- RENDER / PARSE FRAGMENT --------

    private static string RenderFragment(Location loc)
    {
        // Prefer explicit encodings in priority order: pointer, params, line/char, anchor.
        if (!string.IsNullOrEmpty(loc.JsonPointer))
        {
            // Must start with '/' per RFC 6901
            var pointer = loc.JsonPointer.StartsWith('/') ? loc.JsonPointer : "/" + loc.JsonPointer;
            return pointer;
        }

        // Parameterized fragment
        if (loc.Parameters.Count > 0 || !string.IsNullOrEmpty(loc.Symbol))
        {
            var dict = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in loc.Parameters) dict[kv.Key] = kv.Value;

            if (!string.IsNullOrEmpty(loc.Symbol))
                dict["symbol"] = Uri.EscapeDataString(loc.Symbol);

            if (loc.Line is not null && (loc.Line.Value.Start is not null || loc.Line.Value.End is not null))
                dict["line"] = FormatPair(loc.Line.Value.Start, loc.Line.Value.End);

            if (loc.Char is not null && (loc.Char.Value.Start is not null || loc.Char.Value.End is not null))
                dict["char"] = FormatPair(loc.Char.Value.Start, loc.Char.Value.End);

            return string.Join("&", dict.Select(kv => kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}"));
        }

        if (loc.Line is not null && (loc.Line.Value.Start is not null || loc.Line.Value.End is not null))
            return "line=" + FormatPair(loc.Line.Value.Start, loc.Line.Value.End);

        if (loc.Char is not null && (loc.Char.Value.Start is not null || loc.Char.Value.End is not null))
            return "char=" + FormatPair(loc.Char.Value.Start, loc.Char.Value.End);

        if (!string.IsNullOrEmpty(loc.Anchor))
            return loc.Anchor!; // anchors are already slugged by producer

        return string.Empty;

        static string FormatPair(long? a, long? b)
        {
            var sa = a?.ToString() ?? string.Empty;
            var sb = b?.ToString() ?? string.Empty;
            return string.IsNullOrEmpty(sb) ? sa : $"{sa},{sb}";
        }
    }

    private static Location ParseFragment(string frag)
    {
        var loc = new Location { Raw = frag, Parameters = new() };

        if (string.IsNullOrEmpty(frag))
            return loc;

        // JSON Pointer: starts with '/'
        if (frag.StartsWith('/'))
            return Location.FromJsonPointer(frag);

        // Named parameters: k=v[&k=v...]
        if (frag.Contains('='))
        {
            var pairs = frag.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in pairs)
            {
                var kv = p.Split('=', 2);
                var key = kv[0];
                var val = kv.Length == 2 ? kv[1] : null;

                switch (key.ToLowerInvariant())
                {
                    case "line":
                        ParseRange(val, out var ls, out var le);
                        loc = loc.WithLineRange((int?)ls, (int?)le);
                        break;
                    case "char":
                        ParseRange(val, out var cs, out var ce);
                        loc = loc.WithCharRange(cs, ce);
                        break;
                    case "symbol":
                        loc = loc with { Symbol = Uri.UnescapeDataString(val ?? string.Empty) };
                        break;
                    default:
                        loc.Parameters[key] = val;
                        break;
                }
            }
            return loc;
        }

        // RFC 5147-like simple ranges
        if (frag.StartsWith("line=", StringComparison.OrdinalIgnoreCase))
        {
            ParseRange(frag.AsSpan(5), out var s, out var e);
            return Location.FromLineRange((int?)s, (int?)e);
        }
        if (frag.StartsWith("char=", StringComparison.OrdinalIgnoreCase))
        {
            ParseRange(frag.AsSpan(5), out var s, out var e);
            return Location.FromCharRange(s, e);
        }

        // Plain anchor
        return Location.FromAnchor(frag);

        static void ParseRange(ReadOnlySpan<char> s, out long? start, out long? end)
        {
            start = end = null;
            if (s.IsEmpty) return;
            var parts = s.ToString().Split(',', 2);
            if (long.TryParse(parts[0], out var a)) start = a;
            if (parts.Length == 2 && long.TryParse(parts[1], out var b)) end = b;
        }
    }

    // -------- LOCATION MODEL --------

    /// <summary>
    /// Structured sub-resource location carried in the fragment.
    /// Combine fields as needed. For example: a symbol with a line range.
    /// </summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible")]
    public readonly record struct Location
    {
        /// <summary>Original fragment text (no '#'). Preserved for round-tripping.</summary>
        public string Raw { get; init; }

        /// <summary>Plain anchor, e.g., "installation". Mutually exclusive with <see cref="JsonPointer"/>.</summary>
        public string? Anchor { get; init; }

        /// <summary>RFC 6901 JSON Pointer (must start with '/'), e.g., "/components/schemas/User".</summary>
        public string? JsonPointer { get; init; }

        /// <summary>Optional code symbol qualifier, e.g., "Foo.Bar.Baz".</summary>
        public string? Symbol { get; init; }

        /// <summary>1-based line range. Either bound may be null.</summary>
        public (int? Start, int? End)? Line { get; init; }

        /// <summary>0-based character offset range. Either bound may be null.</summary>
        public (long? Start, long? End)? Char { get; init; }

        /// <summary>Additional parameters preserved verbatim. Keys are case-sensitive.</summary>
        public Dictionary<string, string?> Parameters { get; init; }

        // --- factories ---

        public static Location FromAnchor(string anchor) => new()
        {
            Anchor = anchor,
            Parameters = new()
        };

        public static Location FromJsonPointer(string pointer) => new()
        {
            JsonPointer = pointer.StartsWith('/') ? pointer : "/" + pointer,
            Parameters = new()
        };

        public static Location FromJsonPointerSegments(IEnumerable<string> segments) => new()
        {
            JsonPointer = "/" + string.Join("/", segments.Select(EncodeJsonPointerSegment)),
            Parameters = new()
        };

        public static Location FromLineRange(int? start, int? end) => new()
        {
            Line = (start, end),
            Parameters = new()
        };

        public static Location FromCharRange(long? start, long? end) => new()
        {
            Char = (start, end),
            Parameters = new()
        };

        public static Location FromSymbol(string symbol) => new()
        {
            Symbol = symbol,
            Parameters = new()
        };

        public static Location FromParameters(IDictionary<string, string?> parameters) => new()
        {
            Parameters = new(parameters)
        };

        // --- modifiers ---

        public Location WithLineRange(int? start, int? end) => this with { Line = (start, end) };
        public Location WithCharRange(long? start, long? end) => this with { Char = (start, end) };

        // --- JSON Pointer helpers ---

        /// <summary>Encode one RFC 6901 segment: '~'→'~0', '/'→'~1'.</summary>
        public static string EncodeJsonPointerSegment(string segment)
            => segment.Replace("~", "~0").Replace("/", "~1");

        /// <summary>Decode one RFC 6901 segment: '~1'→'/', '~0'→'~'.</summary>
        public static string DecodeJsonPointerSegment(string segment)
            => segment.Replace("~1", "/").Replace("~0", "~");

        /// <summary>Split <see cref="JsonPointer"/> into decoded segments.</summary>
        public IReadOnlyList<string> GetJsonPointerSegments()
        {
            if (string.IsNullOrEmpty(JsonPointer) || !JsonPointer!.StartsWith('/'))
                return [];
            return [.. JsonPointer!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(DecodeJsonPointerSegment)];
        }

        public override int GetHashCode() => ToString().GetHashCode();
    }

    public RepoUri ToRepoUri()
    {
        throw new NotImplementedException();
    }
}
