using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RepoQL.Analyzers;

/// <summary>
/// Detects `using FluentAssertions` and reports a warning to use AwesomeAssertions instead.
/// FluentAssertions is banned due to license constraints.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseCorrectAssertionLibraryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RQL002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use AwesomeAssertions instead of FluentAssertions",
        "Replace 'using {0}' with 'using AwesomeAssertions' — FluentAssertions is not licensed for this project",
        "RepoQL.Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "FluentAssertions has licensing constraints. Use AwesomeAssertions (same API, compatible license).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingDirective);
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;
        var name = usingDirective.Name?.ToString();

        if (name == null)
            return;

        if (name.Equals("FluentAssertions", StringComparison.Ordinal) ||
            name.StartsWith("FluentAssertions.", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, usingDirective.GetLocation(), name));
        }
    }
}
