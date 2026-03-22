using RepoQL.Data.DuckDB;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Configuration;
using RepoQL.Formats.DotNet;
using RepoQL.Core;
using AnalysisResult = RepoQL.Contracts.Analysis.AnalysisResult;

namespace RepoQL.Tests;

[Timeout(180_000)] // 3 minutes - Roslyn workspace operations can be slow in CI
internal sealed class CSharpLoaderTests
{
    [Test]
    public async Task Loader_Materializes_Types_And_Members(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var sample = """
using System.Threading.Tasks;

namespace Demo.Core.Services;

public class PaymentService
{
    private readonly int _seed;

    public PaymentService(int seed)
    {
        _seed = seed;
    }

    public async Task<int> ExecuteAsync(string payload)
    {
        await Task.Delay(10);
        return _seed + payload.Length;
    }
}
""";

        await File.WriteAllTextAsync(tempPath, sample);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/PaymentService.cs")
        };

        (await loader.CanLoadAsync(artifact)).Should().BeTrue();

        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Length.Should().Be(1);
        records.Nodes.Any(n => n.Kind == "csharp.type").Should().BeTrue();
        records.Nodes.Any(n => n.Kind == "csharp.member").Should().BeTrue();

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["language"]!.GetValue<string>().Should().Be("csharp");

        records.Edges.Any(e => e.Type == "HAS_PART" && e.SrcId == documentNode.Id).Should().BeTrue();
        records.Spans.Any().Should().BeTrue();

        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    [Test]
    public async Task Loader_Produces_Namespace_Type_Member_Graph(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var sample = """
        namespace MyApp.Core;

        public interface IOperation { }

        public partial class Calculator : IOperation
        {
            public int Add(int left, int right) => left + right;
        }
        """;

        await File.WriteAllTextAsync(tempPath, sample);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Calculator.cs")
        };

        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");

        // Namespaces are no longer materialized as nodes - they are stored as properties on types
        records.Nodes.Where(n => n.Kind == "csharp.namespace").Should().BeEmpty("namespaces are not materialized as nodes");

