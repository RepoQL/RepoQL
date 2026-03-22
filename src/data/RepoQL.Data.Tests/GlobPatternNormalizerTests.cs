using RepoQL.Contracts;

namespace RepoQL.Data.Tests;

public class GlobPatternNormalizerTests
{
    // Use a consistent repo root for tests
    // On Windows: C:/Source/RepoQL
    // On Unix: /home/user/repo
    private static readonly string WindowsRepoRoot = "C:/Source/RepoQL";
    private static readonly string UnixRepoRoot = "/home/user/repo";

    #region Windows Absolute URIs

    [Test]
    public async Task NormalizePattern_WindowsAbsoluteUri_ConvertsToRelative()
    {
        var pattern = "file:///C:/Source/RepoQL/src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_WindowsAbsoluteUri_DifferentCase_ConvertsToRelative()
    {
        // Windows paths are case-insensitive
        var pattern = "file:///c:/source/repoql/src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        // On Windows, this should match; on Unix, it won't (which is correct)
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
        }
        else
        {
            // On Unix, the case mismatch means it's not under the repo root
            await Assert.That(result).IsEqualTo("file:///c:/source/repoql/src/**/*.cs");
        }
    }

    #endregion

    #region Bare Windows Paths

    [Test]
    public async Task NormalizePattern_BareWindowsPath_WithForwardSlashes_ConvertsToRelative()
    {
        var pattern = "C:/Source/RepoQL/src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_BareWindowsPath_WithBackslashes_ConvertsToRelative()
    {
        var pattern = @"C:\Source\RepoQL\src\**\*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
    }

    #endregion

    #region Unix Absolute Paths

    [Test]
    public async Task NormalizePattern_UnixAbsolutePath_ConvertsToRelative()
    {
        var pattern = "/home/user/repo/src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, UnixRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_UnixAbsolutePath_CaseSensitive_DoesNotMatch()
    {
        // Unix paths are case-sensitive
        var pattern = "/home/user/Repo/src/**/*.cs"; // Capital R
        var result = GlobPatternNormalizer.NormalizePattern(pattern, UnixRepoRoot);

        // On Unix, this should NOT match (different case)
        // On Windows, case doesn't matter for path comparison
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
        }
        else
        {
            await Assert.That(result).IsEqualTo("/home/user/Repo/src/**/*.cs");
        }
    }

    #endregion

    #region Semicolon-Delimited Patterns

    [Test]
    public async Task NormalizePattern_SemicolonDelimited_NormalizesAllParts()
    {
        var pattern = "C:/Source/RepoQL/src/**;C:/Source/RepoQL/lib/**";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**;file:///lib/**");
    }

    [Test]
    public async Task NormalizePattern_MixedAbsoluteAndRelative_NormalizesOnlyAbsolute()
    {
        var pattern = "src/**;C:/Source/RepoQL/tests/**";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("src/**;file:///tests/**");
    }

    #endregion

    #region Negative Patterns

    [Test]
    public async Task NormalizePattern_NegativePattern_PreservesExclamation()
    {
        var pattern = "!C:/Source/RepoQL/tests/**";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("!file:///tests/**");
    }

    [Test]
    public async Task NormalizePattern_MixedWithNegatives_NormalizesCorrectly()
    {
        var pattern = "C:/Source/RepoQL/src/**;!C:/Source/RepoQL/src/tests/**";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**;!file:///src/tests/**");
    }

    #endregion

    #region Already Relative Patterns

    [Test]
    public async Task NormalizePattern_AlreadyRelative_RemainsUnchanged()
    {
        var pattern = "src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("src/**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_FileUriRelative_RemainsUnchanged()
    {
        var pattern = "file:///src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/**/*.cs");
    }

    #endregion

    #region Paths Outside Repo

    [Test]
    public async Task NormalizePattern_PathOutsideRepo_RemainsUnchanged()
    {
        var pattern = "C:/Other/Project/src/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        // Path is not under repo root, so it remains unchanged (with backslashes normalized)
        await Assert.That(result).IsEqualTo("C:/Other/Project/src/**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_UnixPathOutsideRepo_RemainsUnchanged()
    {
        var pattern = "/var/log/**/*.log";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, UnixRepoRoot);

        await Assert.That(result).IsEqualTo("/var/log/**/*.log");
    }

    #endregion

    #region Null/Empty Handling

    [Test]
    public async Task NormalizePattern_Null_ReturnsNull()
    {
        var result = GlobPatternNormalizer.NormalizePattern(null, WindowsRepoRoot);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task NormalizePattern_Empty_ReturnsEmpty()
    {
        var result = GlobPatternNormalizer.NormalizePattern("", WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task NormalizePattern_Whitespace_ReturnsWhitespace()
    {
        var result = GlobPatternNormalizer.NormalizePattern("   ", WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("   ");
    }

    #endregion

    #region Fragments

    [Test]
    public async Task NormalizePattern_WithSymbolFragment_PreservesFragment()
    {
        var pattern = "C:/Source/RepoQL/src/App.cs#symbol=Foo.Bar";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/App.cs#symbol=Foo.Bar");
    }

    [Test]
    public async Task NormalizePattern_WithLineFragment_PreservesFragment()
    {
        var pattern = "C:/Source/RepoQL/src/App.cs#line=10,20";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/App.cs#line=10,20");
    }

    [Test]
    public async Task NormalizePattern_WithCombinedFragment_PreservesFragment()
    {
        var pattern = "C:/Source/RepoQL/src/App.cs#symbol=Foo&line=10,20";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/App.cs#symbol=Foo&line=10,20");
    }

    #endregion

    #region Glob Wildcards

    [Test]
    public async Task NormalizePattern_DoubleStarWildcard_Preserved()
    {
        var pattern = "C:/Source/RepoQL/**/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///**/*.cs");
    }

    [Test]
    public async Task NormalizePattern_SingleStarWildcard_Preserved()
    {
        var pattern = "C:/Source/RepoQL/src/*.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/*.cs");
    }

    [Test]
    public async Task NormalizePattern_QuestionMarkWildcard_Preserved()
    {
        var pattern = "C:/Source/RepoQL/src/?.cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/?.cs");
    }

    [Test]
    public async Task NormalizePattern_BracketWildcard_Preserved()
    {
        var pattern = "C:/Source/RepoQL/src/[abc].cs";
        var result = GlobPatternNormalizer.NormalizePattern(pattern, WindowsRepoRoot);

        await Assert.That(result).IsEqualTo("file:///src/[abc].cs");
    }

    #endregion

    #region ContainsAbsolutePath

    [Test]
    public async Task ContainsAbsolutePath_WindowsUri_ReturnsTrue()
    {
        var result = GlobPatternNormalizer.ContainsAbsolutePath("file:///C:/repo/src/*.cs");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ContainsAbsolutePath_BareWindowsPath_ReturnsTrue()
    {
        var result = GlobPatternNormalizer.ContainsAbsolutePath("C:/repo/src/*.cs");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ContainsAbsolutePath_UnixHomePath_ReturnsTrue()
    {
        var result = GlobPatternNormalizer.ContainsAbsolutePath("/home/user/repo/src/*.cs");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ContainsAbsolutePath_RelativePath_ReturnsFalse()
    {
        var result = GlobPatternNormalizer.ContainsAbsolutePath("src/**/*.cs");
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ContainsAbsolutePath_Null_ReturnsFalse()
    {
        var result = GlobPatternNormalizer.ContainsAbsolutePath(null);
        await Assert.That(result).IsFalse();
    }

    #endregion
}
