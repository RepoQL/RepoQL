using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Buffers;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Embeddings;

/// <summary>
/// Local ONNX embedding provider for BGE small v1.5.
/// Fast path: CLS pooling + L2 norm. CPU by default, tries CUDA/DML.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private InferenceSession? _session;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly string _inputIdsName = "";
    private readonly string _attnMaskName = "";
    private readonly string? _tokenTypeName;
    private readonly string _outputName = "";
    private readonly int _maxSeqLen;
    private volatile bool _disposed;

    private readonly WordPieceTokenizer? _tokenizer;

    public string Model { get; private set; } = "bge-small-en-v1.5";
    public int Dimension { get; private set; } = 384; // will be corrected from graph on first run
    public string Provider { get; private set; } = "CPU";
    public bool Enabled => !_disposed && _session is not null && _tokenizer is not null;

    public OnnxEmbeddingProvider(string modelPath, ILogger<OnnxEmbeddingProvider>? logger = null, int? maxTokens = null, int? intraOp = null, int? interOp = null)
    {
        _logger = logger ?? NullLogger<OnnxEmbeddingProvider>.Instance;
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        var modelFull = Path.GetFullPath(modelPath);
        if (!File.Exists(modelFull)) { _logger.LogWarning("ONNX model not found at {Path}", modelFull); return; }

        var modelDir = Path.GetDirectoryName(modelFull)!;
        var tokenizerJson = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { _logger.LogWarning("Tokenizer not found at {Path}", tokenizerJson); return; }

        try
        {
            _tokenizer = WordPieceTokenizer.LoadFromTokenizerJson(File.ReadAllText(tokenizerJson));
            _logger.LogInformation("Loaded tokenizer: lowercase={Lowercase} vocab={Vocab}", _tokenizer.Lowercase, _tokenizer.VocabSize);

            using var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // Default to quiet logs to avoid noisy provider-assignment warnings.
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                LogVerbosityLevel = 0,
            };

            if (intraOp is { } io and > 0) so.IntraOpNumThreads = io;
            if (interOp is { } eo and > 0) so.InterOpNumThreads = eo;

            var provider = ConfigureExecutionProvider(so);

            _session = new InferenceSession(modelFull, so);
            Provider = provider;
            _logger.LogInformation("ONNX session created for model {Model} using provider: {Provider}", Model, provider);

            Model = Path.GetFileNameWithoutExtension(modelFull);

            // Discover inputs by convention
            foreach (var kv in _session.InputMetadata)
            {
                var name = kv.Key;
                if (string.IsNullOrEmpty(_inputIdsName) && name.Contains("input_ids", StringComparison.OrdinalIgnoreCase)) _inputIdsName = name;
                else if (string.IsNullOrEmpty(_attnMaskName) && name.Contains("attention_mask", StringComparison.OrdinalIgnoreCase)) _attnMaskName = name;
                else if (string.IsNullOrEmpty(_tokenTypeName) && name.Contains("token_type_ids", StringComparison.OrdinalIgnoreCase)) _tokenTypeName = name;
            }
            if (string.IsNullOrEmpty(_inputIdsName) || string.IsNullOrEmpty(_attnMaskName))
            {
                _logger.LogWarning("Model inputs not found (input_ids/attention_mask). Disabling.");
                _session.Dispose();
                _session = null;
                return;
            }

            // Prefer last_hidden_state if present
            _outputName =
                _session.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("last_hidden_state", StringComparison.OrdinalIgnoreCase))
                ?? _session.OutputMetadata.Keys.First();
            _logger.LogDebug("IO: input_ids={Input} attention_mask={Mask} token_type_ids={TypeIds} output={Output}", _inputIdsName, _attnMaskName, _tokenTypeName ?? "<none>", _outputName);

            // Configure max sequence length. Default 256 for speed.
            _maxSeqLen = Math.Max(8, maxTokens ?? 256);
            _logger.LogInformation("Max tokens per sample: {Max}", _maxSeqLen);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize ONNX embedding provider");
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Encode one text to a normalized embedding. Returns null on error.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        var enc = _tokenizer!.Encode(text, _maxSeqLen);
        if (enc.Truncated) _logger.LogDebug("Input truncated to max_len={Max}", _maxSeqLen);

        var idsLen = enc.Length;
        var shape = new[] { 1, idsLen };

        // Autodetect input dtype (int32 vs int64) from graph
        var idsType = _session!.InputMetadata[_inputIdsName].ElementType;
        var useInt32 = idsType == typeof(int) || idsType == typeof(Int32);

        // Build inputs
        if (useInt32)
        {
            var ids32 = Array.ConvertAll(enc.Ids, x => (int)x);
            var mask32 = Array.ConvertAll(enc.AttentionMask, x => (int)x);
            var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<int>(ids32, shape));
            var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<int>(mask32, shape));
            if (!string.IsNullOrEmpty(_tokenTypeName))
            {
                var tt = new int[idsLen]; // zeros
                var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<int>(tt, shape));
                return await RunAndPostprocessAsync(new[] { n0, n1, n2 }, cancellationToken).ConfigureAwait(false);
            }
            return await RunAndPostprocessAsync(new[] { n0, n1 }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var ids64 = Array.ConvertAll(enc.Ids, x => (long)x);
            var mask64 = Array.ConvertAll(enc.AttentionMask, x => (long)x);
            var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(ids64, shape));
            var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<long>(mask64, shape));
            if (!string.IsNullOrEmpty(_tokenTypeName))
            {
                var tt = new long[idsLen]; // zeros
                var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<long>(tt, shape));
                return await RunAndPostprocessAsync(new[] { n0, n1, n2 }, cancellationToken).ConfigureAwait(false);
            }
            return await RunAndPostprocessAsync(new[] { n0, n1 }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Encode a batch. Faster than N single calls. Returns null for failed samples.
    /// </summary>
    public async Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        if (!Enabled || texts is null || texts.Count == 0) return [];
        var batch = texts.Count;
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();

        // Tokenize
        var tokenizeTimer = System.Diagnostics.Stopwatch.StartNew();
        var encs = new EncodingResult[batch];
        for (var i = 0; i < batch; i++)
        {
            encs[i] = _tokenizer!.Encode(texts[i], _maxSeqLen);
        }
        tokenizeTimer.Stop();

        // Autodetect input dtype
        var idsType = _session!.InputMetadata[_inputIdsName].ElementType;
        var useInt32 = idsType == typeof(int) || idsType == typeof(Int32);

        // Flattened [B,T]
        var tensorPrepTimer = System.Diagnostics.Stopwatch.StartNew();
        var shape = new[] { batch, _maxSeqLen };

        if (useInt32)
        {
            var ids = ArrayPool<int>.Shared.Rent(batch * _maxSeqLen);
            var mask = ArrayPool<int>.Shared.Rent(batch * _maxSeqLen);
            try
            {
                for (var b = 0; b < batch; b++)
                {
                    var e = encs[b];
                    var rowBase = b * _maxSeqLen;
                    Array.Copy(e.Ids, 0, ids, rowBase, _maxSeqLen);
                    Array.Copy(e.AttentionMask, 0, mask, rowBase, _maxSeqLen);
                }
                var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<int>(ids, shape));
                var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<int>(mask, shape));
                tensorPrepTimer.Stop();

                float[]?[] result;
                if (!string.IsNullOrEmpty(_tokenTypeName))
                {
                    var tt = new int[batch * _maxSeqLen]; // zeros
                    var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<int>(tt, shape));
                    result = await RunAndPostprocessBatchAsync(new[] { n0, n1, n2 }, batch, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await RunAndPostprocessBatchAsync(new[] { n0, n1 }, batch, cancellationToken).ConfigureAwait(false);
                }

                totalTimer.Stop();
                _logger.LogInformation("Batch embedding: size={BatchSize}, tokenize={TokenizeMs:F1}ms, tensorPrep={TensorPrepMs:F1}ms, total={TotalMs:F1}ms, perItem={PerItemMs:F1}ms",
                    batch, tokenizeTimer.Elapsed.TotalMilliseconds, tensorPrepTimer.Elapsed.TotalMilliseconds,
                    totalTimer.Elapsed.TotalMilliseconds, totalTimer.Elapsed.TotalMilliseconds / batch);
                return result;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(ids);
                ArrayPool<int>.Shared.Return(mask);
            }
        }
        else
        {
            var ids = ArrayPool<long>.Shared.Rent(batch * _maxSeqLen);
            var mask = ArrayPool<long>.Shared.Rent(batch * _maxSeqLen);
            try
            {
                for (var b = 0; b < batch; b++)
                {
                    var e = encs[b];
                    var rowBase = b * _maxSeqLen;
                    for (var i = 0; i < _maxSeqLen; i++) ids[rowBase + i] = e.Ids[i];
                    for (var i = 0; i < _maxSeqLen; i++) mask[rowBase + i] = e.AttentionMask[i];
                }
                var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(ids, shape));
                var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<long>(mask, shape));
                tensorPrepTimer.Stop();

                float[]?[] result;
                if (!string.IsNullOrEmpty(_tokenTypeName))
                {
                    var tt = new long[batch * _maxSeqLen]; // zeros
                    var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<long>(tt, shape));
                    result = await RunAndPostprocessBatchAsync(new[] { n0, n1, n2 }, batch, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await RunAndPostprocessBatchAsync(new[] { n0, n1 }, batch, cancellationToken).ConfigureAwait(false);
                }

                totalTimer.Stop();
                _logger.LogInformation("Batch embedding: size={BatchSize}, tokenize={TokenizeMs:F1}ms, tensorPrep={TensorPrepMs:F1}ms, total={TotalMs:F1}ms, perItem={PerItemMs:F1}ms",
                    batch, tokenizeTimer.Elapsed.TotalMilliseconds, tensorPrepTimer.Elapsed.TotalMilliseconds,
                    totalTimer.Elapsed.TotalMilliseconds, totalTimer.Elapsed.TotalMilliseconds / batch);
                return result;
            }
            finally
            {
                ArrayPool<long>.Shared.Return(ids);
                ArrayPool<long>.Shared.Return(mask);
            }
        }
    }

    private async Task<float[]?> RunAndPostprocessAsync(IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken ct)
    {
        try
        {
            using var results = await Task.Run(() => _session!.Run(inputs), ct).ConfigureAwait(false);
            var outVal = results.First(v => v.Name == _outputName);

            // Prefer DenseTensor for fast buffer access
            if (outVal.Value is DenseTensor<float> t)
            {
                switch (t.Rank)
                {
                    case 3: // [1,T,H] -> CLS at [0,0,:]
                    {
                        var dims = t.Dimensions;
                        var H = dims[2];
                        Dimension = H;
                        var vec = new float[H];
                        var span = t.Buffer.Span;
                        span.Slice(0, H).CopyTo(vec); // first H floats
                        L2NormalizeInPlace(vec);
                        return vec;
                    }
                    case 2: // [1,H]
                    {
                        var H = t.Dimensions[1];
                        Dimension = H;
                        var vec = new float[H];
                        var span = t.Buffer.Span;
                        span.Slice(0, H).CopyTo(vec);
                        L2NormalizeInPlace(vec);
                        return vec;
                    }
                    default:
                    {
                        var vec = t.ToArray();
                        if (vec.Length != Dimension) Dimension = vec.Length;
                        L2NormalizeInPlace(vec);
                        return vec;
                    }
                }
            }
            else if (outVal.Value is IEnumerable<float> seq)
            {
                var vec = seq is float[] a ? a : seq.ToArray();
                if (vec.Length != Dimension) Dimension = vec.Length;
                L2NormalizeInPlace(vec);
                return vec;
            }
            _logger.LogWarning("Unexpected output type {Type}", outVal.Value?.GetType().FullName ?? "<null>");
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding inference failed");
            return null;
        }
    }

    private async Task<float[]?[]> RunAndPostprocessBatchAsync(IReadOnlyCollection<NamedOnnxValue> inputs, int batch, CancellationToken ct)
    {
        try
        {
            var inferenceTimer = System.Diagnostics.Stopwatch.StartNew();
            using var results = await Task.Run(() => _session!.Run(inputs), ct).ConfigureAwait(false);
            inferenceTimer.Stop();

            var postprocessTimer = System.Diagnostics.Stopwatch.StartNew();
            var outVal = results.First(v => v.Name == _outputName);

            if (outVal.Value is DenseTensor<float> t)
            {
                if (t.Rank == 3) // [B,T,H] -> CLS [b,0,:]
                {
                    var dims = t.Dimensions;
                    int B = dims[0], T = dims[1], H = dims[2];
                    if (B != batch) _logger.LogWarning("Output batch {B} != input {I}", B, batch);
                    Dimension = H;

                    var span = t.Buffer.Span;
                    var stride = T * H;
                    var arr = new float[B][];
                    for (var b = 0; b < B; b++)
                    {
                        var vec = new float[H];
                        span.Slice(b * stride, H).CopyTo(vec); // first token for sample b
                        L2NormalizeInPlace(vec);
                        arr[b] = vec;
                    }
                    postprocessTimer.Stop();
                    _logger.LogDebug("ONNX batch timing: inference={InferenceMs:F1}ms, postprocess={PostprocessMs:F1}ms",
                        inferenceTimer.Elapsed.TotalMilliseconds, postprocessTimer.Elapsed.TotalMilliseconds);
                    return arr!;
                }
                else if (t.Rank == 2) // [B,H]
                {
                    var dims = t.Dimensions;
                    int B = dims[0], H = dims[1];
                    if (B != batch) _logger.LogWarning("Output batch {B} != input {I}", B, batch);
                    Dimension = H;

                    var arr = new float[B][];
                    var span = t.Buffer.Span;
                    for (var b = 0; b < B; b++)
                    {
                        var vec = new float[H];
                        span.Slice(b * H, H).CopyTo(vec);
                        L2NormalizeInPlace(vec);
                        arr[b] = vec;
                    }
                    postprocessTimer.Stop();
                    _logger.LogDebug("ONNX batch timing: inference={InferenceMs:F1}ms, postprocess={PostprocessMs:F1}ms",
                        inferenceTimer.Elapsed.TotalMilliseconds, postprocessTimer.Elapsed.TotalMilliseconds);
                    return arr!;
                }
                else
                {
                    postprocessTimer.Stop();
                    _logger.LogWarning("Unexpected output rank {Rank}", t.Rank);
                    return Enumerable.Range(0, batch).Select(_ => (float[]?)null).ToArray();
                }
            }

            postprocessTimer.Stop();
            _logger.LogWarning("Unexpected output type {Type}", outVal.Value?.GetType().FullName ?? "<null>");
            return Enumerable.Range(0, batch).Select(_ => (float[]?)null).ToArray();
        }
        catch (OperationCanceledException) { return Enumerable.Range(0, batch).Select(_ => (float[]?)null).ToArray(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch embedding inference failed");
            return Enumerable.Range(0, batch).Select(_ => (float[]?)null).ToArray();
        }
    }

    private string ConfigureExecutionProvider(SessionOptions so)
    {
        var envOverrideRaw = Environment.GetEnvironmentVariable("REPOQL_ORT_PROVIDER");
        var envOverride = envOverrideRaw?.Trim();
        if (!string.IsNullOrEmpty(envOverride))
        {
            var normalized = envOverride.ToUpperInvariant();

            // Providers that do not require explicit registration (CPU, defaults, etc.) should still
            // short-circuit GPU probing when requested.
            if (normalized is "CPU" or "CPUEXECUTIONPROVIDER" or "CPU_ONLY")
            {
                _logger.LogInformation("Using CPU execution provider (REPOQL_ORT_PROVIDER={Provider})", envOverride);
                return "CPU";
            }

            _logger.LogInformation("Attempting to use {Provider} execution provider (REPOQL_ORT_PROVIDER={Provider})", normalized, envOverride);
            if (TryAppendProvider(so, normalized, out var error))
            {
                _logger.LogInformation("Successfully configured {Provider} execution provider", normalized);
                return normalized;
            }

            _logger.LogWarning("Failed to configure {Provider} execution provider: {Error}. Honoring override by staying on CPU.", envOverride, error);
            // Unknown/unsupported override: do not probe GPU; fall back to CPU per explicit request.
            return "CPU";
        }

        _logger.LogInformation("Probing for available GPU execution providers...");

        if (OperatingSystem.IsMacOS())
        {
            _logger.LogInformation("Attempting CoreML execution provider (macOS)");
            if (TryAppendProvider(so, "COREML", out var error))
            {
                _logger.LogInformation("Successfully configured CoreML execution provider");
                return "COREML";
            }
            _logger.LogInformation("CoreML execution provider not available: {Error}", error);
        }

        if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Attempting DirectML execution provider (Windows)");
            if (TryAppendProvider(so, "DML", out var dmlError))
            {
                _logger.LogInformation("Successfully configured DirectML execution provider");
                return "DML";
            }
            _logger.LogInformation("DirectML execution provider not available: {Error}", dmlError);

            _logger.LogInformation("Attempting CUDA execution provider (Windows)");
            if (TryAppendProvider(so, "CUDA", out var error))
            {
                _logger.LogInformation("Successfully configured CUDA execution provider");
                return "CUDA";
            }
            _logger.LogInformation("CUDA execution provider not available: {Error}", error);
        }
        else
        {
            _logger.LogInformation("Attempting CUDA execution provider (Linux/Unix)");
            if (TryAppendProvider(so, "CUDA", out var error))
            {
                _logger.LogInformation("Successfully configured CUDA execution provider");
                return "CUDA";
            }
            _logger.LogInformation("CUDA execution provider not available: {Error}", error);
        }

        _logger.LogInformation("No GPU execution providers available, using CPU");
        return "CPU";
    }

    private bool TryAppendProvider(SessionOptions so, string provider, out string? error)
    {
        try
        {
            switch (provider)
            {
                case "CUDA":
                    so.AppendExecutionProvider_CUDA();
                    error = null;
                    return true;
                case "DML":
                    so.AppendExecutionProvider_DML();
                    error = null;
                    return true;
                case "COREML":
                    so.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_NONE);
                    error = null;
                    return true;
                default:
                    error = $"Unknown provider: {provider}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void L2NormalizeInPlace(float[] v)
    {
        double ss = 0;
        for (var i = 0; i < v.Length; i++) ss += (double)v[i] * v[i];
        var norm = (float)Math.Sqrt(ss);
        if (norm == 0f) return;
        var inv = 1f / norm;
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose()
    {
        _disposed = true;
        _session?.Dispose();
        _session = null;
    }
}
