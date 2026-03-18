using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Purpose: Accurate token counting for Voyage AI API limits using the model's actual tokenizer.
/// Complexity: Loads embedded tokenizer.json (Voyage BPE/Qwen2), extracts vocab + merges,
/// builds a BpeTokenizer. Falls back to conservative char-based estimate if load fails.
/// </summary>
internal sealed class VoyageTokenCounter
{
    private const string EmbeddedResourceName = "RepoQL.Data.DuckDB.Resources.voyage-tokenizer.json";
    private const int FallbackCharsPerToken = 2; // Conservative: overestimates tokens → safe splits

    private readonly ILogger? _logger;
    private BpeTokenizer? _tokenizer;
    private bool _loadAttempted;
    private readonly object _lock = new();
    private readonly Func<string, int>? _customCounter;

    public VoyageTokenCounter(ILogger? logger = null)
    {
        _logger = logger;
    }

    private VoyageTokenCounter(Func<string, int> customCounter)
    {
        _customCounter = customCounter;
        _loadAttempted = true;
    }

    /// <summary>Creates a counter where 1 char = 1 token. For tests that use char-based limits.</summary>
    internal static VoyageTokenCounter CharBased() => new(text => text.Length);

    /// <summary>
    /// Count tokens in text. Uses the Voyage tokenizer if available, otherwise falls back
    /// to a conservative char-based estimate (2 chars/token = overestimates = safe).
    /// </summary>
    public int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        if (_customCounter is not null)
            return _customCounter(text);

        var tokenizer = GetTokenizer();
        if (tokenizer is not null)
        {
            try
            {
                return tokenizer.CountTokens(text);
            }
            catch
            {
                // Tokenizer failed on this input — fall back to estimate
            }
        }

        return (text.Length + FallbackCharsPerToken - 1) / FallbackCharsPerToken;
    }

    /// <summary>
    /// Count tokens for context + all chunks combined (a single Voyage group).
    /// </summary>
    public int CountGroupTokens(string? context, IReadOnlyList<string> chunks)
    {
        var total = CountTokens(context);
        foreach (var chunk in chunks)
            total += CountTokens(chunk);
        return total;
    }

    private BpeTokenizer? GetTokenizer()
    {
        if (_tokenizer is not null)
            return _tokenizer;

        lock (_lock)
        {
            if (_tokenizer is not null)
                return _tokenizer;

            if (_loadAttempted)
                return null;

            _loadAttempted = true;

            try
            {
                _tokenizer = LoadEmbeddedTokenizer();
                _logger?.LogInformation("Loaded Voyage BPE tokenizer ({VocabSize} vocab entries)",
                    _tokenizer.Vocabulary.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to load Voyage tokenizer, falling back to char-based estimates");
            }

            return _tokenizer;
        }
    }

    /// <summary>
    /// Loads the embedded tokenizer.json, extracts vocab + merges, builds a BpeTokenizer.
    /// </summary>
    private BpeTokenizer LoadEmbeddedTokenizer()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {EmbeddedResourceName} not found");

        return BuildFromTokenizerJson(stream);
    }

    /// <summary>
    /// Parses a HuggingFace tokenizer.json and extracts vocab + merges for BpeTokenizer.
    /// The JSON structure has model.vocab (object) and model.merges (array of "token1 token2" strings).
    /// </summary>
    internal static BpeTokenizer BuildFromTokenizerJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var model = doc.RootElement.GetProperty("model");

        // Extract vocab: { "token": id, ... } → write as JSON to temp stream
        var vocabElement = model.GetProperty("vocab");
        using var vocabStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(vocabStream))
            vocabElement.WriteTo(writer);
        vocabStream.Position = 0;

        // Extract merges: ["token1 token2", ...] → write as lines to temp stream
        var mergesElement = model.GetProperty("merges");
        using var mergesStream = new MemoryStream();
        using (var sw = new StreamWriter(mergesStream, leaveOpen: true))
        {
            foreach (var merge in mergesElement.EnumerateArray())
                sw.WriteLine(merge.GetString());
        }
        mergesStream.Position = 0;

        return BpeTokenizer.Create(vocabStream, mergesStream);
    }
}
