using AwesomeAssertions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

internal class MatchesGlobDebugTest
{
    [Test]
    public void Debug_BasicPatternBehavior()
    {
        using var store = new DuckDbDataStore(":memory:");

        // Test basic single pattern matching
        var rows1 = store.Query("SELECT matches_glob('file:///src/App.cs', 'src/**/*.cs', TRUE, 'file:///') AS matched").ToList();
        var basicMatch = rows1[0]["matched"];

        // Test glob_match for comparison
        var rows2 = store.Query("SELECT glob_match('file:///src/App.cs', 'src/**/*.cs') AS matched").ToList();
        var globMatch = rows2[0]["matched"];

        // Test matches_glob with empty pattern
        var rows3 = store.Query("SELECT matches_glob('file:///src/App.cs', '', TRUE, 'file:///') AS matched").ToList();
        var emptyMatch = rows3[0]["matched"];

        // Force failure to show all values
        throw new Exception($@"DEBUG VALUES:
matches_glob('file:///src/App.cs', 'src/**/*.cs', ...) = {basicMatch} (type: {basicMatch?.GetType()?.Name ?? "null"})
glob_match('file:///src/App.cs', 'src/**/*.cs') = {globMatch} (type: {globMatch?.GetType()?.Name ?? "null"})
matches_glob('file:///src/App.cs', '', ...) = {emptyMatch} (type: {emptyMatch?.GetType()?.Name ?? "null"})
");
    }
}
