using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for code snippet rendering and text analysis.
///
/// Purpose: Provides SQL-callable functions for language detection,
/// byte offset to line/column conversion, display labels, match scoring,
/// and binary file previews.
///
/// Complexity: Contains UTF-8 parsing, fuzzy matching, and filesystem access.
/// binary_preview is volatile (filesystem I/O), all others are pure.
/// </summary>
[UdfClass]
public class SnippetUdf
{
    private readonly RepositoryConfiguration? _repoConfig;
    private readonly UriRegistry? _uriRegistry;
    private readonly IMultiFileSystem? _fileSystem;

    public SnippetUdf()
    {
    }

    public SnippetUdf(
        RepositoryConfiguration repoConfig,
        UriRegistry uriRegistry,
        IMultiFileSystem? fileSystem = null)
    {
        _repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
        _uriRegistry = uriRegistry ?? throw new ArgumentNullException(nameof(uriRegistry));
        _fileSystem = fileSystem;
    }

    [StructuredUdf("_snippet_glob_internal",
        MacroName = "snippet_glob",
        Description = "Returns uri + snippet text for files matching a glob pattern")]
    public IEnumerable<SnippetGlobRow> SnippetGlob(
        string pattern,
        [UdfDefault("NULL")] int? max_results)
    {
        if (string.IsNullOrWhiteSpace(pattern) || _uriRegistry is null)
            yield break;

        var limit = max_results is > 0 ? max_results.Value : int.MaxValue;
        var emitted = 0;

        foreach (var repoUri in _uriRegistry.MatchPattern(pattern))
        {
            if (emitted >= limit)
                yield break;

            if (!TryOpenTextReader(repoUri, out var reader))
                continue;

            using (reader)
            {
                var snippet = ExtractSnippet(reader, repoUri);
                yield return new SnippetGlobRow(repoUri.AbsoluteUri, snippet);
                emitted++;
            }
        }
    }

    /// <summary>
    /// Infers programming language from media type or file URI for syntax highlighting.
    /// Checks media type kind, then subtype, then base type, then file extension.
    /// </summary>
    [ScalarUdf("language_from_media_type_or_uri", IsPure = true)]
    public string? GetLanguage(string? mediaType, [UdfDefault("NULL")] string? uri)
    {
        string? lang = null;

        // 1) Media type based
        if (!string.IsNullOrWhiteSpace(mediaType) && SemanticMediaType.TryParse(mediaType, out var mt))
        {
            var baseType = $"{mt!.Type}/{mt.Subtype}".ToLowerInvariant();
            var subtype = mt.Subtype.ToLowerInvariant();
            var kind = mt.Kind?.ToLowerInvariant();

            // explicit kind wins
            lang = kind switch
            {
                "csharp" or "cs" => "csharp",
                "python" => "python",
                "typescript" or "ts" => "ts",
                "javascript" or "js" => "javascript",
                "java" => "java",
                "rust" => "rust",
                "go" => "go",
                "ruby" => "ruby",
                "bash" or "shell" or "sh" => "bash",
                "sql" => "sql",
                "markdown" or "md" => "markdown",
                "openapi" when baseType.Contains("yaml") => "yaml",
                "openapi" when baseType.Contains("json") => "json",
                _ => null
            };

            // infer from subtype (handles vendor x-…)
            lang ??= subtype.Contains("csharp") ? "csharp" :
                     subtype.Contains("typescript") ? "ts" :
                     subtype.Contains("javascript") ? "javascript" :
                     subtype.Contains("python") ? "python" :
                     subtype.Contains("java") ? "java" :
                     subtype.Contains("rust") ? "rust" :
                     subtype.Contains("ruby") ? "ruby" :
                     subtype.Contains("golang") || subtype == "go" ? "go" :
                     subtype is "x-sh" or "x-shellscript" or "bash" ? "bash" :
                     null;

            // infer from base type
            if (lang is null)
            {
                if (baseType.Contains("json")) lang = "json";
                else if (baseType.Contains("yaml") || baseType.Contains("yml")) lang = "yaml";
                else if (baseType.Contains("xml")) lang = "xml";
                else if (baseType.Contains("markdown")) lang = "markdown";
                else if (baseType is "text/x-csharp") lang = "csharp";
            }
        }

        // 2) Extension fallback (container URI)
        if (lang is null && !string.IsNullOrWhiteSpace(uri))
        {
            lang = TryLanguageFromPath(uri);
        }

        return lang;
    }

    /// <summary>
    /// Converts a UTF-8 byte offset to a 1-based line number.
    /// </summary>
    [ScalarUdf("line_for_byte_offset", IsPure = true)]
    public string? GetLineForOffset(string? text, [UdfDefault("NULL")] string? byteOffsetStr)
    {
        if (string.IsNullOrEmpty(text) || !long.TryParse(byteOffsetStr, out var off) || off < 0)
            return null;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (off > bytes.LongLength) off = bytes.LongLength;

        var line = 1;
        for (long j = 0; j < off; j++)
            if (bytes[j] == (byte)'\n') line++;

        return line.ToString();
    }

