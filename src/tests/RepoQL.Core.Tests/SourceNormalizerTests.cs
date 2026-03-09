using RepoQL.Core;

namespace RepoQL.Core.Tests;

internal sealed class SourceNormalizerTests
{
    [Test]
    [Arguments("https://github.com/owner/repo.git", "github.com/owner/repo")]
    [Arguments("https://github.com/owner/repo", "github.com/owner/repo")]
    [Arguments("https://github.com/Owner/Repo.git", "github.com/owner/repo")]
    [Arguments("http://github.com/owner/repo.git", "github.com/owner/repo")]
    [Arguments("ssh://git@github.com/owner/repo.git", "github.com/owner/repo")]
    [Arguments("https://github.com/owner/repo/", "github.com/owner/repo")]
    [Arguments("https://github.com/owner/repo.git/", "github.com/owner/repo")]
    public async Task Normalize_ProtocolUrls_ReturnsCanonicalForm(string input, string expected)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("git@github.com:owner/repo.git", "github.com/owner/repo")]
    [Arguments("git@github.com:owner/repo", "github.com/owner/repo")]
    [Arguments("git@github.com:Owner/Repo.GIT", "github.com/owner/repo")]
    [Arguments("git@gitlab.com:group/subgroup/repo.git", "gitlab.com/group/subgroup/repo")]
    public async Task Normalize_SshScpUrls_ReturnsCanonicalForm(string input, string expected)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://user:pass@github.com/owner/repo.git", "github.com/owner/repo")]
    public async Task Normalize_UrlWithCredentials_StripsCredentials(string input, string expected)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-a-url")]
    [Arguments("ftp://github.com/owner/repo")]
    [Arguments("file:///local/path/to/repo.git")]
    [Arguments("git@:repo.git")]
    public async Task Normalize_InvalidInputs_ReturnsEmptyString(string? input)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo("");
    }

    [Test]
    [Arguments("https://github.com/owner/repo?ref=main", "github.com/owner/repo")]
    [Arguments("https://github.com/owner/repo#readme", "github.com/owner/repo")]
    public async Task Normalize_UrlWithQueryOrFragment_StripsExtras(string input, string expected)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("git@ssh.dev.azure.com:v3/org/project/repo", "ssh.dev.azure.com/v3/org/project/repo")]
    [Arguments("https://dev.azure.com/org/project/_git/repo", "dev.azure.com/org/project/_git/repo")]
    public async Task Normalize_AzureDevOps_PreservesFullPath(string input, string expected)
    {
        await Assert.That(SourceNormalizer.Normalize(input)).IsEqualTo(expected);
    }
}
