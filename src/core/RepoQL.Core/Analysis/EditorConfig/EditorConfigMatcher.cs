namespace RepoQL.Core.Analysis.EditorConfig;

internal static class EditorConfigMatcher
{
    public static bool Matches(string repoRelativePath, IEnumerable<string> patterns)
    {
        return patterns.Any(pattern => GlobMatcher.IsMatch(repoRelativePath, pattern));
    }
}
