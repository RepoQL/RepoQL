using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp.TreeSitter;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppMaterializerTests
{
    [Test]
    public async Task Materialize_ClassExtraction_ExtractsTypesMembersAndProperties()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "class_extraction.hpp");

        var abstractBase = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::AbstractBase");
        Prop(abstractBase, "kind").Should().Be("class");
        Prop(abstractBase, "namespace").Should().Be("net");
        Prop(abstractBase, "is_abstract").Should().Be("true");
        Prop(abstractBase, "is_forward_declaration").Should().Be("false");

        var connectionPool = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::ConnectionPool");
        Prop(connectionPool, "kind").Should().Be("class");
        Prop(connectionPool, "accessibility").Should().Be("private");
        Prop(connectionPool, "extends").Should().Contain("AbstractBase").And.Contain("detail::Tracker");

        var abstractConnect = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::AbstractBase::connect");
        Prop(abstractConnect, "kind").Should().Be("method");
        Prop(abstractConnect, "declaring_type").Should().Be("AbstractBase");
        Prop(abstractConnect, "is_virtual").Should().Be("true");
        Prop(abstractConnect, "is_pure_virtual").Should().Be("true");

        var connectOverride = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::ConnectionPool::connect");
        Prop(connectOverride, "kind").Should().Be("method");
        Prop(connectOverride, "declaring_type").Should().Be("ConnectionPool");
        Prop(connectOverride, "return_type").Should().Be("void");
        Prop(connectOverride, "signature").Should().Contain("connect(const std::string& endpoint)");
        Prop(connectOverride, "is_override").Should().Be("true");

        var connectParameters = Parameters(connectOverride);
        connectParameters.Should().HaveCount(1);
        var endpoint = (JsonObject)connectParameters[0]!;
        endpoint["name"]!.ToString().Should().Be("endpoint");
        endpoint["type"]!.ToString().Should().Be("const std::string&");

        var retries = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::ConnectionPool::retries");
        Prop(retries, "is_noexcept").Should().Be("true");
        Prop(retries, "is_constexpr").Should().Be("true");
        Prop(retries, "is_const").Should().Be("true");

        var shutdown = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::ConnectionPool::shutdown");
        Prop(shutdown, "is_final").Should().Be("true");
        Prop(shutdown, "accessibility").Should().Be("protected");

        var port = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::ConnectionPool::port");
        Prop(port, "kind").Should().Be("field");
        Prop(port, "accessibility").Should().Be("private");
        Prop(port, "declaring_type").Should().Be("ConnectionPool");
        Prop(port, "return_type").Should().Be("int");
        Prop(port, "namespace").Should().Be("net");

        var hasPartEdges = records.Edges.Where(e => e.Type == "HAS_PART").ToArray();
        hasPartEdges.Should().NotBeEmpty();
        hasPartEdges.Should().OnlyContain(e => e.IsComposition);

        var structure = records.Artifacts[0].Structure ?? string.Empty;
        structure.Should().Contain("+ void connect(const std::string& endpoint) override");
        structure.Should().Contain("- int port");
        structure.Should().Contain("# virtual void shutdown() final");
    }

    [Test]
    public async Task Materialize_StructExtraction_UsesPublicDefaultAccess()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "struct_enum_namespace.hpp");

        var endpoint = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::internal::Endpoint");
        Prop(endpoint, "kind").Should().Be("struct");
        Prop(endpoint, "accessibility").Should().Be("public");
        Prop(endpoint, "namespace").Should().Be("net::internal");

        var field = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::internal::Endpoint::port");
        Prop(field, "kind").Should().Be("field");
        Prop(field, "accessibility").Should().Be("public");
        Prop(field, "declaring_type").Should().Be("Endpoint");

        var reset = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::internal::Endpoint::reset");
        Prop(reset, "kind").Should().Be("method");
        Prop(reset, "accessibility").Should().Be("public");
        Prop(reset, "declaring_type").Should().Be("Endpoint");
    }

    [Test]
    public async Task Materialize_EnumExtraction_TracksScopedAndEnumeratorValues()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "struct_enum_namespace.hpp");

        var state = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::internal::State");
        Prop(state, "kind").Should().Be("enum");
        Prop(state, "is_scoped").Should().Be("true");
        Prop(state, "underlying_type").Should().Be("uint8_t");

        var errorCode = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "net::internal::ErrorCode");
        Prop(errorCode, "kind").Should().Be("enum");
        Prop(errorCode, "is_scoped").Should().Be("false");

        var disconnected = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::internal::State::Disconnected");
        Prop(disconnected, "kind").Should().Be("enumerator");
        Prop(disconnected, "value").Should().Be("0");

        var timeout = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "qualified_name") == "net::internal::ErrorCode::Timeout");
        Prop(timeout, "kind").Should().Be("enumerator");
        Prop(timeout, "value").Should().Be("100");
    }

    [Test]
    public async Task Materialize_NamespaceExtraction_HandlesNestedQualifiedNames()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "nested_namespace.hpp");

        var nsA = records.Nodes.Single(n => n.Kind == "cpp.namespace" && Prop(n, "qualified_name") == "a");
        var nsB = records.Nodes.Single(n => n.Kind == "cpp.namespace" && Prop(n, "qualified_name") == "a::b");
        var nsC = records.Nodes.Single(n => n.Kind == "cpp.namespace" && Prop(n, "qualified_name") == "a::b::c");

        Prop(nsA, "name").Should().Be("a");
        Prop(nsB, "name").Should().Be("b");
        Prop(nsC, "name").Should().Be("c");

        var token = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "a::b::c::Token");
        Prop(token, "namespace").Should().Be("a::b::c");

        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == nsA.Id && e.DstId == nsB.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == nsB.Id && e.DstId == nsC.Id);
        records.Edges.Should().Contain(e => e.Type == "HAS_PART" && e.IsComposition && e.SrcId == nsC.Id && e.DstId == token.Id);
    }

    [Test]
    public async Task Materialize_FreeFunctions_ExtractsFunctionPropertiesAndQualifiers()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "free_functions.cpp");

        var connect = records.Nodes.Single(n => n.Kind == "cpp.function" && Prop(n, "qualified_name") == "net::connect");
        Prop(connect, "kind").Should().Be("function");
        Prop(connect, "return_type").Should().Be("int");
        Prop(connect, "signature").Should().Contain("inline int connect(int retries, const std::string& endpoint) noexcept");
        Prop(connect, "is_noexcept").Should().Be("true");
        Prop(connect, "is_inline").Should().Be("true");
        Prop(connect, "namespace").Should().Be("net");

        var connectParameters = Parameters(connect);
        connectParameters.Should().HaveCount(2);
        ((JsonObject)connectParameters[0]!)["name"]!.ToString().Should().Be("retries");
        ((JsonObject)connectParameters[0]!)["type"]!.ToString().Should().Be("int");
        ((JsonObject)connectParameters[1]!)["name"]!.ToString().Should().Be("endpoint");
        ((JsonObject)connectParameters[1]!)["type"]!.ToString().Should().Be("const std::string&");

        var compute = records.Nodes.Single(n => n.Kind == "cpp.function" && Prop(n, "qualified_name") == "net::compute");
        Prop(compute, "is_constexpr").Should().Be("true");

        var localOnly = records.Nodes.Single(n => n.Kind == "cpp.function" && Prop(n, "qualified_name") == "net::local_only");
        Prop(localOnly, "is_static").Should().Be("true");
    }

    [Test]
    public async Task Materialize_ForwardDeclaration_SetsIsForwardDeclaration()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(
            materializer,
            fixtureName: "forward_declaration.h",
            artifactFileName: "forward_declaration.hpp");

        var foo = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "name") == "Foo");
        Prop(foo, "kind").Should().Be("class");
        Prop(foo, "is_forward_declaration").Should().Be("true");
    }

    [Test]
    public async Task Materialize_AnonymousNamespace_SetsAnonymousProperties()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "anonymous_namespace.cpp");

        var anon = records.Nodes.Single(n => n.Kind == "cpp.namespace" && Prop(n, "name") == "(anonymous)");
        Prop(anon, "is_anonymous").Should().Be("true");

        var hidden = records.Nodes.Single(n => n.Kind == "cpp.function" && Prop(n, "name") == "hidden");
        Prop(hidden, "namespace").Should().Be("(anonymous)");
    }

    [Test]
    public async Task Materialize_InlineNamespace_SetsInlineProperty()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "inline_namespace.hpp");

        var v2 = records.Nodes.Single(n => n.Kind == "cpp.namespace" && Prop(n, "qualified_name") == "api::v2");
        Prop(v2, "is_inline").Should().Be("true");

        var client = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "qualified_name") == "api::v2::Client");
        Prop(client, "namespace").Should().Be("api::v2");
    }

    [Test]
    public async Task Materialize_ParseTimeout_EmitsAnnotationAndReturnsPartialResults()
    {
        using var materializer = new CppMaterializer(parseTimeout: TimeSpan.Zero);
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "class_extraction.hpp");

        records.Annotations.Should().Contain(a => a.RuleId == "cpp/parse_timeout");
        records.Annotations.Should().NotContain(a => a.RuleId == "cpp/grammar_load_failure");
        records.Nodes.Should().NotBeEmpty();
    }

    [Test]
    public async Task Materialize_GrammarLoadFailure_EmitsDiagnosticAnnotationAndNoStructureNodes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_cpp_missing_grammar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var client = new CppTreeSitterClient(runtimeBasePath: tempDir);
            using var materializer = new CppMaterializer(client: client);

            var records = await CppTestHelpers.LoadRecordsAsync(materializer, "free_functions.cpp");

            client.IsGrammarAvailable.Should().BeFalse();
            records.Nodes.Should().ContainSingle(n => n.Kind == "document");
            records.Edges.Should().BeEmpty();
            records.Spans.Should().BeEmpty();
            records.Annotations.Should().ContainSingle(a => a.RuleId == "cpp/grammar_load_failure");
            records.Annotations[0].Severity.Should().Be("error");
            records.Artifacts[0].Headline.Should().Contain("parse failed");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static bool RequireGrammar(CppMaterializer materializer)
    {
        if (materializer.IsGrammarAvailable)
        {
            return true;
        }

        Skip.Test("tree-sitter-cpp grammar is not bundled on this machine. Build runtimes/* native libraries to run extraction assertions.");
        return false;
    }

    private static string Prop(Node node, string key)
        => node.Props[key]!.ToString();

    private static JsonArray Parameters(Node node)
        => node.Props["parameters"]!.AsArray();
}
