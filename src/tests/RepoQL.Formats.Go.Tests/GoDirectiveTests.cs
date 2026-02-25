using AwesomeAssertions;
using RepoQL.Formats.Go.TreeSitter;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoDirectiveTests
{
    [Test]
    public void Parse_Directives_ExtractsCompilerAndConcurrencyKinds()
    {
        using var client = new GoTreeSitterClient();

        var directives = client.Parse(GoTestHelpers.ReadFixture("directives.go")).Directives;
        directives.Should().Contain(d => d.Kind == "build");
        directives.Should().Contain(d => d.Kind == "generate");
        directives.Should().Contain(d => d.Kind == "embed");
        directives.Should().Contain(d => d.Kind == "linkname");

        var concurrency = client.Parse(GoTestHelpers.ReadFixture("concurrency.go")).Directives;
        concurrency.Should().Contain(d => d.Kind == "goroutine");
        concurrency.Should().Contain(d => d.Kind == "channel");
        concurrency.Should().Contain(d => d.Kind == "select");
    }

    [Test]
    public async Task Materialize_Directives_EmitAnnotations()
    {
        var directiveRecords = await GoTestHelpers.LoadRecordsAsync("directives.go");
        directiveRecords.Annotations.Should().Contain(a => a.Kind == "go.build_constraint");
        directiveRecords.Annotations.Should().Contain(a => a.Kind == "go.generate");
        directiveRecords.Annotations.Should().Contain(a => a.Kind == "go.embed");
        directiveRecords.Annotations.Should().Contain(a => a.Kind == "go.linkname");

        var concurrencyRecords = await GoTestHelpers.LoadRecordsAsync("concurrency.go");
        concurrencyRecords.Annotations.Should().Contain(a => a.Kind == "go.goroutine");
        concurrencyRecords.Annotations.Should().Contain(a => a.Kind == "go.channel");
        concurrencyRecords.Annotations.Should().Contain(a => a.Kind == "go.select");
    }
}

