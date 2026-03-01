using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects `new DuckDBConnection(...)` outside DuckDbDataStore and tests.
/// All DuckDB connections must go through the single-writer DuckDbDataStore.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuckDbConnectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL003";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "DuckDB connections must go through DuckDbDataStore",
        "Do not create DuckDBConnection directly — use DuckDbDataStore to ensure single-writer safety",
        "RepoQL.Data",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "All DuckDB access must go through DuckDbDataStore to maintain single-writer consistency. " +
                     "Use WriteTransaction or ReadQuery callbacks instead of raw connections.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        // Quick syntax filter — only ObjectCreationExpression has the type name visible
        if (context.Node is ObjectCreationExpressionSyntax creation)
        {
            var typeName = creation.Type switch
            {
                SimpleNameSyntax simple => simple.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                _ => null,
            };

            if (typeName != "DuckDBConnection")
                return;
        }
        else
        {
            // ImplicitObjectCreation (new(...)) — need semantic model, but rare for this type
            return;
        }

        // Allow in DuckDbDataStore and its extensions
        var filePath = context.Node.SyntaxTree.FilePath;
        var fileName = Path.GetFileName(filePath);
        if (fileName.Equals("DuckDbDataStore.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("DuckDbDataStoreExtensions.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("EmbeddingCache.cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Allow in test projects
        if (filePath.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Semantic check — confirm this is actually DuckDB.NET.Data.DuckDBConnection
        var symbolInfo = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        var containingType = symbol?.ContainingType;
        var ns = containingType?.ContainingNamespace?.ToDisplayString();

        if (ns == null || !ns.StartsWith("DuckDB.NET", StringComparison.Ordinal))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
    }
}
