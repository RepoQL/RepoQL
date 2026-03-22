using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.Ruby;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyVisibilityTests
{
    [Test]
    public async Task VisibilityStateMachine_AppliesBareAndTargetedModifiers()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("visibility.rb", ReadFixture("visibility_modifiers.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var methods = records.Nodes.Where(n => n.Kind == "rb.member").ToList();
        methods.Single(m => m.Props["name"]!.ToString() == "open_method")
            .Props["accessibility"]!
            .ToString()
            .Should()
            .Be("protected");
        methods.Single(m => m.Props["name"]!.ToString() == "private_method")
            .Props["accessibility"]!
            .ToString()
            .Should()
            .Be("private");
    }

    [Test]
    public async Task VisibilityStateMachine_IsScopedToNestedTypes()
    {
        const string source = """
        class Outer
          private
          def outer_secret
          end

          class Inner
            def inner_public
            end
          end
        end
        """;

        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("nested_visibility.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var outerSecret = records.Nodes.Single(n => n.Kind == "rb.member" && n.Props["name"]!.ToString() == "outer_secret");
        outerSecret.Props["accessibility"]!.ToString().Should().Be("private");

        var innerPublic = records.Nodes.Single(n => n.Kind == "rb.member" && n.Props["name"]!.ToString() == "inner_public");
        innerPublic.Props["accessibility"]!.ToString().Should().Be("public");
    }

    [Test]
    public async Task Structure_UsesVisibilitySymbolsAndBlockMarkers()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("visibility_modifiers.rb") + Environment.NewLine + ReadFixture("block_accepting_methods.rb");
        using var artifactScope = CreateArtifact("structure.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        artifact.Structure.Should().Contain("#open_method(");
        artifact.Structure.Should().Contain("-private_method(");
        artifact.Structure.Should().Contain("around(&block)");
        artifact.Structure.Should().Contain("#symbol=around");
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ruby_visibility_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName}")
            },
            tempDir,
            provider);
    }

    private sealed class ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider) : IDisposable
    {
        public DiscoveredArtifact Artifact { get; } = artifact;
        private readonly string _tempDir = tempDir;
        private readonly IFileProvider _provider = provider;

        public void Dispose()
        {
            (_provider as IDisposable)?.Dispose();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
