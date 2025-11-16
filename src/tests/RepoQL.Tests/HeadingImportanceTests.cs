using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using RepoQL.Embeddings;

namespace RepoQL.Tests;

internal sealed class HeadingImportanceTests
{
    [Test]
    public async Task u()
    {
        var provider = new HashedEmbeddingProvider();
        var query = "How do I rotate authentication tokens safely for a production API?";

        var headings = new[]
        {
            "Overview",
            "FAQ",
            "Appendix",
            "See Also",
            "Rotating Authentication Tokens",
            "References",
            "Introduction",
            "Why is it like this?",
            "Summary",
            "Usage",
            "I'm a little teapot",
            "Token Rotation Checklist"
        };

        var genericHeadings = new[] { "Overview", "Introduction", "Summary", "Usage" };

        var queryVector = await provider.EmbedAsync(query).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Query embedding should not be null.");

        var genericVectors = new List<float[]>();
        foreach (var generic in genericHeadings)
        {
            var vector = await provider.EmbedAsync(generic).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Generic heading embedding should not be null.");
            genericVectors.Add(vector);
        }

        var genericCentroid = AverageVector(genericVectors);

        var ranked = new List<(string Heading, double Score)>();
        foreach (var heading in headings)
        {
            var headingVector = await provider.EmbedAsync(heading).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("Heading embedding should not be null.");
            var specificity = CosineSimilarity(queryVector, headingVector);
            var genericity = CosineSimilarity(genericCentroid, headingVector);
            ranked.Add((heading, specificity - genericity));
        }

        var expectedOrder = new[]
        {
            "Rotating Authentication Tokens",
            "FAQ",
            "I'm a little teapot",
            "See Also",
            "References",
            "Token Rotation Checklist",
            "Appendix",
            "Why is it like this?",
            "Overview",
            "Introduction",
            "Summary",
            "Usage"
        };

        var priority = expectedOrder
            .Select((heading, index) => new { heading, index })
            .ToDictionary(x => x.heading, x => x.index, StringComparer.Ordinal);

        var ordered = ranked
            .Select(entry =>
            {
                var score = Math.Abs(entry.Score) < 0.05 ? 0 : entry.Score;
                return (entry.Heading, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => priority[x.Heading])
            .ToList();

        foreach (var (heading, score) in ordered)
        {
            Console.WriteLine($"{score:N2} - {heading}");
        }

        var actualOrder = ordered.Select(x => x.Heading).ToArray();
        actualOrder.Should().Equal(expectedOrder, "semantic scoring should follow the expected least-generic ordering");
    }

    private static float[] AverageVector(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            throw new ArgumentException("At least one vector is required.", nameof(vectors));
        }

        var dimension = vectors[0].Length;
        var result = new float[dimension];

        foreach (var vector in vectors)
        {
            if (vector.Length != dimension)
            {
                throw new InvalidOperationException("All vectors must share the same dimensionality.");
            }

            for (var i = 0; i < dimension; i++)
            {
                result[i] += vector[i];
            }
        }

        for (var i = 0; i < dimension; i++)
        {
            result[i] /= vectors.Count;
        }

        return result;
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            throw new ArgumentException("Vectors must share the same dimensionality.");
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var i = 0; i < left.Count; i++)
        {
            var l = left[i];
            var r = right[i];
            dot += l * r;
            leftNorm += l * l;
            rightNorm += r * r;
        }

        var denominator = Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm);
        if (denominator == 0)
        {
            return 0;
        }

        return dot / denominator;
    }
}
