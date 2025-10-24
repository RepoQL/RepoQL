using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
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
            try
            {
                so.AppendExecutionProvider_CUDA();
            }
            catch
            {
                try
                {
                    so.AppendExecutionProvider_DML();
                }
                catch
                {
                     /* CPU fallback */
                }
            }
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
            _logger?.LogWarning(ex, "Embedding inference failed; returning null");
            return null;
        }
    }

    public void Dispose() => _session?.Dispose();
}