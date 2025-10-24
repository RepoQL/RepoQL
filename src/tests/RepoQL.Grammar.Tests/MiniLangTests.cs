using AwesomeAssertions;
using RepoQL.Grammar.Runner;

namespace RepoQL.Grammar.Tests;

// Minimal nodes for testing

// Pidgin-based tiny language: let <id> = <int> ; (repeated)

internal class MiniLangTests
{
    [Test]
    public Task DuplicateVar_IsFlagged()
    {
        var src = "let x = 1;\nlet x = 2;\n";
        var lang = new MiniLang();
        var rules = new RuleSet(new DuplicateVarRule());
        var diags = LintRunner.LintFile(lang, src, "mem://mini", rules).ToList();

        diags.Count.Should().Be(1);
        diags[0].Message.Should().Contain("Duplicate 'x'");
        return Task.CompletedTask;
    }
}
