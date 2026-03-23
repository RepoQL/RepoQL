namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Resolves the currently preferred embedding model for compatibility checks.
/// Prefers the contextual provider when it is reachable, then falls back to the flat provider.
/// </summary>
public static class ActiveEmbeddingModelResolver
{
    public static string? Resolve(
        IEmbeddingProvider? flatProvider,
        IContextualEmbeddingProvider? contextualProvider)
    {
        if (contextualProvider is { Enabled: true })
        {
            try
            {
                contextualProvider.InitializeAsync().GetAwaiter().GetResult();
                var contextualModel = NormalizeModel(contextualProvider.Model);
                if (contextualModel is not null)
                    return contextualModel;
            }
            catch
            {
                // Fall back to the local provider for compatibility checks.
            }
        }

        if (flatProvider is { Enabled: true })
            return NormalizeModel(flatProvider.Model);

        return null;
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var trimmed = model.Trim();
        return string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}
