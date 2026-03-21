using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects classes containing [ScalarUdf]/[StructuredUdf] methods but missing [UdfClass],
/// and classes containing [Command] methods but missing [CommandClass].
/// Without the class-level attribute, auto-discovery silently skips the class.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingDiscoveryAttributeAnalyzer : DiagnosticAnalyzer
{
    public const string UdfDiagnosticId = "RQL005";
    public const string CommandDiagnosticId = "RQL006";

    private static readonly DiagnosticDescriptor UdfRule = new(
        UdfDiagnosticId,
        "Class with UDF methods is missing [UdfClass]",
        "'{0}' contains UDF methods but is missing [UdfClass] — it will not be discovered at startup",
        "RepoQL.UDF",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Classes containing [ScalarUdf] or [StructuredUdf] methods must be marked with [UdfClass] " +
                     "for the UDF registry to discover them. Without it, the UDFs silently don't register.");

    private static readonly DiagnosticDescriptor CommandRule = new(
        CommandDiagnosticId,
        "Class with Command methods is missing [CommandClass]",
        "'{0}' contains [Command] methods but is missing [CommandClass] — it will not be discovered at startup",
        "RepoQL.Commands",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Classes containing [Command] methods must be marked with [CommandClass] " +
                     "for the command registry to discover them. Without it, the commands silently don't register.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UdfRule, CommandRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        // Check class-level attributes
        var hasUdfClass = false;
        var hasCommandClass = false;

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = GetAttributeName(attr);
                if (name == "UdfClass" || name == "UdfClassAttribute")
                    hasUdfClass = true;
                else if (name == "CommandClass" || name == "CommandClassAttribute")
                    hasCommandClass = true;
            }
        }

        // Scan methods for UDF or Command attributes
        var hasUdfMethods = false;
        var hasCommandMethods = false;

        foreach (var member in classDecl.Members)
        {
            if (member is not MethodDeclarationSyntax method)
                continue;

            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = GetAttributeName(attr);
                    if (name == "ScalarUdf" || name == "ScalarUdfAttribute" ||
                        name == "StructuredUdf" || name == "StructuredUdfAttribute")
                    {
                        hasUdfMethods = true;
                    }
                    else if (name == "Command" || name == "CommandAttribute")
                    {
                        hasCommandMethods = true;
                    }
                }
            }
        }

        // Report missing class-level attributes
        if (hasUdfMethods && !hasUdfClass)
        {
            // Semantic confirmation — verify the method attribute is actually from UdfFramework
            if (ConfirmNamespace(context, classDecl, "ScalarUdf", "StructuredUdf", "UdfFramework"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UdfRule, classDecl.Identifier.GetLocation(), classDecl.Identifier.Text));
            }
        }

        if (hasCommandMethods && !hasCommandClass)
        {
            // Semantic confirmation — verify the method attribute is actually from RepoQL.Commands
            if (ConfirmNamespace(context, classDecl, "Command", null, "RepoQL.Commands"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    CommandRule, classDecl.Identifier.GetLocation(), classDecl.Identifier.Text));
            }
        }
    }

    private static string? GetAttributeName(AttributeSyntax attr) =>
        attr.Name switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => null,
        };

    /// <summary>
    /// Confirms that at least one method attribute in the class resolves to the expected namespace.
    /// </summary>
    private static bool ConfirmNamespace(SyntaxNodeAnalysisContext context,
        ClassDeclarationSyntax classDecl, string attrName1, string? attrName2, string expectedNs)
    {
        foreach (var member in classDecl.Members)
        {
            if (member is not MethodDeclarationSyntax method)
                continue;

            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = GetAttributeName(attr);
                    if (name == null)
                        continue;

                    var baseName = name.EndsWith("Attribute", StringComparison.Ordinal)
                        ? name.Substring(0, name.Length - 9)
                        : name;

                    if (!baseName.Equals(attrName1, StringComparison.Ordinal) &&
                        (attrName2 == null || !baseName.Equals(attrName2, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var symbolInfo = context.SemanticModel.GetSymbolInfo(attr, context.CancellationToken);
                    var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                    var ns = symbol?.ContainingType?.ContainingNamespace?.ToDisplayString();

                    if (ns != null && ns.Contains(expectedNs, StringComparison.Ordinal))
                        return true;
                }
            }
        }

        return false;
    }
}
