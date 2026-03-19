#!/usr/bin/env dotnet-script
// Test embedding space compatibility between voyage-context-3 and voyage-4-lite
// Usage: set VOYAGE_API_KEY=<key> && dotnet script tools/voyage-compat-test.csx

#r "nuget: System.Text.Json, 9.0.0"

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var apiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new Exception("Set VOYAGE_API_KEY environment variable");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var texts = new[]
{
    "public async Task<PipelineResult> ProcessItemAsync(IClassifiedArtifact item, CancellationToken ct)",
    "authentication middleware JWT token validation",
    "DuckDB single writer pattern for concurrent access",
    "The quick brown fox jumps over the lazy dog",
};

async Task<float[]> EmbedStandard(string model, string text)
{
    var body = JsonSerializer.Serialize(new
    {
        input = new[] { text },
        model = model,
        input_type = "query"
    });
    var resp = await http.PostAsync("https://api.voyageai.com/v1/embeddings",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var json = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) throw new Exception($"{model} failed: {json}");
    var doc = JsonDocument.Parse(json);
    var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
    return arr.EnumerateArray().Select(e => e.GetSingle()).ToArray();
}

async Task<float[]> EmbedContextual(string text)
{
    var body = JsonSerializer.Serialize(new
    {
        inputs = new[] { new[] { text } },
        model = "voyage-context-3",
        input_type = "query"
    });
    var resp = await http.PostAsync("https://api.voyageai.com/v1/contextualizedembeddings",
        new StringContent(body, Encoding.UTF8, "application/json"));
    var json = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) throw new Exception($"context-3 contextual failed: {json}");
    var doc = JsonDocument.Parse(json);
    var outerData = doc.RootElement.GetProperty("data");
    var innerData = outerData[0].GetProperty("data");
    var arr = innerData[0].GetProperty("embedding");
    return arr.EnumerateArray().Select(e => e.GetSingle()).ToArray();
}

double Cosine(float[] a, float[] b)
{
    if (a.Length != b.Length) return -999; // dimension mismatch
    double dot = 0, normA = 0, normB = 0;
    for (var i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        normA += a[i] * a[i];
        normB += b[i] * b[i];
    }
    return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
}

Console.WriteLine("Voyage Embedding Space Compatibility Test");
Console.WriteLine("=========================================");
Console.WriteLine();

// Get embeddings from all models
var models = new[] { "voyage-context-3", "voyage-4-large", "voyage-4", "voyage-4-lite" };

foreach (var text in texts)
{
    Console.WriteLine($"Text: \"{text[..Math.Min(60, text.Length)]}...\"");

    var embeddings = new Dictionary<string, float[]>();

    // Standard endpoint for v4 models
    foreach (var model in models)
    {
        if (model == "voyage-context-3") continue; // doesn't support standard
        try
        {
            embeddings[model] = await EmbedStandard(model, text);
            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {model}: FAILED ({ex.Message[..Math.Min(80, ex.Message.Length)]})");
        }
    }

    // Contextual endpoint for context-3
    try
    {
        embeddings["context-3 (ctx)"] = await EmbedContextual(text);
        await Task.Delay(100);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  context-3 contextual: FAILED ({ex.Message[..Math.Min(80, ex.Message.Length)]})");
    }

    // Print dimensions
    foreach (var (name, vec) in embeddings)
        Console.Write($"  {name}: dim={vec.Length}  ");
    Console.WriteLine();

    // Cosine similarity matrix
    var names = embeddings.Keys.ToList();
    for (var i = 0; i < names.Count; i++)
    {
        for (var j = i + 1; j < names.Count; j++)
        {
            var sim = Cosine(embeddings[names[i]], embeddings[names[j]]);
            Console.WriteLine($"  cos({names[i]}, {names[j]}) = {sim:F4}");
        }
    }
    Console.WriteLine();
}

Console.WriteLine("Interpretation:");
Console.WriteLine("  > 0.90 = same space, interchangeable");
Console.WriteLine("  0.70-0.90 = related but drift, transition ok");
Console.WriteLine("  < 0.70 = different spaces, must re-embed");
