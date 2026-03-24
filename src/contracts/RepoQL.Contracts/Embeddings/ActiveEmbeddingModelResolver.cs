using RepoQL.Contracts.Cloud;

namespace RepoQL.Contracts.Embeddings;

/// <summary>
/// Resolves the currently preferred embedding model for compatibility checks.
/// Prefers the contextual provider when it is reachable, then falls back to the flat provider.
/// </summary>
public static class ActiveEmbeddingModelResolver
{
    public const string PendingContextualModel = "__repoql_contextual_pending__";

    public static string? Resolve(
        IEmbeddingProvider? flatProvider,
        IContextualEmbeddingProvider? contextualProvider,
        ICloudAuthStatusProvider? cloudAuthStatusProvider = null)
    {
        if (ShouldPreferPaidContextual(cloudAuthStatusProvider) &&
            contextualProvider is { Enabled: true })
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
                return PendingContextualModel;
            }

            return PendingContextualModel;
        }

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

    private static bool ShouldPreferPaidContextual(ICloudAuthStatusProvider? cloudAuthStatusProvider)
    {
        if (cloudAuthStatusProvider is null)
            return false;

        try
        {
            var statusTask = cloudAuthStatusProvider.GetStatusAsync();
            if (statusTask.IsCompletedSuccessfully)
                return statusTask.Result.CanUsePaidCloudFeatures;

            return statusTask.AsTask().GetAwaiter().GetResult().CanUsePaidCloudFeatures;
        }
        catch
        {
            return false;
        }
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
