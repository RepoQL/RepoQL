using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp.Analysis;
using RepoQL.Formats.Cpp.TreeSitter;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class MacroInterferenceDetectorTests
{
    [Test]
    public void Detect_QObjectMacro_EmitsMacroInterference()
    {
        var annotations = DetectAnnotations("macro_qobject_interference.hpp");

        annotations.Should().Contain(a =>
            a.RuleId == "cpp/macro_interference"
            && a.Data["macro_name"]!.ToString() == "Q_OBJECT");
    }

    [Test]
    public void Detect_ExportMacro_EmitsMacroInterference()
    {
        var annotations = DetectAnnotations("macro_export_interference.hpp");

        annotations.Should().Contain(a =>
            a.RuleId == "cpp/macro_interference"
            && a.Data["macro_name"]!.ToString().Contains("EXPORT", StringComparison.Ordinal));
    }

    [Test]
    public void Detect_SyntaxError_EmitsSyntaxRule()
    {
        var annotations = DetectAnnotations("malformed_syntax_error.hpp");

        annotations.Should().Contain(a => a.RuleId == "cpp/syntax_error");
        annotations.Should().OnlyContain(a => a.ScopeDocumentId != Guid.Empty);
    }

    [Test]
    public void Detect_TemplateError_EmitsTemplateComplexityRule()
    {
        var annotations = DetectAnnotations("template_complexity_error.hpp");

        annotations.Should().Contain(a => a.RuleId == "cpp/template_complexity");
    }

    [Test]
    public void Detect_PreprocessorBoundary_EmitsBoundaryRule()
    {
        var annotations = DetectAnnotations("preprocessor_boundary_error.hpp");

        annotations.Should().Contain(a => a.RuleId == "cpp/preprocessor_boundary");
        annotations.Should().OnlyContain(a =>
            a.Data.ContainsKey("start_line")
            && a.Data.ContainsKey("end_line"));
    }

    private static IReadOnlyList<Annotation> DetectAnnotations(string fixtureName)
    {
        var source = CppTestHelpers.ReadFixture(fixtureName);
        using var client = new CppTreeSitterClient();
        if (!client.IsGrammarAvailable)
        {
            Skip.Test("tree-sitter-cpp grammar is not bundled on this machine.");
            return [];
        }

        using var parse = client.Parse(source);
        parse.HasTree.Should().BeTrue();
        parse.RootNode.Should().NotBeNull();

        var detector = new MacroInterferenceDetector();
        var document = new DocumentModel(
            RepoUri.Parse($"file:///{fixtureName}"),
            SemanticMediaType.Create("text", "plain").WithKind("code.cpp-header"),
            source);

        return detector.Detect(parse.RootNode!, document, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }
}
