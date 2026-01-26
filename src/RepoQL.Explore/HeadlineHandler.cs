using System.Text;
using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Purpose: Renders headline-only output for read modifier requests.
/// Complexity: Normalizes headline output and token accounting so dispatch stays focused on routing and budgets.
/// </summary>
public sealed class HeadlineHandler : IModifierHandler
{
    private const string Placeholder = "(no headline available)";

    public string ModifierName => "headline";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            const string emptyMessage = "No files matched.";
            var emptyTokens = TokenEstimator.EstimateTokens(emptyMessage);
            return Task.FromResult(new ModifierResult(
                emptyMessage,
                TokenCount: emptyTokens,
                TotalAvailable: 0,
                Shown: 0,
                ExceedsBudget: emptyTokens > tokenBudget,
                Metadata: new ResultMetadata(Array.Empty<string>(), Warning: null, Extra: new Dictionary<string, object>())));
        }

        var builder = new StringBuilder();
        var filesConsulted = new List<string>(documents.Count);

        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            filesConsulted.Add(doc.Uri);

            if (builder.Length > 0)
                builder.Append('\n');

            var headline = GetSingleLineHeadline(doc.Headline) ?? Placeholder;
            builder.Append(doc.Uri);
            builder.Append(" | ");
            builder.Append(headline);
        }

        var content = builder.ToString();
        var tokenCount = TokenEstimator.EstimateTokens(content);
        var exceedsBudget = tokenCount > tokenBudget;

        return Task.FromResult(new ModifierResult(
            content,
            TokenCount: tokenCount,
            TotalAvailable: documents.Count,
            Shown: documents.Count,
            ExceedsBudget: exceedsBudget,
            Metadata: new ResultMetadata(filesConsulted, Warning: null, Extra: new Dictionary<string, object>())));
    }

    private static string? GetSingleLineHeadline(string? headline)
    {
        if (string.IsNullOrWhiteSpace(headline))
            return null;

        var newlineIndex = headline.IndexOf('\n');
        var singleLine = newlineIndex >= 0
            ? headline[..newlineIndex]
            : headline;

        return singleLine.Trim();
    }
}