    /// <summary>
    /// Converts a UTF-8 byte offset to a 1-based column number.
    /// Handles CRLF line endings and UTF-8 code points correctly.
    /// </summary>
    [ScalarUdf("column_for_byte_offset", IsPure = true)]
    public string? GetColumnForOffset(string? text, [UdfDefault("NULL")] string? byteOffsetStr)
    {
        if (string.IsNullOrEmpty(text) || !long.TryParse(byteOffsetStr, out var off) || off < 0)
            return null;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (off > bytes.LongLength) off = bytes.LongLength;

        // Find start of line (after last '\n' strictly before off)
        long lastNl = -1;
        for (long j = 0; j < off; j++)
            if (bytes[j] == (byte)'\n') lastNl = j;
        var start = (int)(lastNl + 1);

        // Walk UTF-8 code points up to 'off'
        var pos = start;
        var charsBefore = 0;
        while (pos < off)
        {
            var span = bytes.AsSpan(pos, (int)(off - pos));
            var status = Rune.DecodeFromUtf8(span, out _, out var consumed);
            if (status != OperationStatus.Done || consumed <= 0)
                break; // partial code point: do not advance into it
            pos += consumed;
            charsBefore++;
        }
        var atBoundary = (pos == off);

        // If at a boundary: caret is before next code point → +1
        // If inside a code point: caret is inside current glyph → do not +1
        var col = atBoundary ? charsBefore + 1 : charsBefore;
        if (col < 1) col = 1;

        return col.ToString();
    }

    /// <summary>
    /// Extracts a display label from node properties JSON.
    /// Tries 'text', then 'name', then 'slug' properties.
    /// </summary>
    [ScalarUdf("node_display_label", IsPure = true)]
    public string? GetDisplayLabel(string? kind, [UdfDefault("NULL")] string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
            return null;

        try
        {
            var node = JsonNode.Parse(propertiesJson) as JsonObject;
            if (node != null)
            {
                return node["text"]?.GetValue<string?>()
                    ?? node["name"]?.GetValue<string?>()
                    ?? node["slug"]?.GetValue<string?>();
            }
        }
        catch
        {
            // Ignore JSON parse errors
        }

        return null;
    }

    /// <summary>
    /// Computes a fuzzy match score (0-5) between a pattern and text.
    /// Rewards consecutive matches, boundary hits, and early positions.
    /// </summary>
    [ScalarUdf("match_score", IsPure = true)]
    public string? ComputeScore(string? pattern, [UdfDefault("NULL")] string? text)
    {
        var score = ComputeMatchScore(pattern, text);
        return score.ToString("F4");
    }

