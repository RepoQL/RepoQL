using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace RepoQL.Sandbox.Tests;

public sealed class FileBasedModuleRegistryTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly FileBasedModuleRegistry _registry;

    public FileBasedModuleRegistryTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "repoql-module-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        _registry = new FileBasedModuleRegistry(_repoRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    [Test]
    public void Register_WithSourceFile_SucceedsAndWritesManifest()
    {
        WriteSource("@agent/changelog", "export const value = 42;");

        var result = _registry.Register("@agent/changelog");

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();

        var manifest = ReadManifest();
        manifest.Should().HaveCount(1);
        manifest[0]!["identifier"]!.GetValue<string>().Should().Be("@agent/changelog");
        manifest[0]!["specifier"]!.GetValue<string>().Should().Be("repoql:@agent/changelog");
    }

    [Test]
    public void Register_WithoutSourceFile_Fails()
    {
        var result = _registry.Register("@agent/missing");

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("Source file not found", StringComparison.Ordinal));
    }

    [Test]
    public void Remove_RegisteredModule_RemovesFromList()
    {
        WriteSource("@agent/changelog", "export const value = 42;");
        _registry.Register("@agent/changelog").Success.Should().BeTrue();

        var removed = _registry.Remove("@agent/changelog");

        removed.Should().BeTrue();
        _registry.List().Should().BeEmpty();
    }

    [Test]
    public void LoadSource_WithValidSpecifier_ReturnsSource()
    {
        const string source = "export function add(a, b) { return a + b; }";
        WriteSource("@agent/math", source);
        _registry.Register("@agent/math").Success.Should().BeTrue();

        var loaded = _registry.LoadSource("repoql:@agent/math");

        loaded.Should().Be(source);
    }

    [Test]
    public void LoadSource_WithUnknownSpecifier_ReturnsNull()
    {
        var loaded = _registry.LoadSource("repoql:@agent/unknown");

        loaded.Should().BeNull();
    }

    [Test]
    public void CheckHealth_WithExistingSource_IsHealthy()
    {
        WriteSource("@agent/changelog", "export const value = 42;");
        _registry.Register("@agent/changelog").Success.Should().BeTrue();

        var health = _registry.CheckHealth();

        health.Should().ContainSingle();
        health[0].Identifier.Should().Be("@agent/changelog");
        health[0].IsHealthy.Should().BeTrue();
        health[0].Problem.Should().BeNull();
    }

    [Test]
    public void CheckHealth_WithDeletedSource_IsUnhealthy()
    {
        WriteSource("@agent/changelog", "export const value = 42;");
        _registry.Register("@agent/changelog").Success.Should().BeTrue();
        File.Delete(Path.Combine(_repoRoot, ".repoql", "modules", "src", "@agent", "changelog.mjs"));

        var health = _registry.CheckHealth();

        health.Should().ContainSingle();
        health[0].IsHealthy.Should().BeFalse();
        health[0].Problem.Should().Contain("Missing source file");
    }

    [Test]
    public void Register_SourceContainingRepoqlRead_InfersReadCapability()
    {
        WriteSource("@agent/reader", "export async function run() { return repoql.read('file:///src/App.cs'); }");

        _registry.Register("@agent/reader").Success.Should().BeTrue();

        var module = _registry.List().Single();
        module.Capabilities.Reads.Should().BeTrue();
        module.Capabilities.Writes.Should().BeFalse();
        module.Capabilities.Deletes.Should().BeFalse();
    }

    [Test]
    public async Task Register_ConcurrentCalls_DoNotCorruptManifest()
    {
        var identifiers = Enumerable.Range(0, 12)
            .Select(index => $"@agent/module-{index}")
            .ToList();

        foreach (var identifier in identifiers)
            WriteSource(identifier, $"export const name = '{identifier}';");

        await Task.WhenAll(identifiers.Select(identifier => Task.Run(() => _registry.Register(identifier))));

        var listed = _registry.List();
        listed.Should().HaveCount(identifiers.Count);
        listed.Select(module => module.Identifier).Should().BeEquivalentTo(identifiers);

        var manifest = ReadManifest();
        manifest.Should().HaveCount(identifiers.Count);
    }

    private JsonArray ReadManifest()
    {
        var manifestPath = Path.Combine(_repoRoot, ".repoql", "modules", "manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        return JsonNode.Parse(File.ReadAllText(manifestPath)).Should().BeOfType<JsonArray>().Subject;
    }

    private void WriteSource(string identifier, string source)
    {
        var path = Path.Combine(
            _repoRoot,
            ".repoql",
            "modules",
            "src",
            identifier.Replace('/', Path.DirectorySeparatorChar) + ".mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }
}
