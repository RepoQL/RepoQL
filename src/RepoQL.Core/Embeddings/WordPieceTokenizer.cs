using System.Runtime.InteropServices;
using System.Text.Json;

namespace RepoQL.Core.Embeddings;

/// <summary>
/// Minimal WordPiece tokenizer for BGE small v1.5.
/// Preserves punctuation as standalone tokens. Adds [CLS]/[SEP] and pads to maxLen.
/// </summary>
internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly bool _lower;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private readonly int _unkId;

    private WordPieceTokenizer(Dictionary<string, int> vocab, bool lowercase, int clsId, int sepId, int padId, int unkId)
    {
        _vocab = vocab; _lower = lowercase; _clsId = clsId; _sepId = sepId; _padId = padId; _unkId = unkId;
    }

    public bool Lowercase => _lower;
    public int VocabSize => _vocab.Count;

    public static WordPieceTokenizer LoadFromTokenizerJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var model = root.GetProperty("model");
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in model.GetProperty("vocab").EnumerateObject())
            vocab[kv.Name] = kv.Value.GetInt32();

        var lowercase = false;
        if (root.TryGetProperty("normalizer", out var norm))
        {
            lowercase = FindLowercaseFlag(norm);
        }

        var cls = GetOr(vocab, "[CLS]", 101);
        var sep = GetOr(vocab, "[SEP]", 102);
        var pad = GetOr(vocab, "[PAD]", 0);
        var unk = GetOr(vocab, "[UNK]", 100);

        return new WordPieceTokenizer(vocab, lowercase, cls, sep, pad, unk);
    }

    private static int GetOr(Dictionary<string, int> d, string k, int def) => d.TryGetValue(k, out var v) ? v : def;

    private static bool FindLowercaseFlag(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, "lowercase", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True) return true;
                    if (FindLowercaseFlag(p.Value)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FindLowercaseFlag(item)) return true;
                break;
        }
        return false;
    }

    public EncodingResult Encode(string? text, int maxLen)
    {
        if (_lower && text is not null) text = text.ToLowerInvariant();

        var pieces = PreTokenize(text ?? string.Empty);

        var budget = Math.Max(2, maxLen);
        var subIds = new List<int>(Math.Min(budget, 64));
        var truncated = false;

        foreach (var piece in pieces)
        {
            if (subIds.Count >= budget - 2) { truncated = true; break; }
            var remaining = budget - 2 - subIds.Count;
            var hitBudget = WordPiece(piece, subIds, remaining); // returns true if budget exhausted mid-token
            if (hitBudget) { truncated = true; break; }
        }

        // Assemble [CLS] + subIds + [SEP] + [PAD]*
        var ids  = new int[budget];
        var attn = new int[budget];

        var idx = 0;
        ids[idx] = _clsId;  attn[idx] = 1; idx++;
        for (var i = 0; i < subIds.Count && idx < budget - 1; i++, idx++) { ids[idx] = subIds[i]; attn[idx] = 1; }
        ids[idx] = _sepId;  attn[idx] = 1; idx++;

        while (idx < budget) { ids[idx] = _padId; attn[idx] = 0; idx++; } // use PAD id explicitly

        return new EncodingResult(ids, attn, truncated);
    }

    private static IEnumerable<string> PreTokenize(string text)
    {
        var buf = new List<char>(32);
        // ReSharper disable once ForCanBeConvertedToForeach - performance
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                if (buf.Count > 0) { yield return new string(CollectionsMarshal.AsSpan(buf)); buf.Clear(); }
                continue;
            }
            if (char.IsPunctuation(ch))
            {
                if (buf.Count > 0) { yield return new string(CollectionsMarshal.AsSpan(buf)); buf.Clear(); }
                yield return ch.ToString();
                continue;
            }
            buf.Add(ch);
        }
        if (buf.Count > 0) yield return new string(CollectionsMarshal.AsSpan(buf));
    }

    // WordPiece now signals budget exhaustion
    private bool WordPiece(string token, List<int> ids, int remaining)
    {
        if (string.IsNullOrEmpty(token) || remaining <= 0) return remaining <= 0;

        var start = 0;
        while (start < token.Length)
        {
            if (remaining <= 0) return true; // hit budget mid-token

            var end = token.Length;
            var matchedId = -1;

            while (start < end)
            {
                var sub = token.Substring(start, end - start);
                if (start > 0) sub = "##" + sub;
                if (_vocab.TryGetValue(sub, out matchedId)) break;
                end--;
            }

            if (matchedId == -1)
            {
                ids.Add(_unkId); // consume the token with [UNK]
                remaining--;
                break;
            }

            ids.Add(matchedId);
            remaining--;
            start = end;
        }
        return false; // did not hit budget mid-token
    }
}