using AwesomeAssertions;
using RepoQL.Commands;

namespace RepoQL.Cli.Tests.CommandFramework;

/// <summary>
/// Purpose: Verify command parser correctly distinguishes commands from SQL and handles all syntax variants.
/// Complexity: Pure parsing — no DI, no side effects.
/// </summary>
internal sealed class CommandParserTests
{
    [Test]
    public void PlainSql_ReturnsNull()
    {
        CommandParser.TryParse("SELECT 1").Should().BeNull();
    }

    [Test]
    public void EmptyInput_ReturnsNull()
    {
        CommandParser.TryParse("").Should().BeNull();
        CommandParser.TryParse(null).Should().BeNull();
        CommandParser.TryParse("   ").Should().BeNull();
    }

    [Test]
    public void LegacyDiagnostics_ReturnsNull()
    {
        // :diagnostics: is not :: syntax
        CommandParser.TryParse(":diagnostics:").Should().BeNull();
    }

    [Test]
    public void DoubleColonAlone_ReturnsNull()
    {
        CommandParser.TryParse("::").Should().BeNull();
    }

    [Test]
    public void SimpleCommand_ParsesName()
    {
        var result = CommandParser.TryParse("::cmd");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().BeEmpty();
        result.IsHelp.Should().BeFalse();
    }

    [Test]
    public void CommandWithOneParam_Parses()
    {
        var result = CommandParser.TryParse("::cmd[hello]");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().HaveCount(1);
        result.Parameters[0].Should().Be("hello");
    }

    [Test]
    public void CommandWithMultipleParams_TrimsWhitespace()
    {
        var result = CommandParser.TryParse("::cmd[a, b , c]");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().HaveCount(3);
        result.Parameters[0].Should().Be("a");
        result.Parameters[1].Should().Be("b");
        result.Parameters[2].Should().Be("c");
    }

    [Test]
    public void EmptyBrackets_NoParams()
    {
        var result = CommandParser.TryParse("::cmd[]");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void UnclosedBracket_ReturnsParseError()
    {
        var result = CommandParser.TryParse("::cmd[unclosed");
        result.Should().NotBeNull();
        result!.ParseError.Should().NotBeNull();
    }

    [Test]
    public void DashPrefixPreserved()
    {
        var result = CommandParser.TryParse("::cmd[-verbose]");
        result.Should().NotBeNull();
        result!.Parameters[0].Should().Be("-verbose");
    }

    [Test]
    public void DottedName_Parses()
    {
        var result = CommandParser.TryParse("::mcp.newrelic[x]");
        result.Should().NotBeNull();
        result!.Name.Should().Be("mcp.newrelic");
        result.Parameters.Should().HaveCount(1);
        result.Parameters[0].Should().Be("x");
    }

    [Test]
    public void DottedNameNoParams_Parses()
    {
        var result = CommandParser.TryParse("::mcp.newrelic");
        result.Should().NotBeNull();
        result!.Name.Should().Be("mcp.newrelic");
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void HelpFlag_Detected()
    {
        var result = CommandParser.TryParse("::diagnostics.fast --help");
        result.Should().NotBeNull();
        result!.Name.Should().Be("diagnostics.fast");
        result.Parameters.Should().BeEmpty();
        result.IsHelp.Should().BeTrue();
    }

    [Test]
    public void SqlCast_NotIntercepted()
    {
        // DuckDB :: cast is always infix: expr::type
        CommandParser.TryParse("SELECT 'x'::VARCHAR").Should().BeNull();
    }

    [Test]
    public void LeadingWhitespace_Trimmed()
    {
        var result = CommandParser.TryParse("  ::cmd  ");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
    }

    [Test]
    public void CommandWithDiagnosticsName_Parses()
    {
        var result = CommandParser.TryParse("::diagnostics");
        result.Should().NotBeNull();
        result!.Name.Should().Be("diagnostics");
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void DiagnosticsFast_Parses()
    {
        var result = CommandParser.TryParse("::diagnostics.fast");
        result.Should().NotBeNull();
        result!.Name.Should().Be("diagnostics.fast");
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void DiagnosticsMemory_Parses()
    {
        var result = CommandParser.TryParse("::diagnostics.memory");
        result.Should().NotBeNull();
        result!.Name.Should().Be("diagnostics.memory");
        result.Parameters.Should().BeEmpty();
    }

    [Test]
    public void HelpFlagAfterBrackets_IsDetected()
    {
        var result = CommandParser.TryParse("::cmd[] --help");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().BeEmpty();
        result.IsHelp.Should().BeTrue();
    }

    [Test]
    public void HelpFlagAfterBracketsWithParams_IsDetected()
    {
        var result = CommandParser.TryParse("::cmd[value] --help");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cmd");
        result.Parameters.Should().ContainSingle().Which.Should().Be("value");
        result.IsHelp.Should().BeTrue();
    }

    [Test]
    public void ParamsWithMixedWhitespace_AreTrimmed()
    {
        var result = CommandParser.TryParse("::cmd[\t alpha \n,  \r\nbeta\t ,   gamma   ]");
        result.Should().NotBeNull();
        result!.Parameters.Should().Equal(["alpha", "beta", "gamma"]);
    }

    [Test]
    public void ParamsWithSpecialCharacters_ArePreserved()
    {
        var result = CommandParser.TryParse("::cmd[path=C:\\temp\\file.txt, https://example.com/a?x=1&y=2, #tag]");
        result.Should().NotBeNull();
        result!.Parameters.Should().Equal(["path=C:\\temp\\file.txt", "https://example.com/a?x=1&y=2", "#tag"]);
    }

    [Test]
    public void ParamsContainingBrackets_AreParsedAsLiteralText()
    {
        var result = CommandParser.TryParse("::cmd[a[b]c, x[y]]");
        result.Should().NotBeNull();
        result!.Parameters.Should().Equal(["a[b]c", "x[y]"]);
    }

    [Test]
    public void UnicodeCommandNameAndParams_ParseSuccessfully()
    {
        var result = CommandParser.TryParse("::café[naïve, 東京]");
        result.Should().NotBeNull();
        result!.Name.Should().Be("café");
        result.Parameters.Should().Equal(["naïve", "東京"]);
    }

    [Test]
    public void InvalidCharacterInCommandName_ReturnsParseError()
    {
        var result = CommandParser.TryParse("::bad!name[param]");
        result.Should().NotBeNull();
        result!.ParseError.Should().NotBeNull();
        result.ParseError.Should().Contain("Invalid character");
    }

    [Test]
    public void VeryLongInput_ParsesWithoutLosingData()
    {
        var longParam = new string('x', 10_000);
        var result = CommandParser.TryParse($"::cmd[{longParam}]");
        result.Should().NotBeNull();
        result!.Parameters.Should().ContainSingle();
        result.Parameters[0].Length.Should().Be(10_000);
    }
}
