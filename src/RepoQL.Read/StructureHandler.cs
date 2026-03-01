using System.Text;
using CoreTokenEstimator = RepoQL.Contracts.TokenEstimator;

using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Read;

/// <summary>
/// Purpose: Renders structure-only read output for matched documents.
/// Complexity: Normalizes headline/structure formatting and token accounting so dispatch can enforce budgets.
/// </summary>
public sealed class StructureHandler : IModifierHandler
{
    private const string StructureUnavailableNote = "(structure not available for this format)";

    public string ModifierName => "structure";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        var content = BuildContent(documents, ct);
        var tokenCount = CoreTokenEstimator.EstimateTokens(content);
        var exceedsBudget = tokenCount > tokenBudget;
        var filesConsulted = documents.Select(doc => doc.Uri).ToList();

        var metadata = new ResultMetadata(
            FilesConsulted: filesConsulted,
            Warning: null,
            Extra: new Dictionary<string, object>());

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: documents.Count,
            Shown: documents.Count,
            ExceedsBudget: exceedsBudget,
            Metadata: metadata));
    }

    private static string BuildContent(IReadOnlyList<ReadDocument> documents, CancellationToken ct)
    {
        if (documents.Count == 0)
            return "No files matched.";

        var builder = new StringBuilder();

        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (builder.Length > 0)
                builder.Append("\n\n");

            builder.Append(doc.Uri);

            var headline = doc.Headline?.Trim();
            var structure = doc.Structure?.Trim();

            if (!string.IsNullOrWhiteSpace(headline))
            {
                builder.Append('\n');
                builder.Append(headline);
            }

            if (!string.IsNullOrWhiteSpace(structure))
            {
                builder.Append(!string.IsNullOrWhiteSpace(headline) ? "\n\n" : "\n");
                builder.Append(structure);
            }
            else
            {
                builder.Append('\n');
                builder.Append(StructureUnavailableNote);
            }
        }

        return builder.ToString();
    }
}