        var typeCandidates = records.Nodes
            .Where(n => n.Kind == "csharp.type")
            .Select(n => n.Props["qualified_name"]?.ToString() ?? "<null>")
            .ToArray();
        typeCandidates.Should().NotBeEmpty($"available types: {string.Join(", ", typeCandidates)}");
        var typeNode = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["qualified_name"]!.ToString().Contains("Calculator", StringComparison.Ordinal));
        typeNode.Should().NotBeNull($"available types: {string.Join(", ", typeCandidates)}");
        var typeNodeValue = typeNode!;
        typeNodeValue.Props["namespace"]!.ToString().Should().Be("MyApp.Core");
        typeNodeValue.Props["extends"]!.ToString().Should().Contain("IOperation");
        bool.Parse(typeNodeValue.Props["is_partial"]!.ToString()).Should().BeTrue();
        typeNodeValue.Props["symbol_key"].Should().NotBeNull();

        var interfaceCandidates = records.Nodes
            .Where(n => n.Kind == "csharp.type" && n.Props["kind"]!.ToString() == "interface")
            .Select(n => n.Props["qualified_name"]?.ToString() ?? "<null>")
            .ToArray();
        interfaceCandidates.Should().NotBeEmpty("no interface nodes were materialized");
        var interfaceNode = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["kind"]!.ToString() == "interface");
        interfaceNode.Should().NotBeNull($"interfaces: {string.Join(", ", interfaceCandidates)}");
        var interfaceNodeValue = interfaceNode!;
        interfaceNodeValue.Props["symbol_key"].Should().NotBeNull();

        var memberNode = records.Nodes.First(n => n.Kind == "csharp.member" && n.Props["name"]!.ToString() == "Add");
        memberNode.Props["name"]!.ToString().Should().Be("Add");
        memberNode.Props["accessibility"]!.ToString().Should().Be("public");
        memberNode.Props["parameters"]!.Should().NotBeNull();
        var parameters = (JsonArray)memberNode.Props["parameters"]!;
        parameters.Count.Should().Be(2);
        parameters[0]!["name"]!.ToString().Should().Be("left");
        memberNode.Props["symbol_key"].Should().NotBeNull();

        // Document -> Type composition (no namespace node in between)
        records.Edges.Count(e => e.IsComposition && e.SrcId == docNode.Id && e.DstId == typeNodeValue.Id).Should().BeGreaterThanOrEqualTo(1);
        records.Edges.Count(e => e.IsComposition && e.SrcId == typeNodeValue.Id && e.DstId == memberNode.Id).Should().Be(1);

        var usesSymbolEdges = records.Edges.Where(e => e.Type == "USES_SYMBOL").ToArray();
        usesSymbolEdges.Length.Should().BeGreaterThan(0);
        var interfaceEdge = usesSymbolEdges.First(e => e.DstId == interfaceNodeValue.Id);
        interfaceEdge.SrcSpanId.Should().NotBeNull();
        interfaceEdge.Props["symbol_key"].Should().NotBeNull();

        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    [Test]
    public async Task Loader_Emits_Multiple_UsesSymbol_Edges(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var sample = """
        namespace Sample;

        public class MathOps
        {
            public int Double(int value) => value * 2;
            public int Triple(int value) => value * 3;
        }

        public class Runner
        {
            public int Execute(int input)
            {
                var ops = new MathOps();
                return ops.Double(input) + ops.Triple(input);
            }
        }
        """;

        await File.WriteAllTextAsync(tempPath, sample);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Runner.cs")
        };

        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        var usesSymbolEdges = records.Edges.Where(e => e.Type == "USES_SYMBOL").ToArray();
        usesSymbolEdges.Length.Should().BeGreaterThanOrEqualTo(2);
        usesSymbolEdges.All(e => e.Props["symbol_key"] is not null).Should().BeTrue();

        try
        {
            File.Delete(tempPath);
        }
        catch { }
    }

    [Test]
    public async Task Analyzer_Reports_Syntax_Diagnostics(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var code = """
        public class Foo
        {
            public void Bar()
            {
                int x = "oops";
            }
        }
        """;

        var document = await LoadCSharpDocumentAsync(loader, "Foo.cs", code);
        var analyzer = new CSharpAnalyzer();
        var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

        var results = new List<AnalysisResult>();
        await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
            results.Add(result);

        results.Should().NotBeEmpty();
        results.Any(r => r.Message.Contains("cannot implicitly convert", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
    }

    [Test]
    public async Task Analyzer_Respects_Rule_Overrides(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var code = """
        public class Foo
        {
            public void Bar()
            {
                int x = "oops";
            }
        }
        """;

        var document = await LoadCSharpDocumentAsync(loader, "Foo.cs", code);
        var analyzer = new CSharpAnalyzer();
        var settings = new AnalyzerSettings(new Dictionary<string, AnalyzerRuleSettings>
        {
            ["csharp/CS0029"] = new() { RuleId = "csharp/CS0029", Severity = AnalysisSeverity.None }
        });
        var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document, settings);

        var results = new List<AnalysisResult>();
        await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
            results.Add(result);

        results.Should().BeEmpty();
    }

    [Test]
    public async Task ProjectAnalysis_Resolves_CrossFile_Symbols(CancellationToken token)
    {
        using var host = new CSharpWorkspaceHost();
        var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Helper.cs"] = """
namespace Demo;

public static class Helper
{
    public static int Value => 42;
}
""",
            ["Foo.cs"] = """
namespace Demo;

public class Foo
{
    private readonly int _offset = 1;

    public int Run() => Helper.Value + _offset;
}
"""
        });

        try
        {
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var analyzer = new CSharpAnalyzer();
            var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
                results.Add(result);

            results.Should().BeEmpty($"diagnostics: {string.Join(", ", results.Select(r => r.RuleId + ":" + r.Message))}");
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task ProjectAnalysis_Reports_Project_Diagnostics(CancellationToken token)
    {
        using var host = new CSharpWorkspaceHost();
        var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Foo.cs"] = """
            namespace Demo;

            public class Foo
            {
                public int Run() => MissingType.Value;
            }
            """
        });

        try
        {
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var analyzer = new CSharpAnalyzer();
            var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
                results.Add(result);

            results.Any(r => r.RuleId == "csharp/CS0103").Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task WorkspaceHost_Caches_Project_Loads(CancellationToken token)
    {
        using var host = new CSharpWorkspaceHost();
        var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Helper.cs"] = """
            namespace Demo;

            public static class Helper
            {
                public static int Value => 42;
            }
            """,
            ["Foo.cs"] = """
            namespace Demo;

            public class Foo
            {
                public int Run() => Helper.Value;
            }
            """
        });

        try
        {
            var helperPath = Path.Combine(projectDir, "Helper.cs");
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            await LoadPhysicalDocumentAsync(loader, helperPath);
            await LoadPhysicalDocumentAsync(loader, fooPath);

            var projectPath = Path.Combine(projectDir, "Demo.csproj");
            host.GetProjectLoadCount(projectPath).Should().Be(1);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task WorkspaceHost_Skips_Files_Without_Project(CancellationToken token)
    {
        using var host = new CSharpWorkspaceHost();
        var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
        await LoadCSharpDocumentAsync(loader, "Standalone.cs", "public class Foo { }");

        host.ActiveSessionCount.Should().Be(0);
    }

    [Test]
    public async Task Analyzer_Emits_Project_Analyzer_Diagnostics(CancellationToken token)
    {
        var analyzerPath = CreateAnalyzerAssembly();
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Foo.cs"] = "namespace Demo; public class Foo { }"
        }, analyzers: [analyzerPath]);

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var analyzer = new CSharpAnalyzer();
            var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
                results.Add(result);

            results.Any(r => r.RuleId == "csharp/AN0001").Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(Path.GetDirectoryName(analyzerPath)!);
        }
    }

    [Test]
    public async Task Analyzer_Respects_EditorConfig_Severity(CancellationToken token)
    {
        var analyzerPath = CreateAnalyzerAssembly();
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Foo.cs"] = "namespace Demo; public class Foo { }"
        }, analyzers: [analyzerPath]);

        var editorConfig = """
root = true

[*.cs]
dotnet_diagnostic.AN0001.severity = none
"""
;
        await File.WriteAllTextAsync(Path.Combine(projectDir, ".editorconfig"), editorConfig, token);

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var analyzer = new CSharpAnalyzer();
            var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
                results.Add(result);

            results.Any(r => r.RuleId == "csharp/AN0001").Should().BeFalse();
        }
        finally
        {
            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(Path.GetDirectoryName(analyzerPath)!);
        }
    }

    [Test]
    public async Task Analyzer_Emits_Generated_Diagnostics(CancellationToken _)
    {
        var generatorPath = CreateGeneratorAssembly();
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Foo.cs"] = "namespace Demo; public partial class Foo { }"
        }, analyzers: [generatorPath]);

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var analyzer = new CSharpAnalyzer();
            var context = CreateAnalyzerContext(document.Uri.AbsoluteUri.ToLowerInvariant(), document);

            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
                results.Add(result);

            results.Any(r => r.RuleId == "csharp/CS0103" &&
                             r.Target.TargetUri.Scheme.Equals("repoql", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(Path.GetDirectoryName(generatorPath)!);
        }
    }

    [Test]
    public async Task GeneratorOutputs_Appear_As_Virtual_Documents(CancellationToken token)
    {
        var generatorPath = CreateGeneratorAssembly();
        var projectDir = CreateTempProject(
            new Dictionary<string, string>
            {
                ["Foo.cs"] = """
                namespace Demo;

                public partial class Foo { }
                """
            },
            analyzers: [generatorPath]);

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(2);
            var generatedArtifact = records.Artifacts.Single(a =>
                string.Equals(a.StoreUri.Scheme, "repoql", StringComparison.OrdinalIgnoreCase));
            generatedArtifact.Text.Should().Contain("GeneratedProperty");

            // Types now also have URIs, so filter for document nodes specifically
            var generatedNode = records.Nodes.Single(n =>
                n.Kind == "document" &&
                n.Uri is not null &&
                string.Equals(n.Uri.Scheme, "repoql", StringComparison.OrdinalIgnoreCase));
            bool.Parse(generatedNode.Props["is_generated"]!.ToString()).Should().BeTrue();
            generatedNode.Props["generator"]!.ToString().Should().Contain("DemoGenerator");
        }
        finally
        {
            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(Path.GetDirectoryName(generatorPath)!);
        }
    }

    [Test]
    public async Task GeneratorOutputs_Emitted_Once_Per_Project(CancellationToken token)
    {
        var generatorPath = CreateGeneratorAssembly();
        var projectDir = CreateTempProject(
            new Dictionary<string, string>
            {
                ["Foo.cs"] = "namespace Demo; public partial class Foo { }",
                ["Bar.cs"] = "namespace Demo; public partial class Foo { public int FromBar => 1; }"
            },
            analyzers: [generatorPath]);

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());

            var fooDocument = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Foo.cs"));
            var first = loader.Materialize(fooDocument);
            first.Artifacts.Count(a => string.Equals(a.StoreUri.Scheme, "repoql", StringComparison.OrdinalIgnoreCase)).Should().Be(1);

            var barDocument = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Bar.cs"));
            var second = loader.Materialize(barDocument);
            second.Artifacts.Count(a => string.Equals(a.StoreUri.Scheme, "repoql", StringComparison.OrdinalIgnoreCase)).Should().Be(0);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
            TryDeleteDirectory(Path.GetDirectoryName(generatorPath)!);
        }
    }

    private static async Task<DocumentModel> LoadCSharpDocumentAsync(CSharpLoader loader, string fileName, string content, bool useDeterministicPath = false)
    {
        var path = useDeterministicPath
            ? Path.Combine(Path.GetTempPath(), $"repoql_test_{fileName}")
            : Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}_{fileName}");
        await File.WriteAllTextAsync(path, content);
        var document = await LoadPhysicalDocumentAsync(loader, path);
        TryDeleteFile(path);
        return document;
    }

    private static string CreateGeneratorAssembly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"repoql_gen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, "DemoSourceGenerator.dll");

        var source = """
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

[Generator]
public sealed class DemoGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        const string code = "namespace Demo { public partial class Foo { public int GeneratedProperty => MissingType.Value; } }";
        context.AddSource("Demo.Generated", SourceText.From(code, Encoding.UTF8));
    }

    public void Initialize(GeneratorInitializationContext context) { }
}
""";

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Stream).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Encoding).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SourceText).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(GeneratorExecutionContext).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CSharpSyntaxTree).Assembly.Location)
        };

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var systemRuntimePath = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(systemRuntimePath))
                references.Add(MetadataReference.CreateFromFile(systemRuntimePath));
        }

        var compilation = CSharpCompilation.Create(
            "DemoSourceGenerator",
            [syntaxTree],
            references.ToArray(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dllPath);
        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Failed to build source generator: {errors}");
        }

        return dllPath;
    }

    private static string CreateAnalyzerAssembly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"repoql_analyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, "DemoAnalyzer.dll");

var source = """
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DemoAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        "AN0001",
        "Test analyzer",
        "Class '{0}' must declare a field named '_required'",
        "Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        var hasRequiredField = declaration.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Any(v => v.Identifier.ValueText == "_required");

        if (!hasRequiredField)
        {
            var diagnostic = Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), declaration.Identifier.ValueText);
            context.ReportDiagnostic(diagnostic);
        }
    }
}
""";

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Immutable.ImmutableArray).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(GeneratorExecutionContext).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CSharpSyntaxTree).Assembly.Location)
        };

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            var systemRuntimePath = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(systemRuntimePath))
                references.Add(MetadataReference.CreateFromFile(systemRuntimePath));
        }

        var compilation = CSharpCompilation.Create(
            "DemoAnalyzer",
            [syntaxTree],
            references.ToArray(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dllPath);
        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Failed to build analyzer: {errors}");
        }

        return dllPath;
    }

    private static async Task<DocumentModel> LoadPhysicalDocumentAsync(CSharpLoader loader, string filePath)
    {
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(filePath)!);
        var fileName = Path.GetFileName(filePath);
        var dirName = Path.GetFileName(Path.GetDirectoryName(filePath)!);
        // Use repo-relative URI (file:///test/...) instead of absolute Windows path (file:///C:/...)
        // CSharpIdFactory rejects absolute filesystem paths
        // Include parent dir name to avoid URI collisions when multiple tests use same filename
        var repoRelativeUri = $"file:///test/{dirName}/{fileName}";
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse(repoRelativeUri)
        };

        await loader.CanLoadAsync(artifact);
        return await loader.LoadAsync(artifact);
    }

    private static AnalyzerContext CreateAnalyzerContext(string documentKey, DocumentModel document, AnalyzerSettings? settings = null)
    {
        var csharpLoader = new CSharpLoader();
        var csharpAnalyzer = new CSharpAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));

        var registry = new FormatRegistry([
            new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("code.csharp"),
                csharpLoader,
                csharpAnalyzer,
                csharpLoader,
                ["csharp","cs"]),
            new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
                plainLoader,
                plainAnalyzer,
                plainLoader)
        ]);

        return new AnalyzerContext(settings ?? new AnalyzerSettings(), "/repo");
    }

    private static string CreateTempProject(IDictionary<string, string> sourceFiles, IEnumerable<string>? analyzers = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"repoql_proj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // Detect current target framework to match test runner environment
        var currentFramework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var targetFramework = currentFramework.Contains(".NET 10") ? "net10.0" :
                              currentFramework.Contains(".NET 9") ? "net9.0" :
                              currentFramework.Contains(".NET 8") ? "net8.0" : "net8.0";

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("  </PropertyGroup>");
        if (analyzers is not null)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var analyzer in analyzers)
            {
                sb.AppendLine($"    <Analyzer Include=\"{analyzer}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }
        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(dir, "Demo.csproj"), sb.ToString());
        foreach (var kvp in sourceFiles)
        {
            File.WriteAllText(Path.Combine(dir, kvp.Key), kvp.Value);
        }
        return dir;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }

    #region Edge Case Tests

    [Test]
    public async Task Loader_Handles_Empty_File(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var emptyContent = string.Empty;

        await File.WriteAllTextAsync(tempPath, emptyContent);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Empty.cs")
        };

        try
        {
            (await loader.CanLoadAsync(artifact)).Should().BeTrue();
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(1);
            records.Nodes.Any(n => n.Kind == "document").Should().BeTrue();
            records.Nodes.Count(n => n.Kind == "csharp.type").Should().Be(0);
            records.Nodes.Count(n => n.Kind == "csharp.member").Should().Be(0);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Only_Comments(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var commentOnlyContent = """
        // This is a comment
        /* This is a multi-line
           comment */
        /// <summary>XML doc comment</summary>
        """;

        await File.WriteAllTextAsync(tempPath, commentOnlyContent);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/CommentsOnly.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(1);
            records.Nodes.Count(n => n.Kind == "csharp.type").Should().Be(0);
            records.Nodes.Count(n => n.Kind == "csharp.member").Should().Be(0);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Syntax_Errors(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var invalidSyntax = """
        public class Broken
        {
            public void Method(
            // Missing closing parenthesis and brace
        """;

        await File.WriteAllTextAsync(tempPath, invalidSyntax);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Broken.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(1);
            records.Nodes.Any(n => n.Kind == "document").Should().BeTrue();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Nested_Types(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var nestedCode = """
        namespace MyApp;

        public class Outer
        {
            public class Middle
            {
                public class Inner
                {
                    public int Value { get; set; }
                }
            }
        }
        """;

        await File.WriteAllTextAsync(tempPath, nestedCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Nested.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Nodes.Count(n => n.Kind == "csharp.type").Should().Be(3);
            var outerType = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Outer");
            outerType.Should().NotBeNull();
            outerType!.Props["qualified_name"]!.ToString().Should().Contain("Outer");

            var middleType = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Middle");
            middleType.Should().NotBeNull();

            var innerType = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Inner");
            innerType.Should().NotBeNull();

            var innerProp = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.member" && n.Props["name"]!.ToString() == "Value");
            innerProp.Should().NotBeNull();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Generic_Types_With_Constraints(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var genericCode = """
        using System;

        namespace MyApp;

        public class Container<T> where T : IDisposable, new()
        {
            public T Create() => new T();
        }

        public class MultiGeneric<TKey, TValue> where TKey : notnull where TValue : class
        {
            public void Add(TKey key, TValue value) { }
        }
        """;

        await File.WriteAllTextAsync(tempPath, genericCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Generic.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Nodes.Count(n => n.Kind == "csharp.type").Should().Be(2);
            var containerType = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString().Contains("Container"));
            containerType.Should().NotBeNull();

            var multiGenericType = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString().Contains("MultiGeneric"));
            multiGenericType.Should().NotBeNull();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Delegates(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var delegateCode = """
        namespace MyApp;

        public delegate void MyDelegate(int x, string y);
        public delegate TResult MyGenericDelegate<T, TResult>(T input);

        public class UsesDelegate
        {
            public event MyDelegate? OnEvent;
        }
        """;

        await File.WriteAllTextAsync(tempPath, delegateCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Delegates.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Nodes.Any(n => n.Kind == "document").Should().BeTrue();
            var usesDelegate = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "UsesDelegate");
            usesDelegate.Should().NotBeNull();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_File_Scoped_Namespaces(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var fileScopedCode = """
        namespace MyApp.Services;

        public class MyService
        {
            public void DoWork() { }
        }

        public interface IService { }
        """;

        await File.WriteAllTextAsync(tempPath, fileScopedCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Service.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            // Namespaces are no longer materialized as nodes
            records.Nodes.Where(n => n.Kind == "csharp.namespace").Should().BeEmpty();

            var service = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "MyService");
            service.Should().NotBeNull();
            service!.Props["namespace"]!.ToString().Should().Be("MyApp.Services");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Global_Usings(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var globalUsingCode = """
        global using System;
        global using System.Linq;

        namespace MyApp;

        public class Foo { }
        """;

        await File.WriteAllTextAsync(tempPath, globalUsingCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/GlobalUsings.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(1);
            var docNode = records.Nodes.Single(n => n.Kind == "document");
            int.Parse(docNode.Props["using_count"]!.ToString()).Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Record_With_Primary_Constructor(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var recordCode = """
        namespace MyApp;

        public record Person(string Name, int Age);

        public record struct Point(int X, int Y);
        """;

        await File.WriteAllTextAsync(tempPath, recordCode);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Records.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            var personRecord = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Person");
            personRecord.Should().NotBeNull();
            bool.Parse(personRecord!.Props["is_record"]!.ToString()).Should().BeTrue();

            var pointRecord = records.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Point");
            pointRecord.Should().NotBeNull();
            bool.Parse(pointRecord!.Props["is_record"]!.ToString()).Should().BeTrue();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Multiple_Partial_Types_Cross_File(CancellationToken token)
    {
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Partial1.cs"] = """
            namespace Demo;

            public partial class PartialType
            {
                public void Method1() { }
            }
            """,
            ["Partial2.cs"] = """
            namespace Demo;

            public partial class PartialType
            {
                public void Method2() { }
            }
            """,
            ["Partial3.cs"] = """
            namespace Demo;

            public partial class PartialType
            {
                public int Property { get; set; }
            }
            """
        });

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var doc1 = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Partial1.cs"));
            var doc2 = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Partial2.cs"));
            var doc3 = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Partial3.cs"));

            var records1 = loader.Materialize(doc1);
            var records2 = loader.Materialize(doc2);
            var records3 = loader.Materialize(doc3);

            var type1 = records1.Nodes.First(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "PartialType");
            var type2 = records2.Nodes.First(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "PartialType");
            var type3 = records3.Nodes.First(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "PartialType");

            bool.Parse(type1.Props["is_partial"]!.ToString()).Should().BeTrue();
            bool.Parse(type2.Props["is_partial"]!.ToString()).Should().BeTrue();
            bool.Parse(type3.Props["is_partial"]!.ToString()).Should().BeTrue();

            var symbolKey1 = type1.Props["symbol_key"]?.GetValue<string>();
            var symbolKey2 = type2.Props["symbol_key"]?.GetValue<string>();
            var symbolKey3 = type3.Props["symbol_key"]?.GetValue<string>();

            symbolKey1.Should().NotBeNullOrEmpty();
            symbolKey1.Should().Be(symbolKey2);
            symbolKey1.Should().Be(symbolKey3);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    #endregion

    #region Determinism Tests

    [Test]
    public async Task SpanIds_Are_Deterministic_Across_Runs(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var code = """
        namespace Demo;

        public class TestClass
        {
            public int Method(string input) => input.Length;
        }
        """;

        // Create file once, then load it twice to verify deterministic IDs
        var path = Path.Combine(Path.GetTempPath(), $"repoql_test_{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(path, code);

            var run1 = await LoadPhysicalDocumentAsync(loader, path);
            var records1 = loader.Materialize(run1);

            var run2 = await LoadPhysicalDocumentAsync(loader, path);
            var records2 = loader.Materialize(run2);

            var spans1 = records1.Spans.OrderBy(s => s.Id).ToList();
            var spans2 = records2.Spans.OrderBy(s => s.Id).ToList();

            spans1.Count.Should().Be(spans2.Count);
            for (int i = 0; i < spans1.Count; i++)
            {
                spans1[i].Id.Should().Be(spans2[i].Id);
                spans1[i].StartLine.Should().Be(spans2[i].StartLine);
                spans1[i].StartColumn.Should().Be(spans2[i].StartColumn);
                spans1[i].EndLine.Should().Be(spans2[i].EndLine);
                spans1[i].EndColumn.Should().Be(spans2[i].EndColumn);
            }
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Test]
    public async Task SymbolKeys_Are_Stable_Across_Runs(CancellationToken token)
    {
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Test.cs"] = """
            namespace Demo;

            public class TestClass
            {
                public int Method(string input) => input.Length;
            }
            """
        });

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var filePath = Path.Combine(projectDir, "Test.cs");

            var doc1 = await LoadPhysicalDocumentAsync(loader, filePath);
            var records1 = loader.Materialize(doc1);

            var doc2 = await LoadPhysicalDocumentAsync(loader, filePath);
            var records2 = loader.Materialize(doc2);

            var type1 = records1.Nodes.First(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "TestClass");
            var type2 = records2.Nodes.First(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "TestClass");

            var symbolKey1 = type1.Props["symbol_key"]?.GetValue<string>();
            var symbolKey2 = type2.Props["symbol_key"]?.GetValue<string>();

            symbolKey1.Should().NotBeNullOrEmpty();
            symbolKey1.Should().Be(symbolKey2);

            var member1 = records1.Nodes.First(n => n.Kind == "csharp.member" && n.Props["name"]!.ToString() == "Method");
            var member2 = records2.Nodes.First(n => n.Kind == "csharp.member" && n.Props["name"]!.ToString() == "Method");

            var memberKey1 = member1.Props["symbol_key"]?.GetValue<string>();
            var memberKey2 = member2.Props["symbol_key"]?.GetValue<string>();

            memberKey1.Should().NotBeNullOrEmpty();
            memberKey1.Should().Be(memberKey2);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task NodeIds_Are_Deterministic(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var code = """
        namespace Demo;

        public class TestClass
        {
            public int Value { get; set; }
        }
        """;

        // Create file once, then load it twice to verify deterministic IDs
        var path = Path.Combine(Path.GetTempPath(), $"repoql_test_{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(path, code);

            var run1 = await LoadPhysicalDocumentAsync(loader, path);
            var records1 = loader.Materialize(run1);

            var run2 = await LoadPhysicalDocumentAsync(loader, path);
            var records2 = loader.Materialize(run2);

            var type1 = records1.Nodes.First(n => n.Kind == "csharp.type");
            var type2 = records2.Nodes.First(n => n.Kind == "csharp.type");

            type1.Id.Should().Be(type2.Id);

            var member1 = records1.Nodes.First(n => n.Kind == "csharp.member");
            var member2 = records2.Nodes.First(n => n.Kind == "csharp.member");

            member1.Id.Should().Be(member2.Id);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public async Task Loader_Handles_Missing_Project_File_Gracefully(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");
        var code = """
        namespace Demo;

        public class Foo
        {
            public int Value => 42;
        }
        """;

        await File.WriteAllTextAsync(tempPath, code);
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Foo.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().Be(1);
            records.Nodes.Any(n => n.Kind == "csharp.type").Should().BeTrue();
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task Loader_Handles_Corrupted_Project_File(CancellationToken token)
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"repoql_proj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDir);

        var invalidCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0
        """;

        File.WriteAllText(Path.Combine(projectDir, "Demo.csproj"), invalidCsproj);
        File.WriteAllText(Path.Combine(projectDir, "Foo.cs"), "namespace Demo; public class Foo { }");

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var fooPath = Path.Combine(projectDir, "Foo.cs");
            var document = await LoadPhysicalDocumentAsync(loader, fooPath);
            var records = loader.Materialize(document);

            records.Artifacts.Length.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task Loader_Handles_Very_Large_File(CancellationToken token)
    {
        var loader = new CSharpLoader();
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}.cs");

        var sb = new StringBuilder();
        sb.AppendLine("namespace Demo;");
        sb.AppendLine();
        sb.AppendLine("public class LargeFile");
        sb.AppendLine("{");

        for (int i = 0; i < 1000; i++)
        {
            sb.AppendLine($"    public int Property{i} {{ get; set; }}");
            sb.AppendLine($"    public void Method{i}() {{ }}");
        }

        sb.AppendLine("}");

        await File.WriteAllTextAsync(tempPath, sb.ToString());
        using var provider = new PhysicalFileProvider(Path.GetDirectoryName(tempPath)!);
        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(Path.GetFileName(tempPath)),
            RepoUri = RepoUri.Parse("file:///workspace/src/Large.cs")
        };

        try
        {
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            records.Nodes.Count(n => n.Kind == "csharp.member").Should().BeGreaterThan(1000);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Test]
    public async Task WorkspaceHost_Handles_Concurrent_Loads(CancellationToken token)
    {
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["File1.cs"] = "namespace Demo; public class Class1 { }",
            ["File2.cs"] = "namespace Demo; public class Class2 { }",
            ["File3.cs"] = "namespace Demo; public class Class3 { }",
            ["File4.cs"] = "namespace Demo; public class Class4 { }",
            ["File5.cs"] = "namespace Demo; public class Class5 { }"
        });

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());

            var tasks = new List<Task<DocumentModel>>();
            for (int i = 1; i <= 5; i++)
            {
                var filePath = Path.Combine(projectDir, $"File{i}.cs");
                tasks.Add(LoadPhysicalDocumentAsync(loader, filePath));
            }

            var documents = await Task.WhenAll(tasks);

            documents.Length.Should().Be(5);
            foreach (var doc in documents)
            {
                var records = loader.Materialize(doc);
                records.Nodes.Any(n => n.Kind == "csharp.type").Should().BeTrue();
            }
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    [Test]
    public async Task CrossFile_References_To_Nested_Types(CancellationToken token)
    {
        var projectDir = CreateTempProject(new Dictionary<string, string>
        {
            ["Container.cs"] = """
            namespace Demo;

            public class Outer
            {
                public class Inner
                {
                    public int Value => 42;
                }
            }
            """,
            ["Consumer.cs"] = """
            namespace Demo;

            public class Consumer
            {
                public Outer.Inner GetInner() => new Outer.Inner();
            }
            """
        });

        try
        {
            using var host = new CSharpWorkspaceHost();
            var loader = new CSharpLoader(host, CreateAnalysisConfiguration());
            var containerDoc = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Container.cs"));
            var containerRecords = loader.Materialize(containerDoc);

            var innerType = containerRecords.Nodes.FirstOrDefault(n => n.Kind == "csharp.type" && n.Props["name"]!.ToString() == "Inner");
            innerType.Should().NotBeNull();

            var consumerDoc = await LoadPhysicalDocumentAsync(loader, Path.Combine(projectDir, "Consumer.cs"));
            var consumerRecords = loader.Materialize(consumerDoc);

            var usesEdges = consumerRecords.Edges.Where(e => e.Type == "USES_SYMBOL").ToArray();
            usesEdges.Should().NotBeEmpty();
        }
        finally
        {
            TryDeleteDirectory(projectDir);
        }
    }

    private static RepoQlConfig CreateAnalysisConfiguration()
        => new()
        {
            Dotnet = new RepoQlConfig.DotnetSettings
            {
                Analysis = true
            }
        };

    #endregion
}
