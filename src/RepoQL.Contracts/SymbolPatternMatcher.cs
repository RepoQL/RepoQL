namespace RepoQL.Contracts;

/// <summary>
/// Matches symbol names against patterns with wildcards.
///
/// Purpose: Enable filtering nodes by symbol pattern (e.g., "MyClass.*" for direct children,
/// "MyClass.**" for all descendants). Used by fragment matching in URI patterns.
///
/// Complexity: Pattern parsing and hierarchical matching based on dot-separated symbol names.
/// Protected by stateless design and simple string operations.
/// </summary>
public static class SymbolPatternMatcher
{
    /// <summary>
    /// The type of wildcard at the end of a symbol pattern.
    /// </summary>
    public enum WildcardType
    {
        /// <summary>No wildcard - exact match required.</summary>
        None,

        /// <summary>Single star (.*) - matches direct children only.</summary>
        DirectChildren,

        /// <summary>Double star (.**) - matches all descendants.</summary>
        AllDescendants
    }

    /// <summary>
    /// Parses a symbol pattern to extract the base symbol and wildcard type.
    /// </summary>
    /// <param name="pattern">The pattern to parse (e.g., "MyClass.*" or "MyClass.**").</param>
    /// <returns>Tuple of (base symbol without wildcard, wildcard type).</returns>
    public static (string BaseSymbol, WildcardType Wildcard) ParsePattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return (string.Empty, WildcardType.None);

        if (pattern.EndsWith(".**", StringComparison.Ordinal))
            return (pattern[..^3], WildcardType.AllDescendants);

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
            return (pattern[..^2], WildcardType.DirectChildren);

        return (pattern, WildcardType.None);
    }

    /// <summary>
    /// Tests if a qualified symbol name matches a pattern.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name (e.g., "MyClass.Inner.Method").</param>
    /// <param name="pattern">The pattern to match against (e.g., "MyClass.*" or "MyClass.**").</param>
    /// <returns>True if the name matches the pattern, false otherwise.</returns>
    /// <remarks>
    /// Pattern semantics:
    /// - "MyClass" - exact match only
    /// - "MyClass.*" - matches direct children (MyClass.Method, MyClass.Field)
    /// - "MyClass.**" - matches all descendants (MyClass.Method, MyClass.Inner.Method)
    /// </remarks>
    public static bool Matches(string qualifiedName, string pattern)
        => Matches(qualifiedName, pattern, ignoreCase: true);

    /// <summary>
    /// Tests if a qualified symbol name matches a pattern.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name (e.g., "MyClass.Inner.Method").</param>
    /// <param name="pattern">The pattern to match against (e.g., "MyClass.*" or "MyClass.**").</param>
    /// <param name="ignoreCase">Whether to ignore case while matching.</param>
    /// <returns>True if the name matches the pattern, false otherwise.</returns>
    public static bool Matches(string qualifiedName, string pattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(qualifiedName) || string.IsNullOrEmpty(pattern))
            return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var (baseSymbol, wildcard) = ParsePattern(pattern);

        return wildcard switch
        {
            WildcardType.None => IsExactOrSuffixMatch(qualifiedName, baseSymbol, comparison),
            WildcardType.DirectChildren => IsDirectChild(qualifiedName, baseSymbol, comparison),
            WildcardType.AllDescendants => IsDescendant(qualifiedName, baseSymbol, comparison),
            _ => false
        };
    }

    /// <summary>
    /// Tests if a qualified name matches a pattern exactly or by suffix.
    /// Suffix match allows "Method" to match "Namespace.Class.Method".
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name.</param>
    /// <param name="pattern">The pattern to match (without wildcards).</param>
    /// <param name="comparison">String comparison mode used for case sensitivity.</param>
    /// <returns>True if exact match or if qualifiedName ends with ".pattern".</returns>
    private static bool IsExactOrSuffixMatch(string qualifiedName, string pattern, StringComparison comparison)
    {
        // Exact match
        if (qualifiedName.Equals(pattern, comparison))
            return true;

        // Suffix match: qualifiedName ends with ".pattern"
        var suffix = "." + pattern;
        return qualifiedName.EndsWith(suffix, comparison);
    }

    /// <summary>
    /// Tests if a qualified name is a direct child of a parent symbol.
    /// Direct children have exactly one dot-separated component after the parent.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name.</param>
    /// <param name="parent">The parent symbol name.</param>
    /// <param name="comparison">String comparison mode used for case sensitivity.</param>
    /// <returns>True if qualifiedName is a direct child of parent.</returns>
    /// <example>
    /// IsDirectChild("MyClass.Method", "MyClass") → true
    /// IsDirectChild("MyClass.Inner.Method", "MyClass") → false (Inner is in between)
    /// </example>
    private static bool IsDirectChild(string qualifiedName, string parent, StringComparison comparison)
    {
        // Handle empty parent - matches any single-level name
        if (string.IsNullOrEmpty(parent))
            return !qualifiedName.Contains('.', StringComparison.Ordinal);

        return HasHierarchicalMatch(
            qualifiedName,
            parent,
            comparison,
            remainingSegmentCount => remainingSegmentCount == 1);
    }

    /// <summary>
    /// Tests if a qualified name is a descendant of an ancestor symbol.
    /// Descendants are any symbols nested under the ancestor at any depth.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name.</param>
    /// <param name="ancestor">The ancestor symbol name.</param>
    /// <param name="comparison">String comparison mode used for case sensitivity.</param>
    /// <returns>True if qualifiedName is a descendant of ancestor.</returns>
    /// <example>
    /// IsDescendant("MyClass.Method", "MyClass") → true
    /// IsDescendant("MyClass.Inner.Method", "MyClass") → true
    /// IsDescendant("MyClass", "MyClass") → false (not a descendant of itself)
    /// </example>
    private static bool IsDescendant(string qualifiedName, string ancestor, StringComparison comparison)
    {
        // Handle empty ancestor - matches any nested name
        if (string.IsNullOrEmpty(ancestor))
            return qualifiedName.Contains('.', StringComparison.Ordinal);

        return HasHierarchicalMatch(
            qualifiedName,
            ancestor,
            comparison,
            remainingSegmentCount => remainingSegmentCount >= 1);
    }

    /// <summary>
    /// Scans dot-separated segments and looks for the target path at any segment boundary.
    /// When found, delegates to <paramref name="remainingSegmentPredicate"/> to decide if the
    /// number of remaining segments is valid for the wildcard semantics.
    /// </summary>
    private static bool HasHierarchicalMatch(
        string qualifiedName,
        string targetPath,
        StringComparison comparison,
        Func<int, bool> remainingSegmentPredicate)
    {
        var nameSegments = qualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var targetSegments = targetPath.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (nameSegments.Length == 0 || targetSegments.Length == 0 || nameSegments.Length < targetSegments.Length)
            return false;

        var maxStart = nameSegments.Length - targetSegments.Length;
        for (var start = 0; start <= maxStart; start++)
        {
            var matchedTarget = true;
            for (var i = 0; i < targetSegments.Length; i++)
            {
                if (!string.Equals(nameSegments[start + i], targetSegments[i], comparison))
                {
                    matchedTarget = false;
                    break;
                }
            }

            if (!matchedTarget)
                continue;

            var remainingSegments = nameSegments.Length - (start + targetSegments.Length);
            if (remainingSegmentPredicate(remainingSegments))
                return true;
        }

        return false;
    }
}
