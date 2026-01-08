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
    {
        if (string.IsNullOrEmpty(qualifiedName) || string.IsNullOrEmpty(pattern))
            return false;

        var (baseSymbol, wildcard) = ParsePattern(pattern);

        return wildcard switch
        {
            WildcardType.None => qualifiedName.Equals(baseSymbol, StringComparison.OrdinalIgnoreCase),
            WildcardType.DirectChildren => IsDirectChild(qualifiedName, baseSymbol),
            WildcardType.AllDescendants => IsDescendant(qualifiedName, baseSymbol),
            _ => false
        };
    }

    /// <summary>
    /// Tests if a qualified name is a direct child of a parent symbol.
    /// Direct children have exactly one dot-separated component after the parent.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name.</param>
    /// <param name="parent">The parent symbol name.</param>
    /// <returns>True if qualifiedName is a direct child of parent.</returns>
    /// <example>
    /// IsDirectChild("MyClass.Method", "MyClass") → true
    /// IsDirectChild("MyClass.Inner.Method", "MyClass") → false (Inner is in between)
    /// </example>
    private static bool IsDirectChild(string qualifiedName, string parent)
    {
        // Handle empty parent - matches any single-level name
        if (string.IsNullOrEmpty(parent))
            return !qualifiedName.Contains('.', StringComparison.Ordinal);

        // Must start with "parent."
        var prefix = parent + ".";
        if (!qualifiedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Suffix must not contain any more dots (exactly one level deep)
        var suffix = qualifiedName[prefix.Length..];
        return suffix.Length > 0 && !suffix.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Tests if a qualified name is a descendant of an ancestor symbol.
    /// Descendants are any symbols nested under the ancestor at any depth.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified symbol name.</param>
    /// <param name="ancestor">The ancestor symbol name.</param>
    /// <returns>True if qualifiedName is a descendant of ancestor.</returns>
    /// <example>
    /// IsDescendant("MyClass.Method", "MyClass") → true
    /// IsDescendant("MyClass.Inner.Method", "MyClass") → true
    /// IsDescendant("MyClass", "MyClass") → false (not a descendant of itself)
    /// </example>
    private static bool IsDescendant(string qualifiedName, string ancestor)
    {
        // Handle empty ancestor - matches any nested name
        if (string.IsNullOrEmpty(ancestor))
            return qualifiedName.Contains('.', StringComparison.Ordinal);

        // Must start with "ancestor." to be a descendant
        var prefix = ancestor + ".";
        return qualifiedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && qualifiedName.Length > prefix.Length;
    }
}
