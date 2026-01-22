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
/// Normalizes absolute paths in patterns to repo-relative URIs before matching (when repo root is available).
/// All functions are null-safe with three-valued logic.
/// </summary>
[UdfClass]
public class GlobMatchUdf
{
    private readonly string? _repoRoot;

    /// <summary>
    /// Creates a GlobMatchUdf with repository configuration for absolute path normalization.
    /// </summary>
    public GlobMatchUdf(RepositoryConfiguration repoConfig)
    {
        _repoRoot = repoConfig.Path;
    }

    /// <summary>
    /// Creates a GlobMatchUdf without repository configuration.
    /// Absolute paths in patterns will not be normalized.
    /// </summary>
    public GlobMatchUdf()
    {
        _repoRoot = null;
    }

    /// <summary>
    /// Matches a URI against a single glob pattern.
    /// Returns NULL for invalid inputs (three-valued logic).
    /// Normalizes absolute paths in the pattern to repo-relative URIs.
    /// </summary>
    [ScalarUdf("repoql_glob_match", IsPure = true)]
    public string? GlobMatch(
        string? uri,
        string? pattern,
        [UdfDefault("true")] bool ignoreCase,
        [UdfDefault("NULL")] string? defaultScheme)
    {
        // Normalize pattern to convert absolute paths to repo-relative (when repo root is available)
        var normalizedPattern = _repoRoot != null
            ? GlobPatternNormalizer.NormalizePattern(pattern, _repoRoot)
            : pattern;

        var matched = RepoUriGlobMatcher.IsMatch(uri, normalizedPattern, ignoreCase, defaultScheme);
        if (matched is null)
            return null;

        return matched.Value ? "true" : "false";
    }

    /// <summary>
    /// Matches a URI against a pattern specification with advanced features.
    /// Supports semicolon-delimited patterns, negative patterns with ! prefix,
    /// and fragment patterns like #symbol=MyClass.* and #line=10,*.
    /// Returns TRUE for NULL/blank patterns (matches everything).
    /// Normalizes absolute paths in patterns to repo-relative URIs.
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

        // Normalize pattern to convert absolute paths to repo-relative (when repo root is available)
        var normalizedPattern = _repoRoot != null
            ? GlobPatternNormalizer.NormalizePattern(patternSpec, _repoRoot)
            : patternSpec;

        var matched = UriPatternMatcher.Matches(uri, normalizedPattern, ignoreCase, defaultScheme ?? "file:///");
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
