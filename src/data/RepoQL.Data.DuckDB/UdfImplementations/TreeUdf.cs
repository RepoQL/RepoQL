using System.Text;
using System.Text.Json;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for formatting URI lists as ASCII tree structures.
/// Purpose: Provides visual tree representation of file/document hierarchies for quick codebase overview.
/// Complexity: URI parsing, tree building, and progressive disclosure logic (full vs folders-only);
/// isolated to this class. The rest of the system just calls FormatTree with the foldersOnly flag.
/// </summary>
[UdfClass]
public class TreeUdf
{
    private const string Branch = "├── ";
    private const string LastBranch = "└── ";
    private const string Vertical = "│   ";
    private const string Space = "    ";

    /// <summary>
    /// Formats a list of URIs as an ASCII tree grouped by scheme with optional headlines.
    /// </summary>
    /// <param name="urisJson">JSON array of URI strings, e.g. ["file:///src/a.cs", "help:///readme.md"]</param>
    /// <param name="headlinesJson">JSON array of headline strings (aligned with urisJson), e.g. ["Foo | 10 ln", null]</param>
    /// <param name="foldersOnly">If true, shows only folders with file type counts (e.g., "src/ (3 cs, 2 json)")</param>
    /// <returns>ASCII tree string with box-drawing characters</returns>
    [ScalarUdf("_tree_internal", MacroName = "tree", IsPure = true,
        Description = "Format URIs as ASCII tree grouped by scheme with optional headlines")]
    public string FormatTree(
        string urisJson,
        string headlinesJson,
        [UdfDefault("false")] bool foldersOnly)
    {
        if (string.IsNullOrWhiteSpace(urisJson))
            return string.Empty;

        var uris = ParseUriArray(urisJson);
        if (uris.Count == 0)
            return string.Empty;

        var headlines = ParseHeadlineArray(headlinesJson);
        if (headlines.Count < uris.Count)
        {
            while (headlines.Count < uris.Count)
                headlines.Add(null);
        }
        else if (headlines.Count > uris.Count)
        {
            headlines = headlines.Take(uris.Count).ToList();
        }

        var entries = new List<TreeEntry>(uris.Count);
        for (var i = 0; i < uris.Count; i++)
        {
            entries.Add(new TreeEntry(uris[i], headlines[i]));
        }

        if (entries.Count == 0)
            return string.Empty;

        var byScheme = GroupByScheme(entries);
        var showHeadlines = headlines.Any(h => !string.IsNullOrWhiteSpace(h));

        var sb = new StringBuilder();
        var schemeList = byScheme.OrderBy(kv => kv.Key).ToList();

        for (var i = 0; i < schemeList.Count; i++)
        {
            var (scheme, paths) = schemeList[i];
            var tree = BuildTree(paths);
            var isLastScheme = i == schemeList.Count - 1;
            RenderTree(sb, scheme, tree, isLastScheme, foldersOnly, showHeadlines);
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> ParseUriArray(string input)
    {
        var result = new List<string>();

        // Try JSON format first: ["uri1", "uri2"]
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var uri = element.GetString();
                    if (!string.IsNullOrWhiteSpace(uri))
                        result.Add(uri);
                }

                return result;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON - try DuckDB array format
        }

        // Try DuckDB array format: [uri1, uri2] (no quotes around strings)
        if (input.StartsWith('[') && input.EndsWith(']'))
        {
            var inner = input[1..^1].Trim();
            if (string.IsNullOrWhiteSpace(inner))
                return result;

            // Split by comma, but handle potential commas in URIs (unlikely but possible)
            var parts = inner.Split(',');
            foreach (var part in parts)
            {
                var uri = part.Trim().Trim('\'', '"');
                if (!string.IsNullOrWhiteSpace(uri))
                    result.Add(uri);
            }
        }

        return result;
    }

    private static Dictionary<string, List<TreeEntry>> GroupByScheme(List<TreeEntry> entries)
    {
        var result = new Dictionary<string, List<TreeEntry>>();

        foreach (var entry in entries)
        {
            var uri = entry.Uri;
            var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
                continue;

            var rawPath = uri[(schemeEnd + 3)..];

            // For file:// URIs, the path typically starts with / (e.g., file:///path)
            // Include the leading / in the scheme for display (file:///)
            var scheme = uri[..(schemeEnd + 3)]; // "file://"
            if (rawPath.StartsWith('/'))
            {
                scheme += "/"; // Make it "file:///"
            }

            // Normalize path separators and remove leading slash
            var path = rawPath.Replace('\\', '/').TrimStart('/');

            if (!result.TryGetValue(scheme, out var paths))
            {
                paths = new List<TreeEntry>();
                result[scheme] = paths;
            }

            paths.Add(new TreeEntry(path, entry.Headline));
        }

        return result;
    }

