using AwesomeAssertions;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppMaterializerPlan02Tests
{
    [Test]
    public async Task Materialize_PreprocessorNodes_ExtractsIncludeMacroUsingAndConditionalAnnotations()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_preprocessor_nodes.hpp");

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.include"
            && Prop(n, "target") == "pool.h"
            && Prop(n, "style") == "\"\"");
        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.include"
            && Prop(n, "target") == "vector"
            && Prop(n, "style") == "<>");

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.macro"
            && Prop(n, "name") == "TRACE_CALL"
            && Prop(n, "parameters") == "x"
            && Prop(n, "replacement") == "x");

        var usingNode = records.Nodes.Single(n => n.Kind == "cpp.using");
        Prop(usingNode, "target").Should().Contain("ResolvedTarget");
        var hasUsingEdge = records.Edges.Any(e =>
            e.Type == "REFERS_TO"
            && e.SrcId == usingNode.Id
            && e.Props["relationship"] is not null
            && e.Props["relationship"]!.ToString() == "using");
        hasUsingEdge.Should().BeTrue();

        records.Annotations.Should().Contain(a => a.RuleId == "cpp/conditional_compilation");
    }

    [Test]
    public async Task Materialize_TemplateConceptAndModule_ExtractsPlan02Properties()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_templates_concepts_module.cpp");

        var templateType = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "name") == "Buffer");
        Prop(templateType, "is_template").Should().Be("true");
        Prop(templateType, "template_params").Should().Contain("typename T").And.Contain("int N");

        var hasSpecialization = records.Nodes.Any(n =>
            n.Kind == "cpp.type"
            && n.Props["base_template"] is not null
            && n.Props["specialization_args"] is not null);
        hasSpecialization.Should().BeTrue();

        var concept = records.Nodes.Single(n => n.Kind == "cpp.type" && Prop(n, "kind") == "concept");
        Prop(concept, "constraint").Should().Contain("requires");

        var module = records.Nodes.Single(n => n.Kind == "cpp.module");
        Prop(module, "kind").Should().Be("module");
        Prop(module, "partition").Should().Be("impl");
        Prop(module, "is_export").Should().Be("true");
    }

    [Test]
    public async Task Materialize_ModuleError_EmitsUnsupportedModuleSyntaxAnnotation()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_module_error.cpp");

        records.Annotations.Should().Contain(a => a.RuleId == "cpp/unsupported_module_syntax");
    }

    [Test]
    public async Task Materialize_FriendCoroutineExceptionsAndPointers_AreMaterialized()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_friend_coroutine_exception.hpp");

        var bitfield = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "name") == "flags");
        Prop(bitfield, "bitfield_width").Should().Be("4");

        var functionPointer = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "name") == "handler");
        Prop(functionPointer, "is_function_pointer").Should().Be("true");
        functionPointer.Props["pointed_signature"]!.ToString().Should().Contain("int");

        var variadic = records.Nodes.Single(n => n.Kind == "cpp.member" && Prop(n, "name") == "log");
        Prop(variadic, "is_variadic").Should().Be("true");

        var coroutine = records.Nodes.Single(n => n.Kind == "cpp.function" && Prop(n, "name") == "stream_values");
        Prop(coroutine, "is_coroutine").Should().Be("true");

        var hasFriendEdge = records.Edges.Any(e =>
            e.Type == "REFERS_TO"
            && e.Props["relationship"] is not null
            && e.Props["relationship"]!.ToString() == "friend"
            && e.Props["target"] is not null
            && e.Props["target"]!.ToString().Contains("FriendTarget", StringComparison.Ordinal));
        hasFriendEdge.Should().BeTrue();

        records.Annotations.Should().Contain(a => a.RuleId == "cpp/exception_handler");
        records.Annotations.Should().Contain(a => a.RuleId == "cpp/throw_expression");
    }

    [Test]
    public async Task Materialize_TypedefUsingAliasAndConstexpr_AreExtracted()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_type_aliases.hpp");

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.member"
            && Prop(n, "kind") == "typedef"
            && Prop(n, "name") == "U32"
            && Prop(n, "target_type") == "unsigned int");

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.member"
            && Prop(n, "kind") == "typedef"
            && Prop(n, "name") == "Callback"
            && Prop(n, "is_function_pointer") == "true");

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.member"
            && Prop(n, "kind") == "type_alias"
            && Prop(n, "name") == "VecInt"
            && Prop(n, "target_type").Contains("std::vector", StringComparison.Ordinal));

        records.Nodes.Should().Contain(n =>
            n.Kind == "cpp.member"
            && Prop(n, "name") == "MaxItems"
            && Prop(n, "is_constexpr") == "true");
    }

    [Test]
    public async Task Materialize_HeadlineIncludesMacroWarning_WhenInterferenceDetected()
    {
        using var materializer = new CppMaterializer();
        if (!RequireGrammar(materializer))
        {
            return;
        }

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "macro_qobject_interference.hpp");

        records.Artifacts[0].Headline.Should().Contain("⚠");
        records.Artifacts[0].Headline.Should().Contain("Q_OBJECT");
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
