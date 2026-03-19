#!/usr/bin/env dotnet-script
// Voyage AI embedding model speed comparison
// Usage: set VOYAGE_API_KEY=<key> && dotnet script tools/voyage-benchmark.csx

#r "nuget: System.Text.Json, 9.0.0"

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var apiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new Exception("Set VOYAGE_API_KEY environment variable");

var baseUrl = "https://api.voyageai.com/v1";
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

// --- Test content: real C# code at different sizes ---
var small = """
    public async Task<PipelineResult> ProcessItemAsync(IClassifiedArtifact item, CancellationToken cancellationToken)
    {
        if (!string.Equals(item.MediaType?.Kind, "code.csharp", StringComparison.OrdinalIgnoreCase))
            return await next(item).ConfigureAwait(false);
        var records = loader.Materialize(documentModel);
        return (records, PipelineResult.Success);
    }
    """;

var medium = string.Join("\n", Enumerable.Range(0, 50).Select(i =>
    $"public void Method{i}(string arg) {{ Console.WriteLine(arg); var x = arg.Length * {i}; }}"));

var large = string.Join("\n", Enumerable.Range(0, 200).Select(i =>
    $"/// <summary>Processes batch {i} with validation and error handling.</summary>\n" +
    $"public async Task<Result<int>> ProcessBatch{i}Async(IReadOnlyList<Item> items, CancellationToken ct)\n" +
    $"{{\n    ArgumentNullException.ThrowIfNull(items);\n    var filtered = items.Where(x => x.IsValid).ToList();\n" +
    $"    return filtered.Count > 0 ? Result.Ok(filtered.Count) : Result.Fail<int>(\"Empty batch {i}\");\n}}"));

var sizes = new (string Name, string Text)[] { ("small (~100tok)", small), ("medium (~500tok)", medium), ("large (~5000tok)", large) };

// --- Models to test ---
var embeddingModels = new[] { "voyage-context-3", "voyage-4-large", "voyage-4", "voyage-4-lite" };

// --- Standard embedding endpoint ---
async Task<(int tokens, double ms)> EmbedStandard(string model, string text)
{
    var body = JsonSerializer.Serialize(new
    {
        input = new[] { text },
        model = model,
        input_type = "document",
        output_dtype = "int8"
    });
    var sw = Stopwatch.StartNew();
    var resp = await http.PostAsync($"{baseUrl}/embeddings",
        new StringContent(body, Encoding.UTF8, "application/json"));
    sw.Stop();
    var json = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        return (-1, sw.Elapsed.TotalMilliseconds);
    var doc = JsonDocument.Parse(json);
    var tokens = doc.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32();
    return (tokens, sw.Elapsed.TotalMilliseconds);
}

// --- Contextual embedding endpoint ---
async Task<(int tokens, double ms)> EmbedContextual(string model, string text)
{
    // Split into context (first 20%) and chunks
    var splitAt = Math.Max(100, text.Length / 5);
    var context = text[..splitAt];
    var chunk = text[splitAt..];

    // Voyage contextual format: inputs is array of arrays [[context, chunk1, ...], ...]
    var body = JsonSerializer.Serialize(new
    {
        inputs = new[] { new[] { context, chunk } },
        model = model,
        input_type = "document",
        output_dtype = "int8"
    });
    var sw = Stopwatch.StartNew();
    var resp = await http.PostAsync($"{baseUrl}/contextualizedembeddings",
        new StringContent(body, Encoding.UTF8, "application/json"));
    sw.Stop();
    var json = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        return (-1, sw.Elapsed.TotalMilliseconds);
    var doc = JsonDocument.Parse(json);
    var tokens = doc.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32();
    return (tokens, sw.Elapsed.TotalMilliseconds);
}

// --- Run benchmarks ---
Console.WriteLine("Voyage AI Embedding Model Speed Comparison");
Console.WriteLine("==========================================");
Console.WriteLine($"Endpoint: {baseUrl}");
Console.WriteLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
Console.WriteLine();

// Warmup
Console.Write("Warming up... ");
await EmbedStandard("voyage-4-lite", "warmup");
Console.WriteLine("done.");
Console.WriteLine();

// Standard embeddings
Console.WriteLine("=== Standard Embeddings (/v1/embeddings) ===");
Console.WriteLine($"{"Model",-22} {"Size",-16} {"Tokens",7} {"Latency",10} {"tok/s",8}");
Console.WriteLine(new string('-', 67));

foreach (var model in embeddingModels)
{
    foreach (var (sizeName, text) in sizes)
    {
        // 3 runs, take median
        var runs = new List<(int tok, double ms)>();
        for (var i = 0; i < 3; i++)
        {
            var r = await EmbedStandard(model, text);
            runs.Add(r);
            await Task.Delay(100); // rate limit courtesy
        }

        if (runs[0].tok == -1)
        {
            Console.WriteLine($"{model,-22} {sizeName,-16} {"FAIL",7} {runs[0].ms,9:F0}ms");
            continue;
        }

        var sorted = runs.OrderBy(r => r.ms).ToList();
        var median = sorted[1];
        var tokPerSec = median.tok / (median.ms / 1000.0);
        Console.WriteLine($"{model,-22} {sizeName,-16} {median.tok,7} {median.ms,9:F0}ms {tokPerSec,7:F0}/s");
    }
}

Console.WriteLine();

// Contextual embeddings (only context-3 supports this officially, but try others)
Console.WriteLine("=== Contextual Embeddings (/v1/contextualizedembeddings) ===");
Console.WriteLine($"{"Model",-22} {"Size",-16} {"Tokens",7} {"Latency",10} {"tok/s",8}");
Console.WriteLine(new string('-', 67));

foreach (var model in embeddingModels)
{
    foreach (var (sizeName, text) in sizes)
    {
        var runs = new List<(int tok, double ms)>();
        for (var i = 0; i < 3; i++)
        {
            try
            {
                var r = await EmbedContextual(model, text);
                runs.Add(r);
            }
            catch (Exception ex)
            {
                runs.Add((-1, 0));
            }
            await Task.Delay(100);
        }

        if (runs[0].tok == -1)
        {
            Console.WriteLine($"{model,-22} {sizeName,-16} {"N/A",7} {"timeout",10}  (not supported or too slow)");
            break; // skip other sizes for unsupported models
        }

        var sorted = runs.OrderBy(r => r.ms).ToList();
        var median = sorted[1];
        var tokPerSec = median.tok / (median.ms / 1000.0);
        Console.WriteLine($"{model,-22} {sizeName,-16} {median.tok,7} {median.ms,9:F0}ms {tokPerSec,7:F0}/s");
    }
}

Console.WriteLine();
Console.WriteLine("Done. 3 runs per cell, median shown.");
