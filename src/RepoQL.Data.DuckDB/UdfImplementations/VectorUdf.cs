using System.Text.Json;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for vector similarity calculations.
///
/// Purpose: Provides SQL-callable functions for computing similarity
/// between embedding vectors stored as JSON arrays.
///
/// Complexity: JSON parsing and SIMD-friendly dot product. Pure functions.
/// </summary>
[UdfClass]
public class VectorUdf
{
    /// <summary>
    /// Computes cosine similarity between two JSON-encoded float arrays.
    /// Returns value between 0 and 1 (1 = identical direction).
    /// Returns "0" for invalid inputs, mismatched lengths, or zero-magnitude vectors.
    /// </summary>
    [ScalarUdf("cosine_similarity_json", IsPure = true)]
    public string CosineSimilarity(string? vectorAJson, [UdfDefault("NULL")] string? vectorBJson)
    {
        if (string.IsNullOrWhiteSpace(vectorAJson) || string.IsNullOrWhiteSpace(vectorBJson))
            return "0";

        var av = ParseFloatArray(vectorAJson);
        var bv = ParseFloatArray(vectorBJson);

        if (av is null || bv is null || av.Length == 0 || bv.Length == 0 || av.Length != bv.Length)
            return "0";

        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;

        for (var i = 0; i < av.Length; i++)
        {
            var x = av[i];
            var y = bv[i];
            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        var similarity = denominator > 0 ? dot / denominator : 0.0;

        return similarity.ToString("G17");
    }

    #region Helper Methods

    private static float[]? ParseFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var arr = new float[doc.RootElement.GetArrayLength()];
            var idx = 0;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                arr[idx++] = element.TryGetSingle(out var f) ? f : (float)element.GetDouble();
            }

            return arr;
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
