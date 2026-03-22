using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Python.Tests;

public sealed class PythonAnnotationTests
{
    [Test]
    public async Task LoadAndMaterialize_MetaprogrammingHintsEmitExpectedAnnotations()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("metaprogramming.py", ReadFixture("metaprogramming.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var metaprogramming = records.Annotations
            .Where(a => a.Kind == PythonConstants.AnnotationKinds.Metaprogramming)
            .ToArray();

        metaprogramming.Should().NotBeEmpty();
        metaprogramming.Should().Contain(a => a.RuleId == "__getattr__");
        metaprogramming.Should().Contain(a => a.RuleId == "__getattr___module");
        metaprogramming.Should().Contain(a => a.RuleId == "__dir___module");
        metaprogramming.Should().Contain(a => a.RuleId == "exec");
        metaprogramming.Should().Contain(a => a.RuleId == "eval");
        metaprogramming.Should().Contain(a => a.RuleId == "type_dynamic_class");
        metaprogramming.Should().Contain(a => a.RuleId == "setattr");
        metaprogramming.Should().Contain(a => a.RuleId == "__import__");
        metaprogramming.Should().Contain(a => a.RuleId == "importlib.import_module");

        metaprogramming.Single(a => a.RuleId == "__getattr__").Message
            .Should().Be("dynamic attribute access, graph may be incomplete");
        metaprogramming.Single(a => a.RuleId == "__getattr___module").Message
            .Should().Be("dynamic module attribute access (PEP 562), graph may be incomplete");
        metaprogramming.Single(a => a.RuleId == "__dir___module").Message
            .Should().Be("module customizes dir(), discoverable API surface may differ from static graph");
        metaprogramming.Single(a => a.RuleId == "exec").Message
            .Should().Be("dynamic code execution detected");

        metaprogramming.Should().OnlyContain(a =>
            a.Severity == "info"
            && a.Source == "repoql.formats.python");
    }

    [Test]
    public async Task LoadAndMaterialize_FrameworkHintsEmitExpectedAnnotations()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("framework_django_model.py", ReadFixture("framework_django_model.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var framework = records.Annotations
            .Where(a => a.Kind == PythonConstants.AnnotationKinds.Framework)
            .ToArray();

        framework.Should().NotBeEmpty();
        framework.Should().Contain(a => a.RuleId == "django_field");
        framework.Should().Contain(a => a.RuleId == "sqlalchemy_column");
        framework.Should().Contain(a => a.RuleId == "pydantic_field");

        framework.Should().Contain(a =>
            a.RuleId == "django_field"
            && a.Message.Contains("models.CharField", StringComparison.Ordinal));
        framework.Should().Contain(a =>
            a.RuleId == "sqlalchemy_column"
            && a.Message.Contains("db.Column", StringComparison.Ordinal));
        framework.Should().Contain(a =>
            a.RuleId == "pydantic_field"
            && a.Message.Contains("Field(", StringComparison.Ordinal));

        framework.Should().OnlyContain(a =>
            a.Severity == "info"
            && a.Source == "repoql.formats.python"
            && a.Data["confidence"]!.GetValue<string>() == "medium");
    }

    [Test]
    public async Task LoadAndMaterialize_AnnotationSourcesSetWhenAnnotationsExist()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("metaprogramming.py", ReadFixture("metaprogramming.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Annotations.Should().NotBeEmpty();
        records.AnnotationSources.Should().ContainSingle().Which.Should().Be("repoql.formats.python");
    }

    [Test]
    public async Task LoadAndMaterialize_AnnotationSpansMapToExpectedLocations()
    {
        using var loader = new PythonLoader();
        using var metaprogrammingScope = CreateArtifact("metaprogramming.py", ReadFixture("metaprogramming.py"));
        using var frameworkScope = CreateArtifact("framework_django_model.py", ReadFixture("framework_django_model.py"));

        var metaprogrammingDocument = await loader.LoadAsync(metaprogrammingScope.Artifact);
        var metaprogrammingRecords = loader.Materialize(metaprogrammingDocument);
        AssertAnnotationLine(metaprogrammingRecords, PythonConstants.AnnotationKinds.Metaprogramming, "__getattr___module", 4);
        AssertAnnotationLine(metaprogrammingRecords, PythonConstants.AnnotationKinds.Metaprogramming, "__dir___module", 9);
        AssertAnnotationLine(metaprogrammingRecords, PythonConstants.AnnotationKinds.Metaprogramming, "__getattr__", 15);
        AssertAnnotationLine(metaprogrammingRecords, PythonConstants.AnnotationKinds.Metaprogramming, "exec", 20);

        var frameworkDocument = await loader.LoadAsync(frameworkScope.Artifact);
        var frameworkRecords = loader.Materialize(frameworkDocument);
        AssertAnnotationLine(frameworkRecords, PythonConstants.AnnotationKinds.Framework, "django_field", 5, "models.CharField");
        AssertAnnotationLine(frameworkRecords, PythonConstants.AnnotationKinds.Framework, "sqlalchemy_column", 7, "db.Column");
        AssertAnnotationLine(frameworkRecords, PythonConstants.AnnotationKinds.Framework, "pydantic_field", 8, "Field(");
    }

    private static void AssertAnnotationLine(
        Records records,
        string kind,
        string ruleId,
        int expectedLine,
        string? messageContains = null)
    {
        var annotation = records.Annotations.Single(a =>
            a.Kind == kind
            && a.RuleId == ruleId
            && (messageContains is null || a.Message.Contains(messageContains, StringComparison.Ordinal)));

        annotation.TargetSpanId.Should().NotBeNull();
        var span = records.Spans.Single(s => s.Id == annotation.TargetSpanId!.Value);
        span.StartLine.Should().NotBeNull();
        span.EndLine.Should().NotBeNull();
        span.StartByte.Should().NotBeNull();
        span.EndByte.Should().NotBeNull();
        span.StartLine!.Value.Should().Be(expectedLine);
        span.EndLine!.Value.Should().BeGreaterThanOrEqualTo(span.StartLine.Value);
        span.EndByte!.Value.Should().BeGreaterThanOrEqualTo(span.StartByte!.Value);
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_python_annotations_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName.Replace('\\', '/')}")
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
