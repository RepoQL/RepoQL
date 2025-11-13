using AwesomeAssertions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

internal class GlobMatchSqlTests
{
    [Test]
    public void GlobMatch_DefaultSchemeMatchesFileUris()
    {
        using var store = new DuckDbGraphStore(":memory:");
        store.EnsureSchema();

        var rows = store.RawQuery("SELECT glob_match('file:///repo/docs/readme.md', 'docs/**/*.md') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void GlobMatch_AllowsCustomScheme()
    {
        using var store = new DuckDbGraphStore(":memory:");
        store.EnsureSchema();

        var rows = store.RawQuery("SELECT glob_match('docs:///repo/docs/help.md', 'docs/**/*.md', default_scheme := 'docs:///') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void GlobMatch_ReturnsNullForBlankInputs()
    {
        using var store = new DuckDbGraphStore(":memory:");
        store.EnsureSchema();

        var rows = store.RawQuery("SELECT glob_match(NULL, 'docs/**/*.md') AS matched").ToList();
        rows.Should().HaveCount(1);
        rows[0]["matched"].Should().BeNull();
    }
}
