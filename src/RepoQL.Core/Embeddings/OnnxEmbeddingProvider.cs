using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Core.Embeddings;

/// <summary>
/// Local-only ONNX embedding provider for the shipped BGE model (bge-small-en-v1.5).
///
/// Responsibilities
/// - Loads the ONNX encoder and the tokenizer (tokenizer.json) from disk; no network calls.
/// - Tokenizes text to WordPiece IDs with attention mask, using lowercase and special tokens
///   as configured in the tokenizer file.
/// - Builds inputs: <c>input_ids</c>, <c>attention_mask</c> (and <c>token_type_ids</c> if required).
/// - Runs inference via ONNX Runtime; supports CPU by default, attempts CUDA/DML if present.
/// - Pools the model output to a single vector (mean-pool with the mask) and L2-normalizes it.
/// - Returns a 384-dimensional vector compatible with BGE small v1.5.
///
/// Notes
/// - This implementation makes the common BGE assumptions and is intentionally minimal.
/// - If initialization fails (e.g., files missing), the provider disables itself and callers
///   should fallback to the hashed embedding provider.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly string _inputIdsName = string.Empty;
    private readonly string _attnMaskName = string.Empty;
    private readonly string? _tokenTypeName;
    private readonly string _outputName = string.Empty;
    private readonly int _maxSeqLen;

    // Minimal tokenizer (WordPiece) loaded from tokenizer.json
    private readonly WordPieceTokenizer? _tokenizer;

    /// <summary>
    /// Model identifier (defaults to <c>bge-small-en-v1.5</c> until the session is loaded, then uses file name).
    /// </summary>
    public string Model { get; } = "bge-small-en-v1.5";
    public int Dimension { get; private set; } = 384;
    public bool Enabled => _session is not null && _tokenizer is not null;

    /// <summary>
    /// Initialize provider from an ONNX model path. The tokenizer is expected to sit next to the model
    /// as <c>tokenizer.json</c> (standard HuggingFace export). If either the model or tokenizer is missing,
    /// the provider disables itself and <see cref="Enabled"/> becomes false.
    /// </summary>
    public OnnxEmbeddingProvider(string modelPath, ILogger<OnnxEmbeddingProvider>? logger = null, int? maxTokens = null)
    {
        _logger = logger ?? NullLogger<OnnxEmbeddingProvider>.Instance;
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        var modelFull = Path.GetFullPath(modelPath);
        if (!File.Exists(modelFull)) { _logger.LogWarning("ONNX model not found at {Path}", modelFull); return; }

        var modelDir = Path.GetDirectoryName(modelFull)!;
        var tokenizerJson = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { _logger.LogWarning("Tokenizer not found next to model at {Path}", tokenizerJson); return; }

        try
        {
            // Load tokenizer from the shipped JSON (WordPiece + configuration for casing and special tokens).
            _tokenizer = WordPieceTokenizer.LoadFromTokenizerJson(File.ReadAllText(tokenizerJson));
            _logger.LogInformation("Loaded tokenizer: lowercase={Lowercase} vocab={Vocab}", _tokenizer.Lowercase, _tokenizer.VocabSize);

            var so = new SessionOptions();
            try { so.AppendExecutionProvider_CUDA(); } catch { try { so.AppendExecutionProvider_DML(); } catch { /* CPU fallback */ } }
            _session = new InferenceSession(modelFull, so);
            Model = Path.GetFileNameWithoutExtension(modelFull);
            _logger.LogInformation("ONNX session created for model {Model}", Model);

            // Discover input names
            // Discover well-known input names by convention.
            foreach (var kv in _session.InputMetadata)
            {
                var name = kv.Key;
                if (string.IsNullOrEmpty(_inputIdsName) && name.Contains("input_ids")) _inputIdsName = name;
                else if (string.IsNullOrEmpty(_attnMaskName) && name.Contains("attention_mask")) _attnMaskName = name;
                else if (string.IsNullOrEmpty(_tokenTypeName) && name.Contains("token_type_ids")) _tokenTypeName = name;
            }
            if (string.IsNullOrEmpty(_inputIdsName) || string.IsNullOrEmpty(_attnMaskName))
            {
                _logger.LogWarning("Model inputs not found (input_ids/attention_mask); disabling provider");
                _session.Dispose();
                _session = null;
                return;
            }

            // Prefer the first output; BGE typically exposes last_hidden_state as the first output.
            _outputName = _session.OutputMetadata.Keys.First();
            _logger.LogDebug("Discovered model IO: input_ids={Input}, attention_mask={Mask}, token_type_ids={TypeIds}, output={Output}", _inputIdsName, _attnMaskName, _tokenTypeName ?? "<none>", _outputName);

            // Configure max sequence length (default 256 for speed; 512 if explicitly requested)
            _maxSeqLen = Math.Max(8, maxTokens ?? 256);
            _logger.LogInformation("Embedding provider configured: max_tokens={Max}", _maxSeqLen);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize ONNX embedding provider; falling back to hashed provider");
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>
    /// Encode a piece of text into a normalized embedding vector. Returns null on error.
    /// </summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_session is null || _tokenizer is null) return null;

        // Tokenize to WordPiece ids and attention mask (includes [CLS]/[SEP], pads/truncates to 512).
        var enc = _tokenizer.Encode(text, maxLen: _maxSeqLen);
        if (enc.Truncated)
        {
            _logger?.LogDebug("Tokenizer truncated input to {Len} tokens (max={Max})", enc.Length, _maxSeqLen);
        }

        var ids = new long[enc.Length];
        var mask = new long[enc.Length];
        for (var i = 0; i < enc.Length; i++) { ids[i] = enc.Ids[i]; mask[i] = enc.AttentionMask[i]; }
        var shape = new[] { 1, enc.Length };

        // Package inputs as ONNX tensors (int64) in a [1, T] shape.
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<long>(mask, shape))
        };
        if (!string.IsNullOrEmpty(_tokenTypeName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<long>(new long[enc.Length], shape)));
        }

        try
        {
            // ONNX Runtime inference; wrap in Task.Run to keep caller async-friendly.
            using var results = await Task.Run(() => _session.Run(inputs), cancellationToken).ConfigureAwait(false);
            var outVal = results.First(v => v.Name == _outputName);

            float[] vec;
            switch (outVal.Value)
            {
                // [1,H] or [1,T,H]
                case DenseTensor<float> { Rank: 2 } t:
                    vec = [.. t];
                    break;
                case DenseTensor<float> { Rank: 3 } t:
                {
                    var dims = t.Dimensions; // [1,T,H]
                    int T = dims[1], H = dims[2];
                    vec = new float[H];
                    var valid = 0;
                    var span = t.Buffer.Span;
                    // Mean-pool the last_hidden_state using the attention mask.
                    for (var tok = 0; tok < T; tok++)
                    {
                        if (tok >= mask.Length || mask[tok] == 0) continue;
                        valid++;
                        var baseIdx = tok * H;
                        for (var h = 0; h < H; h++) vec[h] += span[baseIdx + h];
                    }
                    if (valid > 0) { for (var h = 0; h < H; h++) vec[h] /= valid; }

                    break;
                }
                case DenseTensor<float> t:
                    // Unexpected rank; flatten
                    vec = [.. t];
                    break;
                case IEnumerable<float> seq:
                    vec = [.. seq];
                    break;
                default:
                    return null;
            }

            // Resize or pad to expected dimension if needed, then L2-normalize.
            if (vec.Length != Dimension)
            {
                Array.Resize(ref vec, Dimension);
            }
            double ss = 0; for (var i = 0; i < vec.Length; i++) ss += vec[i] * vec[i];
            var norm = (float)Math.Sqrt(ss);
            if (norm > 0) for (var i = 0; i < vec.Length; i++) vec[i] /= norm;
            return vec;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding inference failed; returning null");
            return null;
        }
    }

    public void Dispose() => _session?.Dispose();
}

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
                remaining--;
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

internal readonly record struct EncodingResult(int[] Ids, int[] AttentionMask, bool Truncated = false)
{
    public int Length => Ids.Length;
}
