using System.Text;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for embedding operations.
/// Provides functions to embed text and check embedding provider status.
/// </summary>
[UdfClass]
public class EmbedUdf(IEmbeddingProvider? embeddingProvider)
{
    /// <summary>
    /// Returns status information about the embedding provider.
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_embed_status_internal", MacroName = "embed_status", Description = "Returns status information about the embedding provider")]
    public string EmbedStatus([UdfDefault("''")] string? _dummy)
    {
        var providerType = embeddingProvider?.GetType().Name ?? "null";
        var enabled = embeddingProvider?.Enabled ?? false;
        var model = embeddingProvider?.Model ?? "null";
        var dimension = embeddingProvider?.Dimension ?? 0;

        return $"provider_type: {providerType}\nenabled: {enabled}\nmodel: {model}\ndimension: {dimension}";
    }

    /// <summary>
    /// Embeds text and returns a JSON array of floats representing the embedding vector.
    /// Returns null if the embedding provider is not configured or if embedding fails.
    /// Use ::FLOAT[] in SQL to cast the result.
    /// </summary>
    [ScalarUdf("embed_text", Description = "Embed text and return JSON array of floats (use ::FLOAT[] to cast)")]
    public string? EmbedText(string text)
    {
        if (embeddingProvider is null || !embeddingProvider.Enabled)
        {
            return null;
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        float[]?[] vectors;
        try
        {
            vectors = embeddingProvider.EmbedBatchAsync([text], CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        if (vectors.Length == 0 || vectors[0] is null)
        {
            return null;
        }

        return SerializeFloatArray(vectors[0]!);
    }

    /// <summary>
    /// Serializes float array to JSON array format [1.0,2.0,...].
    /// Use result::FLOAT[] in SQL to convert back.
    /// </summary>
    private static string SerializeFloatArray(float[] vec)
    {
        if (vec == null || vec.Length == 0) return "[]";

        var sb = new StringBuilder(vec.Length * 10 + 2); // Pre-size for efficiency
        sb.Append('[');
        for (var i = 0; i < vec.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
