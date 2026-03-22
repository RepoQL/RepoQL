using AwesomeAssertions;
using RepoQL.Formats.Rust.TreeSitter;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustConcurrentParsingTests
{
    [Test]
    public async Task Parse_DifferentSources_ThreadSafeWithEightThreads()
    {
        using var client = new RustTreeSitterClient();
        var sources = new[]
        {
            FixtureReader.Read("simple_struct.rs"),
            FixtureReader.Read("enum_with_variants.rs"),
            FixtureReader.Read("trait_definition.rs"),
            FixtureReader.Read("impl_blocks.rs"),
            FixtureReader.Read("visibility_modifiers.rs"),
            FixtureReader.Read("use_declarations.rs"),
            FixtureReader.Read("async_functions.rs"),
            FixtureReader.Read("malformed.rs")
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => client.Parse(sources[i])))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(8);
        results[0].Structs.Should().Contain(s => s.Name == "User");
        results[1].Enums.Should().Contain(e => e.Name == "Message");
        results[2].Traits.Should().Contain(t => t.Name == "Storage");
        results[3].ImplBlocks.Should().Contain(i => i.TargetType == "Cache");
        results[4].Structs.Should().Contain(s => s.Name == "PublicType");
        results[5].UseDeclarations.Should().HaveCountGreaterThanOrEqualTo(3);
        results[6].Functions.Should().Contain(f => f.Name == "fetch_data" && f.IsAsync);
        results[7].ErrorNodeCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Parse_SameSource_ThreadSafeWithEightThreads()
    {
        using var client = new RustTreeSitterClient();
        var source = FixtureReader.Read("simple_struct.rs");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => client.Parse(source)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var baseline = results[0];

        results.Should().AllSatisfy(result =>
        {
            result.Structs.Select(s => s.Name).Should().BeEquivalentTo(baseline.Structs.Select(s => s.Name));
            result.Enums.Select(e => e.Name).Should().BeEquivalentTo(baseline.Enums.Select(e => e.Name));
            result.Traits.Select(t => t.Name).Should().BeEquivalentTo(baseline.Traits.Select(t => t.Name));
            result.Functions.Select(f => f.Name).Should().BeEquivalentTo(baseline.Functions.Select(f => f.Name));
            result.UseDeclarations.Select(u => u.Path).Should().BeEquivalentTo(baseline.UseDeclarations.Select(u => u.Path));
            result.ErrorNodeCount.Should().Be(baseline.ErrorNodeCount);
        });
    }
}