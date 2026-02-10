using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.Ruby;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyMetaprogrammingTests
{
    [Test]
    public async Task Materialize_CreatesPropertyNodesAndGeneratedAccessorMethods()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("metaprogramming.rb", ReadFixture("metaprogramming.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var properties = records.Nodes.Where(n => n.Kind == "rb.property").ToArray();
        properties.Should().HaveCount(4);
        properties.Should().Contain(n => n.Props["name"]!.ToString() == "token" && n.Props["accessor_type"]!.ToString() == "reader");
        properties.Should().Contain(n => n.Props["name"]!.ToString() == "password" && n.Props["accessor_type"]!.ToString() == "writer");
        properties.Should().Contain(n => n.Props["name"]!.ToString() == "name" && n.Props["accessor_type"]!.ToString() == "accessor");
        properties.Should().Contain(n => n.Props["name"]!.ToString() == "email" && n.Props["accessor_type"]!.ToString() == "accessor");

        var generatedAccessors = records.Nodes
            .Where(n => n.Kind == "rb.member" && n.Props["generator"] is not null)
            .Where(n => n.Props["generator"]!.ToString().StartsWith("attr_", StringComparison.Ordinal))
            .ToArray();
        generatedAccessors.Should().HaveCount(6);
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "token");
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "password=");
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "name");
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "name=");
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "email");
        generatedAccessors.Should().Contain(n => n.Props["name"]!.ToString() == "email=");
        generatedAccessors.Should().OnlyContain(n => n.Props["is_generated"]!.ToString() == "true");
        generatedAccessors.Should().OnlyContain(n => n.Props["kind"]!.ToString() == "method");
        generatedAccessors.Should().OnlyContain(n => n.Props["is_static"]!.GetValue<bool>() == false);
        generatedAccessors.Should().OnlyContain(n => n.Props["accessibility"]!.ToString() == "private");

        var account = records.Nodes.Single(n => n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Account");
        foreach (var property in properties)
        {
            records.Edges.Should().Contain(e =>
                e.Type == "HAS_PART" &&
                e.IsComposition &&
                e.SrcId == account.Id &&
                e.DstId == property.Id);
        }
    }

    [Test]
    public async Task Materialize_CreatesDelegateScopeAndLiteralDefineMethodMembers()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("metaprogramming.rb", ReadFixture("metaprogramming.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var generated = records.Nodes
            .Where(n => n.Kind == "rb.member" && n.Props["is_generated"]?.ToString() == "true")
            .ToArray();

        generated.Should().Contain(n =>
            n.Props["name"]!.ToString() == "profile_name" &&
            n.Props["generator"]!.ToString() == "delegate" &&
            n.Props["delegate_to"]!.ToString() == "profile");
        generated.Should().Contain(n =>
            n.Props["name"]!.ToString() == "profile_age" &&
            n.Props["generator"]!.ToString() == "delegate" &&
            n.Props["delegate_to"]!.ToString() == "profile");
        generated.Should().Contain(n =>
            n.Props["name"]!.ToString() == "active" &&
            n.Props["generator"]!.ToString() == "scope" &&
            n.Props["is_static"]!.GetValue<bool>());
        generated.Should().Contain(n =>
            n.Props["name"]!.ToString() == "display_name" &&
            n.Props["generator"]!.ToString() == "define_method");
        generated.Should().Contain(n =>
            n.Props["name"]!.ToString() == "legacy_code" &&
            n.Props["generator"]!.ToString() == "define_method");

        var structure = records.Artifacts.Single().Structure;
        structure.Should().Contain("~name (attr_accessor)");
    }

    [Test]
    public async Task Materialize_CreatesRailsAssociationsValidationsAndCallbacks()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("rails_model.rb", ReadFixture("rails_model.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var model = records.Nodes.Single(n => n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "User");
        var associates = records.Edges.Where(e => e.Type == "ASSOCIATES" && e.SrcId == model.Id).ToArray();
        associates.Should().HaveCount(3);
        associates.Should().OnlyContain(e => !e.IsComposition && e.DstId == null);
        associates.Should().Contain(e => e.Props!["association"]!.ToString() == "has_many" && e.Props["target"]!.ToString() == "posts");
        associates.Should().Contain(e => e.Props!["association"]!.ToString() == "belongs_to" && e.Props["target"]!.ToString() == "account");
        associates.Should().Contain(e => e.Props!["association"]!.ToString() == "has_one" && e.Props["target"]!.ToString() == "profile");

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var validations = records.Annotations.Where(a => a.Kind == "ruby.validation").ToArray();
        validations.Should().ContainSingle();
        validations[0].ScopeDocumentId.Should().Be(documentNode.Id);
        validations[0].RuleId.Should().Be("email");
        validations[0].Message.Should().Contain("validates :email");
        validations[0].Data["options"]!.ToString().Should().Contain("presence: true");
        validations[0].TargetSpanId.Should().NotBeNull();
        records.Spans.Should().Contain(s => s.Id == validations[0].TargetSpanId);

        var callbacks = records.Annotations.Where(a => a.Kind == "ruby.callback").ToArray();
        callbacks.Should().HaveCount(2);
        callbacks.Should().Contain(a => a.RuleId == "before_action" && a.Message == "normalize_email");
        callbacks.Should().Contain(a => a.RuleId == "after_action" && a.Message == "audit_changes");
        callbacks.Single(a => a.RuleId == "before_action").Data["options"]!.ToString().Should().Contain("only:");

        var structure = records.Artifacts.Single().Structure;
        structure.Should().Contain("has_many :posts");
        structure.Should().Contain("validates :email");
    }

    [Test]
    public async Task Materialize_AnnotatesUnextractableMetaprogrammingPatterns()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("unextractable_metaprogramming.rb", ReadFixture("unextractable_metaprogramming.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var metaprogramming = records.Annotations.Where(a => a.Kind == "ruby.metaprogramming").ToArray();
        metaprogramming.Should().HaveCount(5);
        metaprogramming.Should().Contain(a => a.Message == "dynamic method definition detected, name not extractable");
        metaprogramming.Should().Contain(a => a.Message == "class_eval detected, definitions not extractable");
        metaprogramming.Should().Contain(a => a.Message == "module_eval detected, definitions not extractable");
        metaprogramming.Should().Contain(a => a.Message == "instance_eval detected, definitions not extractable");
        metaprogramming.Should().Contain(a => a.Message == "method_missing defined, dynamic dispatch possible");
        metaprogramming.Should().OnlyContain(a => a.TargetSpanId != null);

        var generatedDynamicName = records.Nodes
            .Where(n => n.Kind == "rb.member")
            .Where(n => n.Props["generator"] is not null && n.Props["generator"]!.ToString() == "define_method")
            .Where(n => n.Props["name"] is not null && n.Props["name"]!.ToString() == "runtime_name")
            .ToArray();
        generatedDynamicName.Should().BeEmpty();
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ruby_meta_{Guid.NewGuid():N}");
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
