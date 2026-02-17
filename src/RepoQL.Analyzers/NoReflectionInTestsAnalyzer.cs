using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects reflection calls (GetMethod, GetProperty, MethodInfo.Invoke, etc.) in test projects.
/// Tests should use InternalsVisibleTo and call methods directly — reflection hides signature
/// changes until runtime.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoReflectionInTestsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Avoid reflection in tests",
        "'{0}' uses reflection to access non-public members. Make the member internal and call it directly — InternalsVisibleTo is already configured.",
        "RepoQL.Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reflection in tests hides signature changes until runtime. " +
                     "Use InternalsVisibleTo (already configured via MakeInternalsVisibleToTests.targets) " +
                     "and call internal members directly so changes break at compile time.");

    private static readonly ImmutableHashSet<string> ReflectionMethodNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "GetMethod",
        "GetProperty",
        "GetField",
        "GetEvent",
        "GetMember");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        // Only fire in test projects
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!filePath.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var invocation = (InvocationExpressionSyntax)context.Node;

        // Match typeof(T).GetMethod(...), obj.GetType().GetMethod(...), etc.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        if (!ReflectionMethodNames.Contains(methodName))
        {
            return;
        }

        // Check for BindingFlags.NonPublic in the arguments — that's the signal
        // that this is accessing something that should be internal instead
        var hasNonPublicFlag = false;
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var argText = arg.ToString();
            if (argText.Contains("NonPublic", StringComparison.Ordinal))
            {
                hasNonPublicFlag = true;
                break;
            }
        }

        if (!hasNonPublicFlag)
        {
            return;
        }

        // Semantic check — confirm this is System.Reflection
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        var containingType = symbol?.ContainingType?.ToDisplayString();

        if (containingType == null ||
            (!containingType.StartsWith("System.Type", StringComparison.Ordinal) &&
             !containingType.StartsWith("System.Reflection", StringComparison.Ordinal)))
        {
            return;
        }

        // Get the enclosing method name for the diagnostic message
        var enclosingMethod = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        var location = enclosingMethod?.Identifier.Text ?? Path.GetFileName(filePath);

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), location));
    }
}
