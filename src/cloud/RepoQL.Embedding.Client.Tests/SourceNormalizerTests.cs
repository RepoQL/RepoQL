using AwesomeAssertions;
using RepoQL.Embedding.Client;

namespace RepoQL.Embedding.Client.Tests;

public sealed class SourceNormalizerTests
{
    [Test]
    [Arguments("https://github.com/org/repo.git", "github.com/org/repo")]
    [Arguments("git@github.com:org/repo.git", "github.com/org/repo")]
    [Arguments("github://org/repo", "github.com/org/repo")]
    [Arguments("ssh://git@github.com/org/repo", "github.com/org/repo")]
    [Arguments("https://user:token@github.com/org/repo.git", "github.com/org/repo")]
    [Arguments("https://gitlab.com/org/repo", "gitlab.com/org/repo")]
    [Arguments("https://bitbucket.org/org/repo", "bitbucket.org/org/repo")]
    [Arguments("https://dev.azure.com/org/proj/_git/repo", "dev.azure.com/org/proj/_git/repo")]
    [Arguments("https://gitea.company.com/team/repo", "gitea.company.com/team/repo")]
    [Arguments("file:///local/path", "")]
    public async Task Normalize_ReturnsExpectedCanonicalSource(string input, string expected)
    {
        var result = SourceNormalizer.Normalize(input);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_ReturnsEmptyString_ForNullInput()
    {
        var result = SourceNormalizer.Normalize(null);

        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Normalize_ReturnsEmptyString_ForEmptyInput()
    {
        var result = SourceNormalizer.Normalize("");

        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Normalize_ReturnsEmptyString_ForWhitespaceInput()
    {
        var result = SourceNormalizer.Normalize("   ");

        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    [Arguments(" HTTPS://GITHUB.COM/Org/Repo.GIT/ ", "github.com/org/repo")]
    [Arguments("ssh://user:secret@GITHUB.com/Org/Repo.git/", "github.com/org/repo")]
    [Arguments("git@gitea.company.com:team/platform/repo.git", "gitea.company.com/team/platform/repo")]
    [Arguments("github://Org/Team/Repo.git/", "github.com/org/team/repo")]
    [Arguments("https://dev.azure.com/org/proj/_git/repo.git", "dev.azure.com/org/proj/_git/repo")]
    [Arguments("github.com:org/repo.git", "github.com/org/repo")]
    public async Task Normalize_HandlesAdditionalEdgeCases(string input, string expected)
    {
        var result = SourceNormalizer.Normalize(input);

        await Assert.That(result).IsEqualTo(expected);
    }
}