    private static TreeNode BuildTree(List<TreeEntry> paths)
    {
        var root = new TreeNode { Name = "" };

        foreach (var entry in paths)
        {
            var segments = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isFile = i == segments.Length - 1;

                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new TreeNode { Name = segment, IsFile = isFile };
                    current.Children[segment] = child;
                }

                if (isFile)
                {
                    child.IsFile = true;
                    if (!string.IsNullOrWhiteSpace(entry.Headline) && string.IsNullOrWhiteSpace(child.Headline))
                        child.Headline = entry.Headline;
                    // Count this file in the parent folder
                    current.FileCount++;

                    // Track extension for type-aware folder summaries
                    var ext = GetExtension(segment);
                    if (!string.IsNullOrEmpty(ext))
                    {
                        current.FilesByExtension.TryGetValue(ext, out var count);
                        current.FilesByExtension[ext] = count + 1;
                    }
                    else
                    {
                        // Files without extension
                        current.FilesByExtension.TryGetValue("(no ext)", out var count);
                        current.FilesByExtension["(no ext)"] = count + 1;
                    }
                }

                current = child;
            }
        }

        return root;
    }

    /// <summary>
    /// Extract file extension without the dot, in lowercase.
    /// </summary>
    private static string GetExtension(string filename)
    {
        var dotIndex = filename.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == filename.Length - 1)
            return string.Empty;
        return filename[(dotIndex + 1)..].ToLowerInvariant();
    }

    private static void RenderTree(StringBuilder sb, string scheme, TreeNode root, bool isLastScheme, bool foldersOnly, bool showHeadlines)
    {
        sb.AppendLine(scheme);

        // Filter children based on mode
        var children = root.Children
            .Where(kv => !foldersOnly || !kv.Value.IsFile) // In folders-only mode, skip files at root
            .OrderBy(kv => !kv.Value.IsFile) // Files first, then folders
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasFolders = children.Any(kv => !kv.Value.IsFile);
        for (var i = 0; i < children.Count; i++)
        {
            var isLast = i == children.Count - 1;
            RenderNode(sb, children[i].Value, "", isLast, foldersOnly, showHeadlines, hasFolders);
        }

        // Add blank line between schemes (but not after last)
        if (!isLastScheme)
            sb.AppendLine();
    }

    private static void RenderNode(StringBuilder sb, TreeNode node, string prefix, bool isLast, bool foldersOnly, bool showHeadlines, bool parentHasFolders)
    {
        var name = node.Name;

        // Add folder indicator and counts for non-file nodes with children
        if (!node.IsFile && node.Children.Count > 0)
        {
            name += "/";
            if (node.FileCount > 0)
            {
                // Show type counts (e.g., "3 cs, 2 sql")
                name += FormatTypeCounts(node);
            }
        }
        else if (!node.IsFile && node.Children.Count == 0)
        {
            // Empty folder
            name += "/";
        }

        // For files with headlines, use the full headline as the display name
        if (node.IsFile && !string.IsNullOrWhiteSpace(node.Headline))
        {
            name = node.Headline;
        }

        sb.Append(prefix);

        // Folders get box-drawing branch (├── or └──)
        // Files get │ if parent has subfolders (to connect visually), otherwise just spaces
        if (node.IsFile)
        {
            sb.Append(parentHasFolders ? Vertical : Space);
        }
        else
        {
            sb.Append(isLast ? LastBranch : Branch);
        }

        sb.AppendLine(name);

        // Render children (skip files in folders-only mode)
        var childPrefix = prefix + (isLast ? Space : Vertical);
        var children = node.Children
            .Where(kv => !foldersOnly || !kv.Value.IsFile)
            .OrderBy(kv => !kv.Value.IsFile) // Files first, then folders
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasFolders = children.Any(kv => !kv.Value.IsFile);
        for (var i = 0; i < children.Count; i++)
        {
            var childIsLast = i == children.Count - 1;
            RenderNode(sb, children[i].Value, childPrefix, childIsLast, foldersOnly, showHeadlines, hasFolders);
        }
    }

    /// <summary>
    /// Format file counts by extension (e.g., " (3 cs, 2 json, 1 md)").
    /// </summary>
    private static string FormatTypeCounts(TreeNode node)
    {
        if (node.FilesByExtension.Count == 0)
            return string.Empty;

        // Sort by count descending, then by extension
        var parts = node.FilesByExtension
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Value} {kv.Key}")
            .ToList();

        return $" ({string.Join(", ", parts)})";
    }

    private class TreeNode
    {
        public required string Name { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new();
        public int FileCount { get; set; }
        public bool IsFile { get; set; }
        public string? Headline { get; set; }
        /// <summary>
        /// Counts of direct child files by extension (e.g., "cs" -> 3, "json" -> 2).
        /// Only populated for folder nodes. Extension is lowercase without dot.
        /// </summary>
        public Dictionary<string, int> FilesByExtension { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TreeEntry(string Uri, string? Headline)
    {
        public string Path => Uri;
    }

    private static List<string?> ParseHeadlineArray(string input)
    {
        var result = new List<string?>();
        if (string.IsNullOrWhiteSpace(input))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Null)
                    {
                        result.Add(null);
                        continue;
                    }

                    var headline = element.GetString();
                    result.Add(string.IsNullOrWhiteSpace(headline) ? null : headline);
                }
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
}
