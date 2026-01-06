using System.Text;
using System.Text.Json;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for formatting URI lists as ASCII tree structures.
/// Purpose: Provides visual tree representation of file/document hierarchies for quick codebase overview.
/// Complexity: URI parsing and tree building logic; isolated to this class.
/// </summary>
[UdfClass]
public class TreeUdf
{
    private const string Branch = "├── ";
    private const string LastBranch = "└── ";
    private const string Vertical = "│   ";
    private const string Space = "    ";

    /// <summary>
    /// Formats a list of URIs as an ASCII tree grouped by scheme with folder counts.
    /// </summary>
    /// <param name="urisJson">JSON array of URI strings, e.g. ["file:///src/a.cs", "docs:///readme.md"]</param>
    /// <returns>ASCII tree string with box-drawing characters</returns>
    [ScalarUdf("_tree_internal", MacroName = "tree", IsPure = true,
        Description = "Format URIs as ASCII tree grouped by scheme with folder counts")]
    public string FormatTree(string urisJson)
    {
        if (string.IsNullOrWhiteSpace(urisJson))
            return string.Empty;

        var uris = ParseUriArray(urisJson);
        if (uris.Count == 0)
            return string.Empty;

        var byScheme = GroupByScheme(uris);

        var sb = new StringBuilder();
        var schemeList = byScheme.OrderBy(kv => kv.Key).ToList();

        for (var i = 0; i < schemeList.Count; i++)
        {
            var (scheme, paths) = schemeList[i];
            var tree = BuildTree(paths);
            var isLastScheme = i == schemeList.Count - 1;
            RenderTree(sb, scheme, tree, isLastScheme);
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

    private static Dictionary<string, List<string>> GroupByScheme(List<string> uris)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var uri in uris)
        {
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
                paths = new List<string>();
                result[scheme] = paths;
            }

            paths.Add(path);
        }

        return result;
    }

    private static TreeNode BuildTree(List<string> paths)
    {
        var root = new TreeNode { Name = "" };

        foreach (var path in paths)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                    // Count this file in the parent folder
                    current.FileCount++;
                }

                current = child;
            }
        }

        return root;
    }

    private static void RenderTree(StringBuilder sb, string scheme, TreeNode root, bool isLastScheme)
    {
        sb.AppendLine(scheme);

        var children = root.Children
            .OrderBy(kv => kv.Value.IsFile) // Folders first
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < children.Count; i++)
        {
            var isLast = i == children.Count - 1;
            RenderNode(sb, children[i].Value, "", isLast);
        }

        // Add blank line between schemes (but not after last)
        if (!isLastScheme)
            sb.AppendLine();
    }

    private static void RenderNode(StringBuilder sb, TreeNode node, string prefix, bool isLast)
    {
        var branch = isLast ? LastBranch : Branch;
        var name = node.Name;

        // Add folder indicator and count for non-file nodes with children
        if (!node.IsFile && node.Children.Count > 0)
        {
            name += "/";
            if (node.FileCount > 0)
            {
                var fileWord = node.FileCount == 1 ? "file" : "files";
                name += $" ({node.FileCount} {fileWord})";
            }
        }
        else if (!node.IsFile && node.Children.Count == 0)
        {
            // Empty folder
            name += "/";
        }

        sb.Append(prefix);
        sb.Append(branch);
        sb.AppendLine(name);

        // Render children
        var childPrefix = prefix + (isLast ? Space : Vertical);
        var children = node.Children
            .OrderBy(kv => kv.Value.IsFile) // Folders first
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < children.Count; i++)
        {
            var childIsLast = i == children.Count - 1;
            RenderNode(sb, children[i].Value, childPrefix, childIsLast);
        }
    }

    private class TreeNode
    {
        public required string Name { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new();
        public int FileCount { get; set; }
        public bool IsFile { get; set; }
    }
}
