using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp.Analysis;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppMultiFileAnalyzerTests
{
    [Test]
    public async Task Analyze_HeaderSourceLinking_CreatesDefinesEdgesForMethodsAndAmbiguousOverloads()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await LoadBatchAsync(
            materializer,
            "plan03_header_source_transport.h",
            "plan03_header_source_transport.cpp");

        var analyzer = new CppMultiFileAnalyzer();
        var result = analyzer.Analyze(records);

        var header = records[0];
        var source = records[1];

        var shutdownDeclaration = header.Nodes.Single(n =>
            n.Kind == "cpp.member"
            && Prop(n, "qualified_name") == "net::ConnectionPool::shutdown");
        var shutdownDefinition = source.Nodes.Single(n =>
            n.Kind == "cpp.function"
            && Prop(n, "signature").Contains("ConnectionPool::shutdown", StringComparison.Ordinal));

        result.Edges.Should().Contain(e =>
            e.Type == "REFERS_TO"
            && e.SrcId == shutdownDeclaration.Id
            && e.DstId == shutdownDefinition.Id
            && e.Props["relationship"]!.ToString() == "defines");

        var connectDeclarations = header.Nodes
            .Where(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::ConnectionPool::connect")
            .ToArray();
        connectDeclarations.Should().HaveCount(2);

        var connectDefinition = source.Nodes.Single(n =>
            n.Kind == "cpp.function"
            && Prop(n, "signature").Contains("ConnectionPool::connect", StringComparison.Ordinal));

        var connectEdges = result.Edges
            .Where(e => e.Type == "REFERS_TO"
                        && e.DstId == connectDefinition.Id
                        && e.Props["relationship"] is not null
                        && e.Props["relationship"]!.ToString() == "defines")
            .ToArray();
        connectEdges.Should().HaveCount(2);
        connectEdges.Select(e => e.SrcId).Should().BeEquivalentTo(connectDeclarations.Select(n => n.Id));
    }

    [Test]
    public async Task Analyze_InheritanceCompletion_CreatesExtendsEdgesWithAccessAndVirtualFlags()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await LoadBatchAsync(
            materializer,
            "plan03_inheritance_base.h",
            "plan03_inheritance_derived.h");

        var analyzer = new CppMultiFileAnalyzer();
        var result = analyzer.Analyze(records);

        var allNodes = records.SelectMany(r => r.Nodes).ToArray();
        var tcpTransport = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::TcpTransport");
        var virtualTransport = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::VirtualTransport");
        var multiTransport = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::MultiTransport");
        var transport = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::Transport");
        var baseType = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::Base");
        var socketBase = allNodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::SocketBase");

        result.Edges.Should().Contain(e =>
            e.Type == "EXTENDS"
            && e.SrcId == tcpTransport.Id
            && e.DstId == transport.Id
            && e.Props["access"]!.ToString() == "public"
            && e.Props["is_virtual"] == null);

        result.Edges.Should().Contain(e =>
            e.Type == "EXTENDS"
            && e.SrcId == virtualTransport.Id
            && e.DstId == baseType.Id
            && e.Props["access"]!.ToString() == "public"
            && e.Props["is_virtual"]!.ToString() == "true");

        result.Edges.Should().Contain(e =>
            e.Type == "EXTENDS"
            && e.SrcId == multiTransport.Id
            && e.DstId == transport.Id
            && e.Props["access"]!.ToString() == "public");

        result.Edges.Should().Contain(e =>
            e.Type == "EXTENDS"
            && e.SrcId == multiTransport.Id
            && e.DstId == socketBase.Id
            && e.Props["access"]!.ToString() == "private");
    }

    [Test]
    public async Task Analyze_TransitiveIncludes_CreatesTransitiveIncludeEdgesWithDepth()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var rawRecords = await LoadBatchAsync(
            materializer,
            "plan03_include_main.cpp",
            "plan03_include_a.h",
            "plan03_include_b.h");
        var records = AddDirectIncludeEdges(rawRecords);

        var analyzer = new CppMultiFileAnalyzer();
        var result = analyzer.Analyze(records);

        var mainDocument = records
            .SelectMany(r => r.Nodes)
            .Single(n => n.Kind == "document" && n.Uri!.Container.AbsolutePath.EndsWith("/plan03_include_main.cpp", StringComparison.OrdinalIgnoreCase));
        var includeBDocument = records
            .SelectMany(r => r.Nodes)
            .Single(n => n.Kind == "document" && n.Uri!.Container.AbsolutePath.EndsWith("/plan03_include_b.h", StringComparison.OrdinalIgnoreCase));

        result.Edges.Should().Contain(e =>
            e.Type == "REFERS_TO"
            && e.SrcId == mainDocument.Id
            && e.DstId == includeBDocument.Id
            && e.Props["relationship"]!.ToString() == "transitive_include"
            && e.Props["depth"]!.ToString() == "2");
    }

    [Test]
    public async Task Analyze_IncludeCycle_EmitsCycleAnnotation()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var rawRecords = await LoadBatchAsync(
            materializer,
            "plan03_cycle_main.cpp",
            "plan03_cycle_a.h",
            "plan03_cycle_b.h");
        var records = AddDirectIncludeEdges(rawRecords);

        var analyzer = new CppMultiFileAnalyzer();
        var result = analyzer.Analyze(records);

        result.Annotations.Should().Contain(a => a.RuleId == "cpp/include_cycle");
    }

    [Test]
    public async Task Analyze_ForwardDeclarations_LinkToDefinitions()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await LoadBatchAsync(
            materializer,
            "plan03_forward_decl.h",
            "plan03_forward_def.h");

        var analyzer = new CppMultiFileAnalyzer();
        var result = analyzer.Analyze(records);

        var allNodes = records.SelectMany(r => r.Nodes).ToArray();
        var forwardDeclaration = allNodes.Single(n =>
            n.Kind == "cpp.type"
            && Prop(n, "qualified_name") == "net::ForwardOnly"
            && Prop(n, "is_forward_declaration") == "true");
        var definition = allNodes.Single(n =>
            n.Kind == "cpp.type"
            && Prop(n, "qualified_name") == "net::ForwardOnly"
            && Prop(n, "is_forward_declaration") == "false");

        result.Edges.Should().Contain(e =>
            e.Type == "REFERS_TO"
            && e.SrcId == forwardDeclaration.Id
            && e.DstId == definition.Id
            && e.Props["relationship"]!.ToString() == "forward_declares");
    }

    private static async Task<Records[]> LoadBatchAsync(CppMaterializer materializer, params string[] fixtureNames)
    {
        var records = new List<Records>(fixtureNames.Length);
        foreach (var fixtureName in fixtureNames)
        {
            records.Add(await CppTestHelpers.LoadRecordsAsync(materializer, fixtureName));
        }

        return [.. records];
    }

    private static Records[] AddDirectIncludeEdges(IReadOnlyList<Records> recordsBatch)
    {
        var documentNodes = recordsBatch
            .SelectMany(r => r.Nodes)
            .Where(n => n.Kind == "document" && n.Uri is not null)
            .ToArray();
        var docsByFile = documentNodes.ToDictionary(
            n => Path.GetFileName(n.Uri!.Container.AbsolutePath),
            n => n,
            StringComparer.OrdinalIgnoreCase);

        var updated = new List<Records>(recordsBatch.Count);
        foreach (var records in recordsBatch)
        {
            var document = records.Nodes.Single(n => n.Kind == "document");
            var edges = records.Edges.ToList();
            foreach (var include in records.Nodes.Where(n => n.Kind == "cpp.include"))
            {
                var target = include.Props["target"]?.ToString();
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                docsByFile.TryGetValue(target, out var targetDocument);
                RepoUri? targetUri = targetDocument?.Uri;
                if (targetUri is null)
                {
                    var resolved = new Uri(document.Uri!.Container, target);
                    targetUri = RepoUri.TryParse(resolved.AbsoluteUri, out var repoUri) ? repoUri : null;
                }

                var style = include.Props["style"]?.ToString() ?? "\"\"";
                edges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = include.Id,
                    DstId = targetDocument?.Id,
                    DstUri = targetUri,
                    Type = "REFERS_TO",
                    IsComposition = false,
                    ScopeDocumentId = document.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Props = new JsonObject
                    {
                        ["target"] = target,
                        ["style"] = style,
                        ["is_resolved"] = targetDocument is null ? "false" : "true"
                    }
                });
            }

            updated.Add(new Records
            {
                Artifacts = records.Artifacts,
                Nodes = records.Nodes,
                Spans = records.Spans,
                Edges = [.. edges],
                Annotations = records.Annotations,
                AnnotationSources = records.AnnotationSources
            });
        }

        return [.. updated];
    }

    private static bool RequireGrammar(CppMaterializer materializer)
    {
        if (materializer.IsGrammarAvailable)
        {
            return true;
        }

        Skip.Test("tree-sitter-cpp grammar is not bundled on this machine.");
        return false;
    }

    private static string Prop(Node node, string key)
        => node.Props[key]?.ToString() ?? string.Empty;
}
