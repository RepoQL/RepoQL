using System.Diagnostics;
using Humanizer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Buffers;
using System.Numerics;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Embeddings;

/// <summary>
/// Local ONNX embedding provider for E5-small-v2.
/// Fast path: CLS pooling + L2 norm. CPU by default on Windows (GPU is slower).
/// Linux/Unix probes for CUDA.
/// </summary>
/// <remarks>
/// <para><strong>E5 Model Prefixes:</strong></para>
/// <para>E5 models require asymmetric prefixes for optimal performance:</para>
/// <list type="bullet">
///   <item>Queries (search text): "query: " prefix</item>
///   <item>Passages (documents): "passage: " prefix</item>
/// </list>
/// <para>Use <see cref="EmbedQueryAsync"/> for search queries and <see cref="EmbedPassageAsync"/> for documents.</para>
/// <para><strong>Configuration:</strong></para>
/// <list type="bullet">
///   <item><c>ort.provider</c> - Execution provider override (CPU, CUDA, DML, COREML). macOS users can set COREML for ANE/GPU acceleration.</item>
///   <item><c>ort.intra_threads</c> - Intra-op parallelism threads (default: auto-detect based on hardware).</item>
///   <item><c>ort.inter_threads</c> - Inter-op parallelism threads (default: 1, ignored in sequential mode).</item>
/// </list>
/// </remarks>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.Embeddings.Onnx");

    // E5 model prefixes for asymmetric embedding
    private const string E5QueryPrefix = "query: ";
    private const string E5PassagePrefix = "passage: ";
    private const string BoostCacheKey = "OnnxBoostSession";
    private const int BoostExpirySeconds = 15;
    private const int DefaultBoostIntraOpThreads = 4;

    private InferenceSession? _session;
    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly IMemoryCache? _cache;
    private Timer? _boostEvictionTimer;
    private readonly string _modelPath;
    private readonly RepoQlConfig.OrtSettings _ortSettings;
    private readonly int? _intraOp;
    private readonly int? _interOp;
    private readonly string _inputIdsName = "";
    private readonly string _attnMaskName = "";
    private readonly string? _tokenTypeName;
    private readonly string _outputName = "";
    private readonly int _maxSeqLen;
    private volatile bool _disposed;

    private readonly WordPieceTokenizer? _tokenizer;

    /// <summary>
    /// Query prefix for asymmetric embedding (E5 models use "query: ").
    /// </summary>
    private readonly string _queryPrefix;

    /// <summary>
    /// Passage prefix for asymmetric embedding (E5 models use "passage: ").
    /// </summary>
    private readonly string _passagePrefix;

    public string Model { get; private set; } = "e5-small-v2";
    public int Dimension { get; private set; } = 384; // will be corrected from graph on first run
    public string Provider { get; private set; } = "CPU";
    public bool Enabled => !_disposed && _session is not null && _tokenizer is not null;

    public OnnxEmbeddingProvider(
        string modelPath,
        ILogger<OnnxEmbeddingProvider>? logger = null,
        int? maxTokens = null,
        int? intraOp = null,
        int? interOp = null,
        IMemoryCache? cache = null,
        RepoQlConfig.OrtSettings? ortSettings = null)
    {
        _logger = logger ?? NullLogger<OnnxEmbeddingProvider>.Instance;
        _cache = cache;
        _ortSettings = ortSettings ?? new RepoQlConfig.OrtSettings();
        _intraOp = intraOp;
        _interOp = interOp;
        // Always use E5 prefixes for asymmetric embedding
        _queryPrefix = E5QueryPrefix;
        _passagePrefix = E5PassagePrefix;

        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? "" : Path.GetFullPath(modelPath);

        if (string.IsNullOrWhiteSpace(_modelPath)) return;
        if (!File.Exists(_modelPath)) { _logger.LogWarning("ONNX model not found at {Path}", _modelPath); return; }

        var modelDir = Path.GetDirectoryName(_modelPath)!;
        var tokenizerJson = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { _logger.LogWarning("Tokenizer not found at {Path}", tokenizerJson); return; }

        try
        {
            _tokenizer = WordPieceTokenizer.LoadFromTokenizerJson(File.ReadAllText(tokenizerJson));
            _logger.LogInformation("Loaded tokenizer: lowercase={Lowercase} vocab={Vocab}", _tokenizer.Lowercase, _tokenizer.VocabSize);

            using var so = BuildSessionOptions(boost: false);
            _session = new InferenceSession(_modelPath, so);
            _logger.LogInformation("ONNX session created for model {Model} using provider: {Provider}", Model, Provider);

            Model = Path.GetFileNameWithoutExtension(_modelPath);

            _logger.LogInformation("Using E5 asymmetric prefixes: query='{QueryPrefix}', passage='{PassagePrefix}'",
                _queryPrefix, _passagePrefix);

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

    /// <summary>Core embedding implementation. Encodes text to a normalized embedding. Returns null on error.</summary>
    private async Task<float[]?> EmbedCoreAsync(string text, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("onnx.embed", ActivityKind.Internal);
        activity?.SetTag("embed.provider", "onnx");
        activity?.SetTag("embed.model", Model);
        activity?.SetTag("embed.count", 1);

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
    /// Embed text as a query (search text). Prepends "query: " prefix.
    /// </summary>
    public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
        => EmbedCoreAsync(_queryPrefix + text, cancellationToken);

    /// <summary>
    /// Embed text as a passage (document/content). Prepends "passage: " prefix.
    /// </summary>
    public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
        => EmbedCoreAsync(_passagePrefix + text, cancellationToken);

    /// <summary>
    /// Embed a batch of texts as queries (search texts). Prepends "query: " prefix.
    /// </summary>
    public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(texts, default, _queryPrefix, useBoost: false, cancellationToken);

    /// <summary>
    /// Embed a batch of texts as passages (documents/content). Prepends "passage: " prefix.
    /// </summary>
    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(texts, default, _passagePrefix, useBoost: false, cancellationToken);

    /// <summary>
    /// Embed a batch of texts as passages with progress reporting. Prepends "passage: " prefix.
    /// </summary>
    // Progress is used by indexing callers; this is the signal for boost-session routing.
    public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
        => EmbedBatchCoreAsync(texts, progress, _passagePrefix, useBoost: true, cancellationToken);

    /// <summary>
    /// Core batch embedding implementation with prefix.
    /// </summary>
    private async Task<float[]?[]> EmbedBatchCoreAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, string prefix, bool useBoost = false, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("onnx.embed_batch", ActivityKind.Internal);
        activity?.SetTag("embed.provider", "onnx");
        activity?.SetTag("embed.model", Model);
        activity?.SetTag("embed.count", texts?.Count ?? 0);

        if (!Enabled || texts is null || texts.Count == 0) return [];
        var session = (useBoost ? GetOrCreateBoostSession() : null) ?? _session!;
        var batch = texts.Count;
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();

        // Tokenize sequentially - inference is 98% of time, parallel overhead not worth it
        var tokenizeTimer = System.Diagnostics.Stopwatch.StartNew();
        var encs = new EncodingResult[batch];
        for (var i = 0; i < batch; i++)
        {
            encs[i] = _tokenizer!.Encode(prefix + texts[i], _maxSeqLen);
        }
        tokenizeTimer.Stop();

        // Autodetect input dtype
        var idsType = session.InputMetadata[_inputIdsName].ElementType;
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
                // ArrayPool may return larger buffer - slice to exact size needed for DenseTensor
                var exactSize = batch * _maxSeqLen;
                var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<int>(ids.AsMemory(0, exactSize), shape));
                var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<int>(mask.AsMemory(0, exactSize), shape));
                tensorPrepTimer.Stop();

                float[]?[] result;
                if (!string.IsNullOrEmpty(_tokenTypeName))
                {
                    var tt = new int[batch * _maxSeqLen]; // zeros
                    var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<int>(tt, shape));
                    result = await RunAndPostprocessBatchAsync(session, new[] { n0, n1, n2 }, batch, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await RunAndPostprocessBatchAsync(session, new[] { n0, n1 }, batch, cancellationToken).ConfigureAwait(false);
                }

                totalTimer.Stop();
                LogBatchProgress(progress, batch, totalTimer.Elapsed);
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
                    // Use SIMD-accelerated widening copy (int32 → int64)
                    CopyWideningInt32ToInt64(e.Ids.AsSpan(0, _maxSeqLen), ids.AsSpan(rowBase, _maxSeqLen));
                    CopyWideningInt32ToInt64(e.AttentionMask.AsSpan(0, _maxSeqLen), mask.AsSpan(rowBase, _maxSeqLen));
                }
                // ArrayPool may return larger buffer - slice to exact size needed for DenseTensor
                var exactSize = batch * _maxSeqLen;
                var n0 = NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(ids.AsMemory(0, exactSize), shape));
                var n1 = NamedOnnxValue.CreateFromTensor(_attnMaskName, new DenseTensor<long>(mask.AsMemory(0, exactSize), shape));
                tensorPrepTimer.Stop();

                float[]?[] result;
                if (!string.IsNullOrEmpty(_tokenTypeName))
                {
                    var tt = new long[batch * _maxSeqLen]; // zeros
                    var n2 = NamedOnnxValue.CreateFromTensor(_tokenTypeName!, new DenseTensor<long>(tt, shape));
                    result = await RunAndPostprocessBatchAsync(session, new[] { n0, n1, n2 }, batch, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await RunAndPostprocessBatchAsync(session, new[] { n0, n1 }, batch, cancellationToken).ConfigureAwait(false);
                }

                totalTimer.Stop();
                LogBatchProgress(progress, batch, totalTimer.Elapsed);
                return result;
            }
            finally
            {
                ArrayPool<long>.Shared.Return(ids);
                ArrayPool<long>.Shared.Return(mask);
            }
        }
    }

    private void LogBatchProgress(BatchEmbeddingProgress progress, int batchSize, TimeSpan batchTime)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        var perItemMs = batchSize == 0 ? 0 : batchTime.TotalMilliseconds / batchSize;

        if (progress.TotalBatches > 0)
        {
            var eta = progress.EstimatedRemaining;
            var etaStr = eta.HasValue && eta.Value > TimeSpan.Zero
                ? $", ETA {eta.Value.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Second)}"
                : "";

            _logger.LogInformation(
                "Batch embedding: {Batch}/{Total} ({Percent}%) size={BatchSize}, batch={BatchTime}, perItem={PerItemMs:F1}ms{Eta}",
                progress.BatchNumber, progress.TotalBatches, progress.PercentComplete, batchSize,
                batchTime.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond),
                perItemMs, etaStr);
        }
        else
        {
            _logger.LogInformation(
                "Batch embedding: size={BatchSize}, batch={BatchTime}, perItem={PerItemMs:F1}ms",
                batchSize,
                batchTime.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond),
                perItemMs);
        }
    }

    private Task<float[]?> RunAndPostprocessAsync(IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            using var results = _session!.Run(inputs);
            {
                DisposableNamedOnnxValue? outVal = null;
                foreach (var v in results)
                {
                    if (v.Name == _outputName)
                    {
                        outVal = v;
                        break;
                    }
                }

                if (outVal is null)
                {
                    _logger.LogWarning("ONNX output {Name} not found", _outputName);
                    return Task.FromResult<float[]?>(null);
                }

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
                            return Task.FromResult<float[]?>(vec);
                        }
                        case 2: // [1,H]
                        {
                            var H = t.Dimensions[1];
                            Dimension = H;
                            var vec = new float[H];
                            var span = t.Buffer.Span;
                            span.Slice(0, H).CopyTo(vec);
                            L2NormalizeInPlace(vec);
                            return Task.FromResult<float[]?>(vec);
                        }
                        default:
                        {
                            var vec = t.ToArray();
                            if (vec.Length != Dimension) Dimension = vec.Length;
                            L2NormalizeInPlace(vec);
                            return Task.FromResult<float[]?>(vec);
                        }
                    }
                }
                else if (outVal.Value is IEnumerable<float> seq)
                {
                    var vec = seq is float[] a ? a : seq.ToArray();
                    if (vec.Length != Dimension) Dimension = vec.Length;
                    L2NormalizeInPlace(vec);
                    return Task.FromResult<float[]?>(vec);
                }

                _logger.LogWarning("Unexpected output type {Type}", outVal.Value?.GetType().FullName ?? "<null>");
                return Task.FromResult<float[]?>(null);
            }
        }
        catch (OperationCanceledException) { return Task.FromResult<float[]?>(null); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding inference failed");
            return Task.FromResult<float[]?>(null);
        }
    }

    private Task<float[]?[]> RunAndPostprocessBatchAsync(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, int batch, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var inferenceTimer = System.Diagnostics.Stopwatch.StartNew();
            using var results = session.Run(inputs);
            inferenceTimer.Stop();

            var postprocessTimer = System.Diagnostics.Stopwatch.StartNew();
            {
                DisposableNamedOnnxValue? outVal = null;
                foreach (var v in results)
                {
                    if (v.Name == _outputName)
                    {
                        outVal = v;
                        break;
                    }
                }

                if (outVal is null)
                {
                    postprocessTimer.Stop();
                    _logger.LogWarning("ONNX output {Name} not found", _outputName);
                    return Task.FromResult(new float[]?[batch]);
                }

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
                        return Task.FromResult<float[]?[]>(arr!);
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
                        return Task.FromResult<float[]?[]>(arr!);
                    }
                    else
                    {
                        postprocessTimer.Stop();
                        _logger.LogWarning("Unexpected output rank {Rank}", t.Rank);
                        return Task.FromResult(new float[]?[batch]);
                    }
                }

                postprocessTimer.Stop();
                _logger.LogWarning("Unexpected output type {Type}", outVal.Value?.GetType().FullName ?? "<null>");
                return Task.FromResult(new float[]?[batch]);
            }
        }
        catch (OperationCanceledException) { return Task.FromResult(new float[]?[batch]); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch embedding inference failed");
            return Task.FromResult(new float[]?[batch]);
        }
    }

    private SessionOptions BuildSessionOptions(bool boost)
    {
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, // Optimal for single-model inference
            // Default to quiet logs to avoid noisy provider-assignment warnings.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            LogVerbosityLevel = 0,
        };

        if (boost)
        {
            ConfigureBoostMemoryOptions(so);
        }
        else
        {
            ConfigureBaseMemoryOptions(so);
            _logger.LogInformation("ONNX memory arena: base session (no arena)");
        }

        // Thread config: explicit constructor args, then resolved config, then ONNX defaults.
        var intraOpThreads = _intraOp ?? (_ortSettings.IntraThreads is >= 0 ? _ortSettings.IntraThreads : null);
        var interOpThreads = _interOp ?? (_ortSettings.InterThreads is >= 0 ? _ortSettings.InterThreads : null);

        // Keep the base session on ONNX auto-threading by default, but cap the arena-backed
        // boost session to a modest worker count. Passage batches can otherwise multiply native
        // thread pools under concurrent query/indexing load and look like thread starvation.
        var resolvedIntraOpThreads = boost
            ? (intraOpThreads is > 0 ? intraOpThreads.Value : DefaultBoostIntraOpThreads)
            : (intraOpThreads ?? 0);

        // IntraOp: 0 = ONNX auto-detect based on hardware. InterOp: 1 is sensible for sequential mode.
        so.IntraOpNumThreads = resolvedIntraOpThreads;
        so.InterOpNumThreads = interOpThreads ?? 1;
        _logger.LogInformation("ONNX thread config: Session={SessionKind}, IntraOp={IntraOp}, InterOp={InterOp}",
            boost ? "boost" : "base", so.IntraOpNumThreads, so.InterOpNumThreads);

        // Int8 quantized model optimizations
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.use_onnx_model_bytes_directly", "1");
        so.AddSessionConfigEntry("session.set_denormal_as_zero", "1");

        var provider = ConfigureExecutionProvider(so);
        if (!boost)
            Provider = provider;

        return so;
    }

    private static void ConfigureBaseMemoryOptions(SessionOptions so)
    {
        so.EnableCpuMemArena = false;
        so.EnableMemoryPattern = false;
    }

    private static void ConfigureBoostMemoryOptions(SessionOptions so)
    {
        so.EnableCpuMemArena = true;
        so.EnableMemoryPattern = true;
        so.AddSessionConfigEntry("arena_extend_strategy", "kSameAsRequested");
    }

    private InferenceSession CreateBoostSession()
    {
        using var so = BuildSessionOptions(boost: true);
        return new InferenceSession(_modelPath, so);
    }

    private InferenceSession? GetOrCreateBoostSession()
    {
        if (_cache is null) return null;
        var session = _cache.GetOrCreate(BoostCacheKey, entry =>
        {
            entry.SetSlidingExpiration(TimeSpan.FromSeconds(BoostExpirySeconds));
            entry.SetSize(64);
            entry.RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                if (value is InferenceSession s)
                {
                    _logger.LogInformation("Boost session evicted ({Reason}), releasing arena memory", reason);
                    s.Dispose();
                }
            });
            _logger.LogInformation("Boost session created (arena enabled, kSameAsRequested)");
            return CreateBoostSession();
        });

        // MemoryCache eviction is lazy — scans are throttled by ExpirationScanFrequency (default 1min).
        // Use Remove() directly to guarantee eviction regardless of scan frequency.
        _boostEvictionTimer?.Dispose();
        _boostEvictionTimer = new Timer(
            _ => { try { _cache.Remove(BoostCacheKey); } catch { /* disposed */ } },
            null,
            TimeSpan.FromSeconds(BoostExpirySeconds + 5),
            Timeout.InfiniteTimeSpan);

        return session;
    }

    private string ConfigureExecutionProvider(SessionOptions so)
    {
        var configuredOverride = _ortSettings.Provider?.Trim();
        if (!string.IsNullOrEmpty(configuredOverride))
        {
            var normalized = configuredOverride.ToUpperInvariant();

            // Providers that do not require explicit registration (CPU, defaults, etc.) should still
            // short-circuit GPU probing when requested.
            if (normalized is "CPU" or "CPUEXECUTIONPROVIDER" or "CPU_ONLY")
            {
                _logger.LogInformation("Using CPU execution provider (ort.provider={Provider})", configuredOverride);
                return "CPU";
            }

            _logger.LogInformation("Attempting to use {Provider} execution provider (ort.provider={Provider})", normalized, configuredOverride);
            if (TryAppendProvider(so, normalized, out var error))
            {
                _logger.LogInformation("Successfully configured {Provider} execution provider", normalized);
                return normalized;
            }

            _logger.LogWarning("Failed to configure {Provider} execution provider: {Error}. Honoring override by staying on CPU.", configuredOverride, error);
            // Unknown/unsupported override: do not probe GPU; fall back to CPU per explicit request.
            return "CPU";
        }

        // Windows: Default to CPU. GPU providers (DirectML, CUDA) are slower than CPU for this workload,
        // and CoreML causes memory leaks. Users can override via ort.provider if needed.
        if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Using CPU execution provider (default for Windows - GPU is slower for embeddings)");
            return "CPU";
        }

        // Non-Windows: probe for CUDA
        _logger.LogInformation("Probing for available GPU execution providers...");
        _logger.LogInformation("Attempting CUDA execution provider (Linux/Unix)");
        if (TryAppendProvider(so, "CUDA", out var cudaError))
        {
            _logger.LogInformation("Successfully configured CUDA execution provider");
            return "CUDA";
        }
        _logger.LogInformation("CUDA execution provider not available: {Error}", cudaError);

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

    /// <summary>
    /// Copies int32 values to int64 destination with widening.
    /// Uses SIMD acceleration when available for ~4x speedup over scalar loops.
    /// </summary>
    private static void CopyWideningInt32ToInt64(ReadOnlySpan<int> source, Span<long> destination)
    {
        if (source.Length > destination.Length)
            throw new ArgumentException("Destination too small", nameof(destination));

        var i = 0;

        // SIMD path: process Vector<int>.Count elements at a time
        // Vector.Widen splits one Vector<int> into two Vector<long> (lower and upper halves)
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<int>.Count)
        {
            var vectorCount = source.Length - (source.Length % Vector<int>.Count);
            for (; i < vectorCount; i += Vector<int>.Count)
            {
                var srcVec = new Vector<int>(source.Slice(i, Vector<int>.Count));
                Vector.Widen(srcVec, out var low, out var high);
                low.CopyTo(destination.Slice(i, Vector<long>.Count));
                high.CopyTo(destination.Slice(i + Vector<long>.Count, Vector<long>.Count));
            }
        }

        // Scalar fallback for remainder (or when SIMD unavailable)
        for (; i < source.Length; i++)
            destination[i] = source[i];
    }

    public void Dispose()
    {
        _disposed = true;
        _boostEvictionTimer?.Dispose();
        _boostEvictionTimer = null;
        _cache?.Remove(BoostCacheKey);
        _session?.Dispose();
        _session = null;
    }
}
