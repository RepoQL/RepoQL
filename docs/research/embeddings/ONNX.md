# ONNX Runtime for Embedding Models

This document provides comprehensive guidance on using ONNX Runtime to run embedding models, with focus on C#/.NET integration for applications like RepoQL.

## Table of Contents

1. [ONNX Overview](#onnx-overview)
2. [Running Embedding Models](#running-embedding-models)
3. [C#/.NET Integration](#cnet-integration)
4. [Performance Optimization](#performance-optimization)
5. [Practical Patterns](#practical-patterns)
6. [Model Sources](#model-sources)

---

## ONNX Overview

### What is ONNX?

**ONNX (Open Neural Network Exchange)** is an open standard that defines a common set of operators and a common file format to represent deep learning models. It serves as an intermediate representation (IR) for neural networks, enabling interoperability between different frameworks.

**ONNX Runtime** is Microsoft's cross-platform, high-performance inference engine for ONNX models. It provides:

- Hardware-agnostic model deployment
- Automatic graph optimizations
- Support for multiple execution providers (CPU, GPU, NPU)
- Bindings for C, C++, C#, Python, Java, JavaScript, and more

### Why Use ONNX for Embeddings?

| Benefit | Description |
|---------|-------------|
| **Portability** | Single model format works across platforms and languages |
| **Performance** | 2x-10x faster inference than PyTorch on CPU; up to 18x with quantization |
| **No Python Dependency** | Run models natively in C#/Java without Python runtime |
| **Hardware Acceleration** | Same model runs on CPU, CUDA, DirectML, CoreML, TensorRT |
| **Smaller Footprint** | Graph optimizations reduce model size and memory usage |
| **Production Ready** | Battle-tested in Azure, Windows ML, and countless production systems |

### ONNX vs Native Framework Inference

| Aspect | ONNX Runtime | PyTorch/TensorFlow |
|--------|--------------|-------------------|
| **CPU Inference** | 2x-10x faster (optimized kernels) | Baseline |
| **Startup Time** | Fast (pre-optimized graph) | Slower (JIT compilation) |
| **Memory Usage** | Lower (optimized allocation) | Higher |
| **Language Support** | Native C#, Java, C++ | Python-first |
| **Model Size** | Smaller (dead code elimination) | Original size |
| **Quantization** | Built-in INT8/INT4 support | Requires additional tooling |

**Benchmark Reference**: ONNX Runtime serving handles approximately 7x more requests per second than PyTorch serving, and up to 9x with Rust bindings.

---

## Running Embedding Models

### Model Architecture Overview

Embedding models (like BERT, BGE, all-MiniLM) follow this pipeline:

```
Text Input → Tokenization → Model Inference → Pooling → Normalization → Embedding Vector
```

When converting to ONNX, you must handle each step:

1. **Tokenization**: Convert text to token IDs (separate ONNX or native implementation)
2. **Model Inference**: Run the transformer through ONNX Runtime
3. **Pooling**: Aggregate token embeddings (CLS token, mean pooling, etc.)
4. **Normalization**: L2 normalize the output (for cosine similarity)

### Exporting Models to ONNX

#### Using Hugging Face Optimum (Recommended)

The `optimum` library provides the easiest path to ONNX export:

```bash
# Install optimum with ONNX support
pip install "optimum-onnx[onnxruntime]"

# Export a model via CLI
optimum-cli export onnx --model sentence-transformers/all-MiniLM-L6-v2 ./minilm_onnx/

# Export with optimization level O2 (extended optimizations)
optimum-cli export onnx --model BAAI/bge-small-en-v1.5 --optimize O2 ./bge_small_onnx/

# Export with FP16 quantization (GPU only)
optimum-cli export onnx --model BAAI/bge-base-en-v1.5 --optimize O4 --device cuda ./bge_base_fp16/
```

**Optimization Levels:**
- `O1`: Basic general optimizations
- `O2`: Basic + extended optimizations, transformer-specific fusions
- `O3`: O2 + GELU approximation
- `O4`: O3 + mixed precision (FP16, requires CUDA)

#### Programmatic Export

```python
from optimum.onnxruntime import ORTModelForFeatureExtraction
from transformers import AutoTokenizer

model_id = "sentence-transformers/all-MiniLM-L6-v2"

# Export to ONNX
model = ORTModelForFeatureExtraction.from_pretrained(model_id, export=True)
tokenizer = AutoTokenizer.from_pretrained(model_id)

# Save locally
model.save_pretrained("./minilm_onnx")
tokenizer.save_pretrained("./minilm_onnx")
```

### Tokenization Handling

Tokenization is the critical preprocessing step that converts text to model inputs. Three approaches:

#### Option 1: ONNX Runtime Extensions (Recommended for C#)

Convert the tokenizer itself to ONNX format using ONNX Runtime Extensions:

```python
# Export tokenizer to ONNX (requires onnxruntime-extensions)
from onnxruntime_extensions import gen_processing_models

# This creates a tokenizer.onnx that can be loaded in C#
gen_processing_models(
    tokenizer,
    pre_kwargs={"add_special_tokens": True},
    post_kwargs={},
    output_model_path="tokenizer.onnx"
)
```

In C#, load both models:
```csharp
var tokenizerOptions = new SessionOptions();
tokenizerOptions.RegisterOrtExtensions();
using var tokenizerSession = new InferenceSession("tokenizer.onnx", tokenizerOptions);
using var modelSession = new InferenceSession("model.onnx");
```

#### Option 2: BERTTokenizers NuGet Package

For BERT-family models, use the `BERTTokenizers` package:

```bash
dotnet add package BERTTokenizers --version 1.1.0
```

```csharp
using BERTTokenizers;

var tokenizer = new BertBaseUncasedTokenizer();
var tokens = tokenizer.Tokenize("Your input text here");
var encoded = tokenizer.Encode(128, "Your input text here"); // max 128 tokens
```

#### Option 3: Custom WordPiece Implementation

For maximum control, implement tokenization directly (as RepoQL does in `OnnxEmbeddingProvider`).

### Input/Output Tensor Formats

**Standard BERT-family Inputs:**

| Input Name | Shape | Type | Description |
|------------|-------|------|-------------|
| `input_ids` | `[batch, seq_len]` | INT64 | Token IDs from tokenizer |
| `attention_mask` | `[batch, seq_len]` | INT64 | 1 for real tokens, 0 for padding |
| `token_type_ids` | `[batch, seq_len]` | INT64 | Segment IDs (optional for some models) |

**Standard Outputs:**

| Output Name | Shape | Type | Description |
|-------------|-------|------|-------------|
| `last_hidden_state` | `[batch, seq_len, hidden_dim]` | FLOAT32 | Per-token embeddings |
| `pooler_output` | `[batch, hidden_dim]` | FLOAT32 | CLS token embedding (if available) |

### Batch Inference

ONNX Runtime supports dynamic batch sizes. For efficient batch processing:

```csharp
// Prepare batch inputs
int batchSize = texts.Length;
int seqLen = 128;

var inputIds = new long[batchSize * seqLen];
var attentionMask = new long[batchSize * seqLen];

// Fill arrays from tokenizer output...

// Create tensors
using var inputIdsTensor = OrtValue.CreateTensorValueFromMemory(
    inputIds, new long[] { batchSize, seqLen });
using var attentionMaskTensor = OrtValue.CreateTensorValueFromMemory(
    attentionMask, new long[] { batchSize, seqLen });

// Run batch inference
var inputs = new Dictionary<string, OrtValue>
{
    { "input_ids", inputIdsTensor },
    { "attention_mask", attentionMaskTensor }
};

using var outputs = session.Run(runOptions, inputs, session.OutputNames);
```

---

## C#/.NET Integration

### NuGet Packages

| Package | Purpose | When to Use |
|---------|---------|-------------|
| `Microsoft.ML.OnnxRuntime` | CPU inference | Default choice |
| `Microsoft.ML.OnnxRuntime.Gpu` | NVIDIA CUDA acceleration | NVIDIA GPU with CUDA 12.x |
| `Microsoft.ML.OnnxRuntime.DirectML` | DirectX 12 acceleration | Windows GPU (AMD/Intel/NVIDIA) |
| `Microsoft.ML.OnnxRuntime.Managed` | Managed-only wrapper | When native dependencies are problematic |
| `Microsoft.ML.OnnxRuntimeGenAI` | Generative AI models | LLMs (Phi, Llama, etc.) |
| `System.Numerics.Tensors` | Tensor operations | Required for tensor APIs (v9.0.0+) |

**Installation:**
```bash
dotnet add package Microsoft.ML.OnnxRuntime --version 1.23.2
dotnet add package System.Numerics.Tensors --version 9.0.0
```

### Execution Provider Comparison

| Provider | Platform | Hardware | Notes |
|----------|----------|----------|-------|
| **CPU** | All | Any CPU | Default, always available |
| **CUDA** | Linux, Windows | NVIDIA GPU | Best for NVIDIA, requires CUDA 12.x |
| **TensorRT** | Linux, Windows | NVIDIA GPU | Highest NVIDIA performance, longer startup |
| **DirectML** | Windows 10+ | Any DirectX 12 GPU | Vendor-agnostic, good for AMD/Intel |
| **CoreML** | macOS, iOS | Apple Silicon/ANE | Best for Apple devices |
| **ROCm** | Linux | AMD GPU | AMD's CUDA alternative |
| **OpenVINO** | Linux, Windows | Intel CPU/GPU/VPU | Best for Intel hardware |

**Provider Selection (C#):**
```csharp
// CPU (default)
using var session = new InferenceSession("model.onnx");

// CUDA
using var cudaOptions = SessionOptions.MakeSessionOptionWithCudaProvider(gpuDeviceId: 0);
using var session = new InferenceSession("model.onnx", cudaOptions);

// DirectML
var dmlOptions = new SessionOptions();
dmlOptions.AppendExecutionProvider_DML(deviceId: 0);
using var session = new InferenceSession("model.onnx", dmlOptions);

// CoreML (macOS/iOS)
var coremlOptions = new SessionOptions();
coremlOptions.AppendExecutionProvider_CoreML();
using var session = new InferenceSession("model.onnx", coremlOptions);
```

### Session Management and Configuration

```csharp
public class OnnxInferenceService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SessionOptions _options;

    public OnnxInferenceService(string modelPath)
    {
        _options = new SessionOptions
        {
            // Graph optimization level
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

            // Thread configuration
            IntraOpNumThreads = Environment.ProcessorCount,
            InterOpNumThreads = 1,

            // Execution mode (sequential for most embedding models)
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,

            // Memory optimization
            EnableMemoryPattern = true,
            EnableCpuMemArena = true,
        };

        // Optional: Disable spinning to reduce CPU usage
        _options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        // Optional: Enable profiling for debugging
        // _options.EnableProfiling = true;
        // _options.ProfileOutputPathPrefix = "onnx_profile";

        _session = new InferenceSession(modelPath, _options);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _options?.Dispose();
    }
}
```

### Memory Management and Tensor Handling

**Critical Rules:**

1. **Always dispose OrtValue objects** - Memory leaks can be huge with tensors
2. **Use `CreateTensorValueFromMemory`** - Pins managed memory, avoids copies
3. **Spans are tied to OrtValue lifetime** - Don't use after disposal

```csharp
public float[] RunInference(long[] inputIds, long[] attentionMask, int seqLen)
{
    // Create tensors from existing arrays (no copy, memory is pinned)
    using var inputIdsTensor = OrtValue.CreateTensorValueFromMemory(
        inputIds, new long[] { 1, seqLen });
    using var attentionMaskTensor = OrtValue.CreateTensorValueFromMemory(
        attentionMask, new long[] { 1, seqLen });

    var inputs = new Dictionary<string, OrtValue>
    {
        { "input_ids", inputIdsTensor },
        { "attention_mask", attentionMaskTensor }
    };

    using var runOptions = new RunOptions();
    using var outputs = _session.Run(runOptions, inputs, _session.OutputNames);

    // Get output tensor
    var outputTensor = outputs[0];

    // Get data as span (valid only while outputTensor is alive)
    var outputSpan = outputTensor.GetTensorDataAsSpan<float>();

    // Copy to new array before disposal
    return outputSpan.ToArray();
}
```

**Reusing Buffers for Batch Processing:**

```csharp
public class BufferedInference
{
    private readonly float[] _outputBuffer;
    private readonly int _embeddingDim;

    public BufferedInference(int embeddingDim, int maxBatchSize)
    {
        _embeddingDim = embeddingDim;
        _outputBuffer = new float[maxBatchSize * embeddingDim];
    }

    public ReadOnlySpan<float> GetEmbedding(int batchIndex)
    {
        int start = batchIndex * _embeddingDim;
        return _outputBuffer.AsSpan(start, _embeddingDim);
    }
}
```

---

## Performance Optimization

### Graph Optimizations

ONNX Runtime performs automatic graph optimizations at three levels:

| Level | Optimizations | Use Case |
|-------|---------------|----------|
| **Basic** | Constant folding, redundant node elimination, node fusions | Always beneficial |
| **Extended** | GELU fusion, attention fusion, layer norm fusion | Transformer models |
| **Layout** | Memory layout optimizations for CPU | CPU-bound workloads |

```csharp
// Enable all optimizations (recommended)
options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

// Save optimized model to disk (faster subsequent loads)
options.OptimizedModelFilePath = "model_optimized.onnx";
```

### Quantization

Quantization reduces model size and improves inference speed by using lower precision:

| Type | Precision | Size Reduction | Speed Improvement | Accuracy Loss |
|------|-----------|----------------|-------------------|---------------|
| **FP32** | 32-bit float | Baseline | Baseline | None |
| **FP16** | 16-bit float | 50% | 1.5-2x (GPU) | Minimal |
| **INT8** | 8-bit integer | 75% | 2-4x | Small |
| **INT4** | 4-bit integer | 87.5% | 4x+ | Moderate |

**Dynamic Quantization (Easiest):**
```python
from onnxruntime.quantization import quantize_dynamic, QuantType

quantize_dynamic(
    model_input="model.onnx",
    model_output="model_int8.onnx",
    weight_type=QuantType.QInt8
)
```

**Static Quantization (Best Quality):**
```python
from onnxruntime.quantization import quantize_static, CalibrationDataReader

class EmbeddingCalibrationReader(CalibrationDataReader):
    def __init__(self, calibration_texts, tokenizer):
        self.data = [tokenizer(text) for text in calibration_texts]
        self.index = 0

    def get_next(self):
        if self.index >= len(self.data):
            return None
        result = self.data[self.index]
        self.index += 1
        return result

quantize_static(
    model_input="model.onnx",
    model_output="model_int8_static.onnx",
    calibration_data_reader=EmbeddingCalibrationReader(texts, tokenizer)
)
```

**Hardware Requirements for Quantized Models:**
- INT8 on CPU: x86-64 with VNNI (Intel Ice Lake+, AMD Zen4+)
- INT8 on GPU: NVIDIA Tensor Cores (T4, A100, RTX 20+)
- FP16 on GPU: Most modern GPUs

### Thread Configuration

| Setting | Purpose | Recommendation |
|---------|---------|----------------|
| `IntraOpNumThreads` | Parallelism within operators | Physical CPU cores |
| `InterOpNumThreads` | Parallelism between operators | 1 for sequential models |
| `ExecutionMode` | Sequential vs parallel execution | Sequential for most embeddings |
| `allow_spinning` | Thread spin-waiting | Disable to reduce CPU usage |

```csharp
// Balanced configuration for embedding workloads
var options = new SessionOptions
{
    IntraOpNumThreads = Environment.ProcessorCount,
    InterOpNumThreads = 1,
    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
};

// Reduce CPU usage (important for background services)
options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
```

**NUMA Considerations:**
Setting thread affinity to a single NUMA node provides ~20% performance improvement compared to distributing across nodes.

### Intra-op vs Inter-op Parallelism

- **Intra-op**: Parallelizes computation *within* each operator (matrix multiplication, etc.)
- **Inter-op**: Parallelizes execution *across* operators in the graph

For embedding models (mostly sequential):
- High `IntraOpNumThreads` (utilize all cores per inference)
- Low `InterOpNumThreads` (1, since operations are sequential)

For models with parallel branches:
- Moderate `IntraOpNumThreads`
- Higher `InterOpNumThreads` to parallelize independent branches

---

## Practical Patterns

### Session Pooling

Session creation is expensive (~100ms+). For high-throughput scenarios, pool sessions:

```csharp
public class OnnxSessionPool : IDisposable
{
    private readonly ObjectPool<InferenceSession> _pool;
    private readonly SessionOptions _options;
    private readonly string _modelPath;

    public OnnxSessionPool(string modelPath, int maxSessions = 4)
    {
        _modelPath = modelPath;
        _options = CreateOptions();

        var policy = new DefaultPooledObjectPolicy<InferenceSession>();
        _pool = new DefaultObjectPool<InferenceSession>(
            new SessionPoolPolicy(_modelPath, _options), maxSessions);
    }

    public InferenceSession Rent() => _pool.Get();
    public void Return(InferenceSession session) => _pool.Return(session);

    private class SessionPoolPolicy : IPooledObjectPolicy<InferenceSession>
    {
        private readonly string _modelPath;
        private readonly SessionOptions _options;

        public SessionPoolPolicy(string modelPath, SessionOptions options)
        {
            _modelPath = modelPath;
            _options = options;
        }

        public InferenceSession Create() => new(_modelPath, _options);
        public bool Return(InferenceSession obj) => true; // Sessions are reusable
    }
}
```

### Async Inference Pattern

ONNX Runtime's `Run` is synchronous, but you can wrap it for async workloads:

```csharp
public class AsyncEmbeddingService
{
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _semaphore;

    public AsyncEmbeddingService(string modelPath, int maxConcurrency = 2)
    {
        _session = new InferenceSession(modelPath);
        _semaphore = new SemaphoreSlim(maxConcurrency);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // Offload synchronous inference to thread pool
            return await Task.Run(() => RunInference(text), ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private float[] RunInference(string text)
    {
        // Tokenize and run inference...
    }
}
```

### Warm-up Strategies

Cold start can add significant latency. Warm up sessions at startup:

```csharp
public async Task WarmUpAsync(InferenceSession session)
{
    // Run a few dummy inferences to warm up JIT, memory allocation, etc.
    var warmupTexts = new[] { "warmup", "initialization text", "model ready" };

    foreach (var text in warmupTexts)
    {
        await Task.Run(() => RunInference(session, text));
    }

    // Force GC after warmup to clean up temporary allocations
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
}
```

### Error Handling

```csharp
public float[]? SafeEmbed(string text)
{
    try
    {
        return RunInference(text);
    }
    catch (OnnxRuntimeException ex) when (ex.Message.Contains("out of memory"))
    {
        // Handle OOM - reduce batch size or fall back
        _logger.LogWarning("ONNX OOM, reducing batch size");
        return null;
    }
    catch (OnnxRuntimeException ex)
    {
        _logger.LogError(ex, "ONNX inference failed for text: {TextLength} chars", text.Length);
        throw;
    }
}
```

### Model Versioning

Track model versions to ensure embedding compatibility:

```csharp
public record EmbeddingModelInfo(
    string ModelName,
    string Version,
    int Dimension,
    string Hash
);

public class VersionedModelLoader
{
    public (InferenceSession Session, EmbeddingModelInfo Info) LoadModel(string modelDir)
    {
        var configPath = Path.Combine(modelDir, "model_config.json");
        var info = JsonSerializer.Deserialize<EmbeddingModelInfo>(File.ReadAllText(configPath));

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var session = new InferenceSession(modelPath);

        return (session, info);
    }
}
```

---

## Model Sources

### Pre-Converted ONNX Models on Hugging Face

| Model | Repository | Dimensions | Size | Use Case |
|-------|------------|------------|------|----------|
| all-MiniLM-L6-v2 | `onnx-models/all-MiniLM-L6-v2-onnx` | 384 | ~90MB | General purpose, fast |
| BGE-small-en-v1.5 | `Teradata/bge-small-en-v1.5` | 384 | ~127MB (FP32), ~32MB (INT8) | Retrieval-focused |
| BGE-base-en-v1.5 | `Teradata/bge-base-en-v1.5` | 768 | ~416MB (FP32), ~105MB (INT8) | Higher quality retrieval |
| BGE-M3 | `Teradata/bge-m3` | 1024 | ~543MB (INT8) | Multilingual, multi-vector |

### Downloading from Hugging Face

```python
from huggingface_hub import hf_hub_download

# Download model
hf_hub_download(
    repo_id="Teradata/bge-small-en-v1.5",
    filename="onnx/model.onnx",
    local_dir="./bge_small"
)

# Download tokenizer
hf_hub_download(
    repo_id="Teradata/bge-small-en-v1.5",
    filename="tokenizer.json",
    local_dir="./bge_small"
)
```

### Using Optimum for Custom Export

For models not available in ONNX format:

```bash
# Export any Sentence Transformers model
optimum-cli export onnx \
    --model sentence-transformers/paraphrase-MiniLM-L6-v2 \
    --task feature-extraction \
    --optimize O2 \
    ./paraphrase_minilm_onnx/
```

### Model Recommendations by Use Case

| Use Case | Recommended Model | Rationale |
|----------|-------------------|-----------|
| **Fast local inference** | BGE-small-en-v1.5 (INT8) | 32MB, 384 dims, excellent quality |
| **Highest quality** | BGE-base-en-v1.5 | 768 dims, MTEB benchmark leader |
| **Multilingual** | BGE-M3 | Supports 100+ languages |
| **Legacy compatibility** | all-MiniLM-L6-v2 | Widely used, well-documented |

---

## References

### Official Documentation
- [ONNX Runtime C# Getting Started](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [ONNX Runtime Performance Tuning](https://onnxruntime.ai/docs/performance/)
- [Graph Optimizations](https://onnxruntime.ai/docs/performance/model-optimizations/graph-optimizations.html)
- [Quantization Guide](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html)
- [Thread Management](https://onnxruntime.ai/docs/performance/tune-performance/threading.html)
- [Execution Providers](https://onnxruntime.ai/docs/execution-providers/)

### Hugging Face Resources
- [Optimum ONNX Export Guide](https://huggingface.co/docs/optimum-onnx/en/onnx/usage_guides/export_a_model)
- [ONNX Runtime Inference with Optimum](https://huggingface.co/docs/optimum-onnx/onnxruntime/usage_guides/models)

### NuGet Packages
- [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime) - v1.23.2
- [Microsoft.ML.OnnxRuntime.Gpu](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu) - CUDA support
- [BERTTokenizers](https://www.nuget.org/packages/BERTTokenizers) - C# tokenization

### Additional Resources
- [Cross-Language Embedding Generation (yuniko.software)](https://yuniko.software/hugging-face-tokenizer-to-onnx-model/)
- [Implementing Embeddings with ONNX and Semantic Kernel](https://elguerre.com/2025/05/25/implementing-embeddings-via-onnx-with-semantic-kernel-for-local-rag-solutions-in-net/)
- [ONNX vs PyTorch Performance Comparison](https://dev-kit.io/blog/machine-learning/onnx-vs-pytorch-speed-comparison)