    /// <summary>
    /// Reads the first N bytes of a file and generates a hex+ASCII preview.
    /// Only works with file:// URIs. Volatile - touches filesystem.
    /// </summary>
    [ScalarUdf("binary_preview", IsPure = false)]
    public string? GetBinaryPreview(string? storageUri, [UdfDefault("1024")] string? maxBytesStr)
    {
        if (string.IsNullOrWhiteSpace(storageUri))
            return null;

        var maxBytes = int.TryParse(maxBytesStr, out var m) ? Math.Max(0, m) : 1024;

        try
        {
            if (!Uri.TryCreate(storageUri, UriKind.Absolute, out var uri) || uri.Scheme != "file")
                return null;

            var path = uri.LocalPath;
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(maxBytes, (int)fs.Length)];
            var read = fs.Read(buf, 0, buf.Length);

            var sb = new StringBuilder(read * 3);
            for (var k = 0; k < read; k++)
            {
                if (k % 16 == 0) sb.AppendFormat("{0:X8}  ", k);
                sb.AppendFormat("{0:X2} ", buf[k]);
                if (k % 16 == 15 || k == read - 1)
                {
                    var start = (k / 16) * 16;
                    sb.Append(' ');
                    for (var j = start; j <= k; j++)
                    {
                        var c = buf[j] >= 32 && buf[j] <= 126 ? (char)buf[j] : '.';
                        sb.Append(c);
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    #region Helper Methods

    private static double ComputeMatchScore(string? pattern, string? text)
    {
        var patSpan = (pattern ?? string.Empty).AsSpan();
        var txtSpan = (text ?? string.Empty).AsSpan();

        if (patSpan.Length == 0)
            return 1.0;
        if (txtSpan.Length == 0 || patSpan.Length > txtSpan.Length)
            return 0.0;

        int[]? rented = null;
        Span<int> positions = patSpan.Length <= 256
            ? stackalloc int[patSpan.Length]
            : (rented = ArrayPool<int>.Shared.Rent(patSpan.Length)).AsSpan(0, patSpan.Length);

        try
        {
            var matched = 0;
            var searchIndex = 0;
            for (var i = 0; i < patSpan.Length; i++)
            {
                var target = char.ToLowerInvariant(patSpan[i]);
                var found = false;
                for (; searchIndex < txtSpan.Length; searchIndex++)
                {
                    if (target == char.ToLowerInvariant(txtSpan[searchIndex]))
                    {
                        positions[matched++] = searchIndex++;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return 0.0;
            }

            var score = 0.0;
            var prev = -1;
            for (var i = 0; i < matched; i++)
            {
                var current = positions[i];
                var s = 1.0;
                if (prev >= 0)
                {
                    var gap = current - prev - 1;
                    s += gap == 0 ? 1.5 : -Math.Min(gap, 32) * 0.04;
                }

                if (IsBoundary(txtSpan, current)) s += 0.8;
                if (current == 0) s += 0.3;

                score += s;
                prev = current;
            }

            score -= Math.Max(0, txtSpan.Length - matched) * 0.005;
            return Math.Clamp(score / matched, 0.0, 5.0);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoundary(ReadOnlySpan<char> text, int index)
    {
        if (index == 0) return true;
        var prev = text[index - 1];
        var current = text[index];
        return IsSeparator(prev) || (char.IsLower(prev) && char.IsUpper(current));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeparator(char c)
        => c is '/' or '\\' or '_' or '-' or ' ' or '.';

    private static string? TryLanguageFromPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;

        // Strip fragment and nested container (e.g., jar:…!/path)
        var s = uri;
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash];
        var bang = s.IndexOf('!');
        if (bang >= 0 && bang < s.Length - 1) s = s[(bang + 1)..];

        string path;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u))
            path = u.IsFile ? u.LocalPath : u.AbsolutePath;
        else
            path = s;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".csx" => "csharp",
            ".ts" or ".tsx" => "ts",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".java" => "java",
            ".go" => "go",
            ".rs" => "rust",
            ".rb" => "ruby",
            ".c" or ".h" => "c",
            ".cpp" or ".cc" or ".cxx" or ".hpp" => "cpp",
            ".sql" => "sql",
            ".sh" or ".bash" => "bash",
            ".md" => "markdown",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".xml" => "xml",
            _ => null
        };
    }

    private bool TryOpenTextReader(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        if (TryOpenReaderViaMountedFileSystems(uri, out reader))
            return true;

        if (TryOpenReaderFromLocalFile(uri, out reader))
            return true;

        reader = null;
        return false;
    }

    private bool TryOpenReaderViaMountedFileSystems(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        reader = null;
        if (_fileSystem is null)
            return false;

        Stream? stream = null;
        try
        {
            var fileInfo = _fileSystem.GetFile(uri);
            if (!fileInfo.Exists || fileInfo.IsDirectory)
                return false;

            stream = fileInfo.CreateReadStream();
            reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            stream = null;
            return true;
        }
        catch
        {
            stream?.Dispose();
            reader?.Dispose();
            reader = null;
            return false;
        }
    }

    private bool TryOpenReaderFromLocalFile(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        reader = null;

        if (_repoConfig?.Path is null ||
            !string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string absolutePath;
        try
        {
            absolutePath = FileUriPathResolver.ToAbsolutePath(_repoConfig.Path, uri);
            if (!File.Exists(absolutePath))
                return false;
        }
        catch
        {
            return false;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            stream = null;
            return true;
        }
        catch
        {
            stream?.Dispose();
            reader?.Dispose();
            reader = null;
            return false;
        }
    }

    private string ExtractSnippet(TextReader reader, RepoUri uri)
    {
        var lineRange = ResolveLineRange(uri);
        if (lineRange is null)
            return reader.ReadToEnd();

        var (start, end) = lineRange.Value;
        return ReadLineRange(reader, start, end);
    }

    private (int Start, int End)? ResolveLineRange(RepoUri uri)
    {
        if (uri.Loc.Line is { Start: var lineStart, End: var lineEnd } && lineStart is not null)
        {
            var start = Math.Max(1, lineStart.Value);
            var end = lineEnd is null ? start : Math.Max(start, lineEnd.Value);
            return (start, end);
        }

        if (string.IsNullOrWhiteSpace(uri.Loc.Symbol) || _uriRegistry is null)
            return null;

        if (!RepoUri.TryParse(uri.Container.AbsoluteUri, out var fileUri))
            return null;

        if (!_uriRegistry.TryGetValue(fileUri, out var entry))
            return null;

        foreach (var (symbolUri, symbolEntry) in entry.Symbols)
        {
            if (!symbolEntry.HasSpan)
                continue;

            if (string.Equals(symbolUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                return (symbolEntry.StartLine, symbolEntry.EndLine);

            if (!string.IsNullOrWhiteSpace(symbolUri.Loc.Symbol) &&
                string.Equals(symbolUri.Loc.Symbol, uri.Loc.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return (symbolEntry.StartLine, symbolEntry.EndLine);
            }
        }

        return null;
    }

    private static string ReadLineRange(TextReader reader, int startLine, int endLine)
    {
        var lineNumber = 0;
        var lines = new List<string>(Math.Max(1, endLine - startLine + 1));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (lineNumber < startLine)
                continue;

            if (lineNumber > endLine)
                break;

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    public record SnippetGlobRow(string Uri, string Snippet);

    #endregion
}
