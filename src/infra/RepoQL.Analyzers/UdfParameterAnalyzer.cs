using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects [ScalarUdf] and [StructuredUdf] methods with zero parameters.
/// DuckDB.NET doesn't support parameterless UDFs — add a dummy parameter with [UdfDefault("''")].
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UdfParameterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL004";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "UDF methods must have at least one parameter",
        "'{0}' has no parameters. DuckDB.NET requires at least one, add a dummy parameter with [UdfDefault(\"''\")].",
        "RepoQL.UDF",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DuckDB.NET doesn't support parameterless UDFs. " +
                     "Add a dummy string parameter with [UdfDefault(\"''\")] to satisfy the registration requirement.");

    private static readonly ImmutableHashSet<string> UdfAttributeNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "ScalarUdf", "ScalarUdfAttribute",
        "StructuredUdf", "StructuredUdfAttribute");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;

        // Quick exit — method has parameters
        if (method.ParameterList.Parameters.Count > 0)
            return;

        // Check for UDF attributes
        var hasUdfAttribute = false;
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name switch
                {
                    SimpleNameSyntax simple => simple.Identifier.Text,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    _ => null,
                };

                if (name != null && UdfAttributeNames.Contains(name))
                {
                    hasUdfAttribute = true;
                    break;
                }
            }
            if (hasUdfAttribute) break;
        }

        if (!hasUdfAttribute)
            return;

        // Semantic confirmation — verify the attribute is from the UdfFramework namespace
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(attr, context.CancellationToken);
                var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                var ns = symbol?.ContainingType?.ContainingNamespace?.ToDisplayString();

                if (ns != null && ns.Contains("UdfFramework", StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.Text));
                    return;
                }
            }
        }
    }
}
