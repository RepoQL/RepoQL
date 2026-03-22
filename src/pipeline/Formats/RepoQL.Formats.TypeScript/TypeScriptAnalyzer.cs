using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.TypeScript;

public sealed class TypeScriptAnalyzer : IFormatAnalyzer
{
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.Kind is "code.typescript" or "code.typescript.react" or "code.javascript" or "code.javascript.react";
    }

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        // Placeholder: structure-only parser emits no analysis results yet.
        await Task.CompletedTask;
        yield break;
    }
}
