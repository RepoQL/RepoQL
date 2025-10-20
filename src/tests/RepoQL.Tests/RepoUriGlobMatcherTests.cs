using AwesomeAssertions;
using RepoQL.Core;

namespace RepoQL.Tests;

internal class RepoUriGlobMatcherTests
{
    [Test]
    public void MatchesSingleSegmentWildcard()
    {
        var uri = "file:///repo/src/App.cs";
        var pattern = "file:///repo/src/*.cs";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void MatchesDoubleStarAcrossDirectories()
    {
        var uri = "file:///repo/src/app/components/Button.tsx";
        var pattern = "src/**/*.tsx";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void RespectsCaseSensitivityToggle()
    {
        var uri = "file:///repo/DOCS/README.md";
        var pattern = "file:///repo/docs/README.md";

        RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase: true).Should().BeTrue();
        RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase: false).Should().BeFalse();
    }

    [Test]
    public void HonorsExplicitScheme()
    {
        var uri = "embed:///repo/graphs/model.json";
        var pattern = "embed:///repo/graphs/*.json";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void ReturnsNullForBlankInputs()
    {
        RepoUriGlobMatcher.IsMatch(null, "src/**/*.md").Should().BeNull();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/app.py", "  ").Should().BeNull();
    }

    [Test]
    public void SupportsCharacterClassRanges()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bar.cs", "src/b[a-c]r.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bzr.cs", "src/b[a-c]r.cs").Should().BeFalse();
    }

    [Test]
    public void SupportsNegatedCharacterClasses()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/var.cs", "src/[!b]ar.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bar.cs", "src/[!b]ar.cs").Should().BeFalse();
    }

    [Test]
    public void EscapedWildcardTreatedLiterally()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/docs/notes*.md", @"docs/notes\*.md").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/docs/notestar.md", @"docs/notes\*.md").Should().BeFalse();
    }

    [Test]
    public void LeadingSlashAnchorsToRepositoryRoot()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/main.cs", "/src/*.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/app/src/main.cs", "/src/*.cs").Should().BeFalse();
    }

    [Test]
    public void DoubleStarMatchesDirectoriesAndFiles()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/index.js", "assets/**/index.js").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/lib/index.js", "assets/**/index.js").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/lib/util.js", "assets/**/index.js").Should().BeFalse();
    }
}
