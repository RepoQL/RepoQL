using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects [UdfClass] classes that take DuckDbDataStore directly in their constructor.
/// UDFs must use IReentrantReader to avoid deadlocks during DuckDB UDF callbacks.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UdfDataStoreAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "UDF classes should not depend on DuckDbDataStore directly",
        "[UdfClass] constructor should not take DuckDbDataStore directly — use IReentrantReader to avoid deadlocks during UDF callbacks",
        "RepoQL.UDF",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "UDFs are called back by DuckDB during query execution. Taking DuckDbDataStore directly " +
                     "can cause deadlocks when the UDF tries to acquire locks already held by the calling thread. " +
                     "Use IReentrantReader instead, which bypasses locking and uses a safe reentrant connection.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeClassDeclaration, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        // Allow in test projects
        var filePath = classDecl.SyntaxTree.FilePath;
        if (filePath.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
            return;

        // Quick syntax check: class must have [UdfClass] or [UdfClassAttribute]
        if (!HasUdfClassAttribute(classDecl))
            return;

        // Check primary constructor parameters (C# 12+)
        if (classDecl.ParameterList is { } primaryParams)
        {
            foreach (var param in primaryParams.Parameters)
            {
                if (IsDuckDbDataStoreType(param.Type))
                {
                    // Semantic confirmation that the class actually has [UdfClass] from the right namespace
                    if (ConfirmUdfClassAttribute(context, classDecl))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule, param.GetLocation()));
                    }
                    return;
                }
            }
        }

        // Check explicit constructors
        foreach (var member in classDecl.Members)
        {
            if (member is not ConstructorDeclarationSyntax ctor)
                continue;

            foreach (var param in ctor.ParameterList.Parameters)
            {
                if (IsDuckDbDataStoreType(param.Type))
                {
                    if (ConfirmUdfClassAttribute(context, classDecl))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule, param.GetLocation()));
                    }
                    return;
                }
            }
        }
    }

    private static bool HasUdfClassAttribute(ClassDeclarationSyntax classDecl)
    {
        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name switch
                {
                    SimpleNameSyntax simple => simple.Identifier.Text,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    _ => null,
                };

                if (name is "UdfClass" or "UdfClassAttribute")
                    return true;
            }
        }

        return false;
    }

    private static bool IsDuckDbDataStoreType(TypeSyntax? type)
    {
        if (type is null)
            return false;

        var name = type switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => null,
        };

        return name == "DuckDbDataStore";
    }

    private static bool ConfirmUdfClassAttribute(SyntaxNodeAnalysisContext context, ClassDeclarationSyntax classDecl)
    {
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken);
        if (classSymbol is null)
            return false;

        foreach (var attr in classSymbol.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass?.Name == "UdfClassAttribute" &&
                attrClass.ContainingNamespace?.ToDisplayString() == "RepoQL.Data.DuckDB.UdfFramework")
            {
                return true;
            }
        }

        return false;
    }
}
