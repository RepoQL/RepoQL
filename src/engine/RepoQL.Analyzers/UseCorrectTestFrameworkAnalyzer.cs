using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects xUnit attributes ([Fact], [Theory], [InlineData]) and reports them as warnings,
/// guiding authors toward TUnit equivalents ([Test], [Test], [Arguments]).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseCorrectTestFrameworkAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL001";

    private static readonly ImmutableDictionary<string, string> XUnitToTUnit = ImmutableDictionary.CreateRange(new[]
    {
        new KeyValuePair<string, string>("Fact", "[Test]"),
        new KeyValuePair<string, string>("FactAttribute", "[Test]"),
        new KeyValuePair<string, string>("Theory", "[Test]"),
        new KeyValuePair<string, string>("TheoryAttribute", "[Test]"),
        new KeyValuePair<string, string>("InlineData", "[Arguments(...)]"),
        new KeyValuePair<string, string>("InlineDataAttribute", "[Arguments(...)]"),
    });

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use TUnit instead of xUnit",
        "'{0}' is an xUnit attribute — use {1} (TUnit) instead",
        "RepoQL.Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "This project uses TUnit. Replace xUnit attributes with their TUnit equivalents.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        var name = attribute.Name switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => null,
        };

        if (name == null || !XUnitToTUnit.TryGetValue(name, out var tunitEquivalent))
            return;

        // Only fire if this actually resolves to an xUnit type
        var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        var containingNamespace = symbol?.ContainingType?.ContainingNamespace?.ToDisplayString()
                                  ?? symbol?.ContainingNamespace?.ToDisplayString();

        if (containingNamespace == null || !containingNamespace.StartsWith("Xunit", StringComparison.Ordinal))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, attribute.GetLocation(), name, tunitEquivalent));
    }
}
