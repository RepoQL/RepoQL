using AwesomeAssertions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

internal class MatchesGlobSqlTests
{
    // === Single Pattern ===

    [Test]
    public void MatchesGlob_SinglePattern_Matches()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Pattern matches anywhere in path, so URI needs prefix before pattern directory
        var rows = store.Query("SELECT matches_glob('file:///repo/src/App.cs', 'src/**/*.cs') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void MatchesGlob_SinglePattern_NoMatch()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/src/App.txt', 'src/**/*.cs') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeFalse();
    }

    // === Multiple Patterns (OR) ===

    [Test]
    public void MatchesGlob_MultiplePatterns_MatchesFirst()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/src/App.cs', 'src/**;lib/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void MatchesGlob_MultiplePatterns_MatchesSecond()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/lib/Helper.cs', 'src/**;lib/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void MatchesGlob_MultiplePatterns_NoMatch()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/other/File.cs', 'src/**;lib/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeFalse();
    }

    // === Negative Patterns ===

    [Test]
    public void MatchesGlob_NegativePattern_Excludes()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Matches src/** but excluded by !src/tests/**
        var rows = store.Query("SELECT matches_glob('file:///repo/src/tests/Test.cs', 'src/**;!src/tests/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeFalse();
    }

    [Test]
    public void MatchesGlob_NegativePattern_DoesNotExcludeOther()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Matches src/** and not excluded
        var rows = store.Query("SELECT matches_glob('file:///repo/src/App.cs', 'src/**;!src/tests/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    // === Only Negatives ===

    [Test]
    public void MatchesGlob_OnlyNegatives_MatchesNonExcluded()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/src/App.cs', '!**/*.md') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void MatchesGlob_OnlyNegatives_ExcludesMatch()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///repo/docs/readme.md', '!**/*.md') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeFalse();
    }

    // === Blank = Everything ===

    [Test]
    public void MatchesGlob_BlankPattern_MatchesEverything()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///anything', '') AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    [Test]
    public void MatchesGlob_NullPattern_ReturnsNull_DueToShortCircuiting()
    {
        // DuckDB short-circuits NULL parameters - the function is never called.
        // Use COALESCE(pattern, '') to convert NULL to empty string if you want "match everything" behavior.
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///anything', NULL) AS matched").ToList();
        rows.Should().HaveCount(1);
        rows[0]["matched"].Should().BeNull(); // DuckDB returns NULL without calling the function
    }

    [Test]
    public void MatchesGlob_NullPatternWithCoalesce_MatchesEverything()
    {
        // Use COALESCE to convert NULL to empty string for "match everything" behavior
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob('file:///anything', COALESCE(NULL, '')) AS matched").ToList();
        rows.Should().HaveCount(1);
        Convert.ToBoolean(rows[0]["matched"]).Should().BeTrue();
    }

    // === Three-Valued Logic ===

    [Test]
    public void MatchesGlob_NullUri_ReturnsNull()
    {
        using var store = new DuckDbDataStore(":memory:");

        var rows = store.Query("SELECT matches_glob(NULL, 'src/**') AS matched").ToList();
        rows.Should().HaveCount(1);
        rows[0]["matched"].Should().BeNull();
    }

    // === glob_files macro ===

    [Test]
    public void GlobFiles_ReturnsMatchingDocuments()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Insert test documents
        store.ExecuteRaw("""
            INSERT INTO node (id, kind, uri, container_uri_lowercase, properties, created_at, updated_at)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'document', 'file:///repo/src/App.cs', 'file:///repo/src/app.cs', '{}', NOW(), NOW()),
                ('22222222-2222-2222-2222-222222222222', 'document', 'file:///repo/src/tests/Test.cs', 'file:///repo/src/tests/test.cs', '{}', NOW(), NOW()),
                ('33333333-3333-3333-3333-333333333333', 'document', 'file:///repo/lib/Helper.cs', 'file:///repo/lib/helper.cs', '{}', NOW(), NOW()),
                ('44444444-4444-4444-4444-444444444444', 'document', 'file:///repo/docs/readme.md', 'file:///repo/docs/readme.md', '{}', NOW(), NOW())
            """);

        var rows = store.Query("SELECT * FROM glob_files('src/**')").ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r["uri"]?.ToString()).Should().Contain("file:///repo/src/App.cs");
        rows.Select(r => r["uri"]?.ToString()).Should().Contain("file:///repo/src/tests/Test.cs");
    }

    [Test]
    public void GlobFiles_WithNegativePattern_ExcludesMatch()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Insert test documents
        store.ExecuteRaw("""
            INSERT INTO node (id, kind, uri, container_uri_lowercase, properties, created_at, updated_at)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'document', 'file:///repo/src/App.cs', 'file:///repo/src/app.cs', '{}', NOW(), NOW()),
                ('22222222-2222-2222-2222-222222222222', 'document', 'file:///repo/src/tests/Test.cs', 'file:///repo/src/tests/test.cs', '{}', NOW(), NOW()),
                ('33333333-3333-3333-3333-333333333333', 'document', 'file:///repo/lib/Helper.cs', 'file:///repo/lib/helper.cs', '{}', NOW(), NOW())
            """);

        var rows = store.Query("SELECT * FROM glob_files('src/**;!src/tests/**')").ToList();
        rows.Should().HaveCount(1);
        rows[0]["uri"]?.ToString().Should().Be("file:///repo/src/App.cs");
    }

    [Test]
    public void GlobFiles_BlankPattern_ReturnsAll()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Insert test documents
        store.ExecuteRaw("""
            INSERT INTO node (id, kind, uri, container_uri_lowercase, properties, created_at, updated_at)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'document', 'file:///repo/src/App.cs', 'file:///repo/src/app.cs', '{}', NOW(), NOW()),
                ('22222222-2222-2222-2222-222222222222', 'document', 'file:///repo/docs/readme.md', 'file:///repo/docs/readme.md', '{}', NOW(), NOW())
            """);

        var rows = store.Query("SELECT * FROM glob_files('')").ToList();
        rows.Should().HaveCount(2);
    }

    [Test]
    public void GlobFiles_OnlyNegatives_ReturnsNonMatching()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Insert test documents
        store.ExecuteRaw("""
            INSERT INTO node (id, kind, uri, container_uri_lowercase, properties, created_at, updated_at)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'document', 'file:///repo/src/App.cs', 'file:///repo/src/app.cs', '{}', NOW(), NOW()),
                ('22222222-2222-2222-2222-222222222222', 'document', 'file:///repo/docs/readme.md', 'file:///repo/docs/readme.md', '{}', NOW(), NOW())
            """);

        var rows = store.Query("SELECT * FROM glob_files('!**/*.md')").ToList();
        rows.Should().HaveCount(1);
        rows[0]["uri"]?.ToString().Should().Be("file:///repo/src/App.cs");
    }
}
