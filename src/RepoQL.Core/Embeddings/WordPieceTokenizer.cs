using System.Text.Json;

namespace RepoQL.Core.Embeddings;

/// <summary>
/// Minimal WordPiece tokenizer sufficient for the shipped BGE model.
///
/// It loads vocabulary and special token IDs from tokenizer.json and performs:
/// - optional lowercasing
/// - basic pre-tokenization on whitespace/punctuation
/// - WordPiece segmentation with '##' continuation
/// - Adds [CLS] and [SEP], pads to max length with [PAD]
///
/// This is not a complete HuggingFace tokenizer, but it’s adequate and fast for our usage.
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
    public int ClsId => _clsId;
    public int SepId => _sepId;
    public int PadId => _padId;
    public int UnkId => _unkId;

    /// <summary>
    /// Construct a tokenizer instance from a HuggingFace-style tokenizer.json payload.
    /// </summary>
    public static WordPieceTokenizer LoadFromTokenizerJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var model = root.GetProperty("model");
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in model.GetProperty("vocab").EnumerateObject())
        {
            vocab[kv.Name] = kv.Value.GetInt32();
        }
        var norm = root.TryGetProperty("normalizer", out var n) ? n : default;
        var lowercase = norm.ValueKind == JsonValueKind.Object && norm.TryGetProperty("lowercase", out var lc) && lc.GetBoolean();
        var cls = vocab.GetValueOrDefault("[CLS]", 101);
        var sep = vocab.GetValueOrDefault("[SEP]", 102);
        var pad = vocab.GetValueOrDefault("[PAD]", 0);
        var unk = vocab.GetValueOrDefault("[UNK]", 100);
        return new WordPieceTokenizer(vocab, lowercase, cls, sep, pad, unk);
    }

    /// <summary>
    /// Encode text into token IDs and attention mask, including [CLS]/[SEP], padded to <paramref name="maxLen"/>.
    /// </summary>
    public EncodingResult Encode(string text, int maxLen)
    {
        // Basic normalize
        if (_lower) text = text.ToLowerInvariant();
        var pieces = PreTokenize(text);
        // Collect subword IDs excluding special tokens first
        var subIds = new List<int>(maxLen);
        foreach (var piece in pieces)
        {
            var remaining = Math.Max(0, maxLen - 2 - subIds.Count); // room for SEP
            if (remaining == 0) break;
            var consumed = WordPiece(piece, subIds, remaining);
            if (consumed == 0) continue;
        }

        var truncated = subIds.Count > maxLen - 2;
        if (truncated)
        {
            subIds.RemoveRange(maxLen - 2, subIds.Count - (maxLen - 2));
        }

        // Assemble final sequence: [CLS] + subIds + [SEP]
        var ids = new List<int>(maxLen) { _clsId };
        ids.AddRange(subIds);
        ids.Add(_sepId);

        // Build mask + pad
        var attn = new List<int>(ids.Count);
        for (var i = 0; i < ids.Count; i++) attn.Add(1);
        while (ids.Count < maxLen) { ids.Add(_padId); attn.Add(0); }
        return new EncodingResult([.. ids], [.. attn], truncated);
    }

    /// <summary>
    /// Very small pre-tokenizer: split on whitespace and punctuation.
    /// </summary>
    private static IEnumerable<string> PreTokenize(string text)
    {
        // Simple split on whitespace and punctuation (approximation of BertPreTokenizer)
        var buff = new List<char>(32);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                if (buff.Count > 0) { yield return new string([.. buff]); buff.Clear(); }
                continue;
            }
            buff.Add(ch);
        }
        if (buff.Count > 0) yield return new string([.. buff]);
    }

    /// <summary>
    /// Segment a token into WordPiece subwords, appending IDs into <paramref name="ids"/> until <paramref name="remaining"/> is exhausted.
    /// </summary>
    private int WordPiece(string token, List<int> ids, int remaining)
    {
        if (string.IsNullOrEmpty(token) || remaining <= 0) return 0;
        var start = 0;
        var consumed = 0;
        while (start < token.Length && remaining > 0)
        {
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
                ids.Add(_unkId);
                consumed++;
                break;
            }
            ids.Add(matchedId);
            remaining--;
            consumed++;
            start = end;
        }
        return consumed;
    }
}