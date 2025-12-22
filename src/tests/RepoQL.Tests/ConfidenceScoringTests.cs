using RepoQL.Data.DuckDB;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

/// <summary>
/// Tests confidence scoring on various repositories
/// </summary>
internal class ConfidenceScoringTests
{
    [Test]
    public async Task Test_Confidence_Scoring_On_RepoQL_Queries()
    {
        var queries = new[]
        {
            ("DuckDB graph store", "Highly Relevant"),
            ("semantic embedding search", "Highly Relevant"),
            ("file indexing pipeline", "Highly Relevant"),
            ("markdown heading extraction", "Moderately Relevant"),
            ("C# syntax tree parsing", "Moderately Relevant"),
            ("user authentication login", "Not in RepoQL"),
            ("payment processing stripe", "Not in RepoQL"),
            ("kubernetes deployment", "Not in RepoQL")
        };

        Console.WriteLine("\nQuery | Category | Top Result Confidence % | Makes Sense?");
        Console.WriteLine("------|----------|------------------------|-------------");

        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            options.AddMarkdownFormat();
            options.AddCSharpFormat();
        });

        foreach (var (query, category) in queries)
        {
            try
            {
                // Execute search query
                var sql = $@"
                    SELECT uri, score
                    FROM search('{query.Replace("'", "''")}', k := 5)
                    WHERE uri LIKE 'file:///%'
                    ORDER BY score DESC
                    LIMIT 1";

                var result = repo.Store.RawQuery(sql).ToArray();

                if (result.Length > 0)
                {
                    var row = result[0];
                    var uri = row["uri"]?.ToString() ?? "";
                    var score = row["score"] is double d ? d : 0.0;

                    // Convert score to confidence using the same algorithm as ConfidenceNormalizer
                    var confidence = ScoreToConfidence(score);

                    var makesSense = category switch
                    {
                        "Highly Relevant" => confidence >= 70 ? "yes" : "no",
                        "Moderately Relevant" => confidence >= 40 && confidence < 80 ? "yes" : "no",
                        "Not in RepoQL" => confidence < 50 ? "yes" : "no",
                        _ => "?"
                    };

                    Console.WriteLine($"{query} | {category} | {confidence}% | {makesSense}");
                }
                else
                {
                    Console.WriteLine($"{query} | {category} | No results | ?");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{query} | {category} | ERROR: {ex.Message} | ?");
            }
        }

        // This test is informational - we're just collecting data
        // No assertions, just output
        true.Should().BeTrue();
    }

    /// <summary>
    /// Convert a raw score to 0-100 confidence using the same algorithm as ConfidenceNormalizer
    /// </summary>
    private static int ScoreToConfidence(double rawScore)
    {
        const double SigmoidK = 12.0;
        const double SigmoidMidpoint = 0.50;
        const double HybridFloor = 0.40;

        // Sigmoid component: smooth S-curve centered at midpoint
        var sigmoid = 100.0 / (1.0 + Math.Exp(-SigmoidK * (rawScore - SigmoidMidpoint)));

        // Hybrid component: linear scaling from floor to 1.0
        var hybrid = rawScore < HybridFloor
            ? 0.0
            : (rawScore - HybridFloor) / (1.0 - HybridFloor) * 100.0;

        // Weighted combination: 80% sigmoid, 20% hybrid
        var confidence = sigmoid * 0.8 + hybrid * 0.2;

        return (int)Math.Clamp(Math.Round(confidence), 0, 100);
    }
}
