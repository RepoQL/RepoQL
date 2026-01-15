using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for glob pattern matching on repository URIs and symbols.
///
/// Purpose: Provides SQL-callable functions for matching URIs against glob patterns
/// and symbol names against wildcard patterns.
///
/// Complexity: Delegates to RepoUriGlobMatcher, UriPatternMatcher, and SymbolPatternMatcher.
/// All functions are pure and null-safe with three-valued logic.
/// </summary>
[UdfClass]
public class GlobMatchUdf
{
    /// <summary>
    /// Matches a URI against a single glob pattern.
    /// Returns NULL for invalid inputs (three-valued logic).
    /// </summary>
    [ScalarUdf("repoql_glob_match", IsPure = true)]
    public string? GlobMatch(
        string? uri,
        string? pattern,
        [UdfDefault("true")] bool ignoreCase,
        [UdfDefault("NULL")] string? defaultScheme)
    {
        var matched = RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase, defaultScheme);
        if (matched is null)
            return null;

        return matched.Value ? "true" : "false";
    }

    /// <summary>
    /// Matches a URI against a pattern specification with advanced features.
    /// Supports semicolon-delimited patterns, negative patterns with ! prefix,
    /// and fragment patterns like #symbol=MyClass.* and #line=10,*.
    /// Returns TRUE for NULL/blank patterns (matches everything).
    /// </summary>
    [ScalarUdf("repoql_matches_glob", IsPure = true)]
    public string? MatchesGlob(
        string? uri,
        string? patternSpec,
        [UdfDefault("true")] bool ignoreCase,
        [UdfDefault("'file:///'")]string? defaultScheme)
    {
        // Handle NULL/blank URI - return NULL (three-valued logic)
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        // Handle NULL/blank pattern - return TRUE (matches everything)
        if (string.IsNullOrWhiteSpace(patternSpec))
            return "true";

        var matched = UriPatternMatcher.Matches(uri, patternSpec, ignoreCase, defaultScheme ?? "file:///");
        if (matched is null)
            return null;

        return matched.Value ? "true" : "false";
    }

    /// <summary>
    /// Matches a qualified symbol name against a pattern with wildcards.
    /// Supports: "MyClass" (exact), "MyClass.*" (direct children), "MyClass.**" (all descendants).
    /// Returns NULL for NULL/blank inputs (three-valued logic).
    /// </summary>
    [ScalarUdf("symbol_matches", IsPure = true)]
    public string? SymbolMatches(string? qualifiedName, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName) || string.IsNullOrWhiteSpace(pattern))
            return null;

        var matched = SymbolPatternMatcher.Matches(qualifiedName, pattern);
        return matched ? "true" : "false";
    }
}
