# Embedding Models for Developer Laptops

Research for selecting local embedding models that run on typical developer hardware.

*Research date: January 19, 2026*

## Context

This research informs decisions about which embedding models and runtimes to use for local development—semantic search, RAG pipelines, code similarity. Constraint: developer laptop hardware with CPU-primary inference, 8-32GB RAM, no dedicated GPU assumed.

---

## Small Models (<500M Parameters)

### EmbeddingGemma (Google)

Released September 2025. Apache 2.0 license.

| Spec | Value |
|------|-------|
| Parameters | 308M |
| Disk size | <200MB (quantized) |
| Dimensions | 768 (truncatable to 512, 256, 128) |
| Context | 2,048 tokens |
| Languages | 100+ |

MTEB English: 69.67, MTEB Multilingual: 61.15 (as of September 2025; rankings change). Highest-scoring model under 500M parameters at time of release.

> [Google Developers Blog](https://developers.googleblog.com/en/introducing-embeddinggemma/) — release announcement
> [Hugging Face Blog](https://huggingface.co/blog/embeddinggemma) — quantization options (int4, int8, mixed precision)

### Nomic Embed Text V2 (Nomic AI)

Released February 2025. Apache 2.0 license (changed from restricted in 2024).

| Spec | Value |
|------|-------|
| Parameters | 475M total, 305M active (MoE) |
| Disk size | 273MB (Q2_K) to 958MB (F16) |
| Dimensions | 768 (truncatable to 256) |
| Context | 512 tokens |
| Languages | ~100 |

BEIR: 52.86, MIRACL: 65.80.

> [Nomic Blog](https://www.nomic.ai/blog/posts/nomic-embed-text-v2) — release announcement
> [Hugging Face GGUF](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe-GGUF) — quantization formats

### all-MiniLM-L6-v2 (Sentence Transformers)

Released 2021. Apache 2.0 license.

| Spec | Value |
|------|-------|
| Parameters | 22.7M |
| Disk size | ~22MB (weights), ~43MB (FP16) |
| Dimensions | 384 |
| Context | 256 tokens (128 optimal) |
| Languages | English |

MTEB Average: 56.09. Smallest and fastest model in this survey—widely used for prototyping and resource-constrained environments.

> [Hugging Face](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — model card

### gte-multilingual-base (Alibaba)

| Spec | Value |
|------|-------|
| Parameters | 305M |
| Disk size | 324MB (int8 ONNX), 1.2GB (fp32) |
| Dimensions | 768 (elastic: 128-768) |
| Context | 8,192 tokens |
| Languages | 70-75 |

Vendor claims <2% retrieval loss at 128-dim with elastic dimensions.

> [Hugging Face](https://huggingface.co/Alibaba-NLP/gte-multilingual-base) — model card
> [Alibaba Cloud](https://www.alibabacloud.com/blog/gte-multilingual-series-a-key-model-for-retrieval-augmented-generation_601776) — technical overview

### BGE-small-en-v1.5 (BAAI)

MIT license.

| Spec | Value |
|------|-------|
| Parameters | 33.4M |
| Disk size | ~130MB |
| Dimensions | 384 |
| Context | 512 tokens |
| Languages | English |

MTEB score not found in sources reviewed.

> [Hugging Face](https://huggingface.co/BAAI/bge-small-en-v1.5) — model card

---

## Runtimes

### Sentence Transformers (Python)

Most widely adopted library for embedding generation in Python. De facto standard for local development.

- Hundreds of models from Hugging Face
- Devices: CPU, CUDA, MPS (Apple Silicon)
- ONNX backend available: reported 1.4x-3x CPU speedup
- Batching via `batch_size` parameter (default: 32)
- `pip install sentence-transformers`

> [Sentence Transformers Docs](https://sbert.net/docs/sentence_transformer/usage/efficiency.html) — performance
> [MarktechPost](https://www.marktechpost.com/2024/10/17/from-onnx-to-static-embeddings-what-makes-sentence-transformers-v3-2-0-a-game-changer/) — ONNX speedup

### FastEmbed (Qdrant)

- ONNX-based, designed for CPU inference
- Reports 2,500 queries/sec on consumer CPUs
- Operator fusion and INT8 quantization
- `pip install fastembed`

> [Johal.in](https://johal.in/fastembed-onnx-lightweight-embedding-inference-2025/) — benchmarks

### Ollama (Local Server)

Popular choice when already using Ollama for LLM inference—unified stack for both embeddings and generation.

- Models: nomic-embed-text, mxbai-embed-large, all-minilm, BGE variants
- GPU support: NVIDIA, AMD, Apple Silicon
- CPU-only works with quantized models
- Default endpoint: `http://127.0.0.1:11434`
- Integrates with LangChain, LlamaIndex

> [Ollama Embedding Models](https://ollama.com/search?c=embedding) — available models
> [Collabnix Guide](https://collabnix.com/ollama-embedded-models-the-complete-technical-guide-to-local-ai-embeddings-in-2025/) — deployment guide

Reported: Intel i7-1355U achieves ~7.54 tokens/s with mxbai-embed-large.

> [Medium](https://medium.com/@ttio2tech_28094/local-large-language-models-hardware-benchmarking-ollama-benchmarks-cpu-gpu-macbooks-c696abbec813) — hardware benchmarks

Note: Embedding output may vary between Ollama versions.

> [GitHub Issue #7085](https://github.com/ollama/ollama/issues/7085) — filed October 2024, status unclear

### llama.cpp (C++ Inference)

- GGUF format models
- Designed for commodity hardware
- CPU: AVX, AVX2, AVX512 (x86); NEON, SVE (ARM)
- GPU: CUDA, Metal, Vulkan, OpenCL
- Quantization: 1.5-bit through 8-bit
- Python: `llama-cpp-python`

> [llama.cpp GitHub](https://github.com/ggml-org/llama.cpp) — repository
> [Arm Streamline Guide](https://learn.arm.com/learning-paths/servers-and-cloud-computing/llama_cpp_streamline/2_llama.cpp_intro/) — profiling

### transformers.js (Browser/Node.js)

- ONNX-exported models
- WebAssembly (default) or WebGPU
- WebGPU reported 40x-75x speedup over WASM on M3 Max
- Runs in browser, Node.js, Deno, React Native
- `npm install @huggingface/transformers`

> [Transformers.js Docs](https://huggingface.co/docs/transformers.js/en/index) — documentation
> [Hugging Face WebGPU Benchmark](https://huggingface.co/posts/Xenova/906785325455792) — speedup measurements

### Hugging Face TEI (Text Embeddings Inference)

- Rust-based server, Docker deployment
- CPU image: `ghcr.io/huggingface/text-embeddings-inference:cpu-1.6`
- Token-based dynamic batching
- OpenAI-compatible API

> [TEI GitHub](https://github.com/huggingface/text-embeddings-inference) — repository
> [TEI CPU Docs](https://huggingface.co/docs/text-embeddings-inference/en/local_cpu) — CPU deployment

### Intel Optimum (CPU Optimization)

- Optimized for Intel CPUs (AVX-512, VNNI, AMX)
- Reported up to 4x throughput vs baseline bf16
- Reported <1% accuracy loss on MTEB STS benchmarks
- Peak throughput at batch size 128 (Xeon 8480+)

> [Hugging Face Blog](https://huggingface.co/blog/intel-fast-embedding) — benchmarks on Xeon

---

## Resource Requirements

### RAM by Model Size

| Parameters | FP16 RAM | Int4 RAM |
|------------|----------|----------|
| 22M | ~44 MB | ~11 MB |
| 100M | ~200 MB | ~50 MB |
| 300M | ~600 MB | ~150 MB |
| 500M | ~1 GB | ~250 MB |

Formula: Parameters × bytes-per-parameter (FP32=4, FP16=2, Int8=1, Int4=0.5). Add ~20% for inference overhead.

> [Medium](https://jaydevtonde.substack.com/p/understanding-model-memory-calculations) — calculation
> [Hugging Face](https://huggingface.co/docs/accelerate/usage_guides/model_size_estimator) — overhead

### CPU Inference Speed

| Model | Reported Speed | Source |
|-------|---------------|--------|
| all-MiniLM-L6-v2 | 5,000-14,000 sent/sec (multi-thread) | [Milvus](https://milvus.io/ai-quick-reference/what-are-some-popular-pretrained-sentence-transformer-models-and-how-do-they-differ-for-example-allminilml6v2-vs-allmpnetbasev2) |
| nomic-embed-text (Ollama, M2 Max) | 9,340 tokens/sec | [Collabnix](https://collabnix.com/ollama-embedded-models-the-complete-technical-guide-for-2025-enterprise-deployment/) |
| FastEmbed ONNX | 2,500 queries/sec | [Johal.in](https://johal.in/fastembed-onnx-lightweight-embedding-inference-2025/) |

### Quantization Impact

| Quantization | Memory Reduction | Reported Speed Change | Reported Accuracy Loss |
|--------------|-----------------|----------------------|------------------------|
| FP32 → Int8 | 4x | 3.66x average | <2% |
| FP32 → Int4 | 8x | 2.4x (single-stream) | 3-6% |

> [Hugging Face Blog](https://huggingface.co/blog/embedding-quantization) — quantization benchmarks

### Batch Size Effects

| Batch Size | Notes |
|------------|-------|
| 8-16 | Reported suitable for CPU-only |
| 32 | Sentence Transformers default |
| 64-128 | Reported peak throughput for quantized models on Xeon |

Tradeoff: batch 64→128 may improve throughput ~20% but increase latency ~50%.

> [Zilliz](https://zilliz.com/ai-faq/what-is-the-impact-of-batch-size-on-embedding-generation-throughput) — tradeoffs
> [Hugging Face](https://huggingface.co/blog/intel-fast-embedding) — Xeon batching

### RAM Tier Constraints

**8GB RAM:**
- MiniLM (43MB), nomic-embed-text quantized (~500MB) viable
- System overhead limits headroom

**16GB RAM:**
- mxbai-embed-large (1.2GB), BGE-M3 (1.1GB) viable

**32GB RAM:**
- All models in this survey viable

> [Local AI Master](https://localaimaster.com/blog/ram-requirements-local-ai) — RAM guidance

---

## Gaps

- **MTEB scores for gte-multilingual-base**: Not found in sources reviewed; check [MTEB Leaderboard](https://huggingface.co/spaces/mteb/leaderboard)
- **MTEB scores for BGE-small-en-v1.5**: Not found in sources reviewed
- **TEI throughput benchmarks**: Specific numbers not published
- **Ollama version consistency**: [GitHub Issue #7085](https://github.com/ollama/ollama/issues/7085) reports embedding variance between versions; resolution status unclear
- **Code-specific models**: CodeBERT, UniXcoder not evaluated; may be relevant for code search use cases
- **Jina embeddings**: jina-embeddings-v3 not evaluated

---

## Summary Tables

### Models

| Model | Params | Size (quant) | Dims | Languages | MTEB | Context |
|-------|--------|--------------|------|-----------|------|---------|
| EmbeddingGemma | 308M | <200MB | 768 | 100+ | 69.67 (EN) | 2K |
| Nomic V2 | 305M active | 273MB | 768 | ~100 | BEIR 52.86 | 512 |
| all-MiniLM-L6-v2 | 22.7M | ~22MB | 384 | EN | 56.09 | 256 |
| gte-multilingual-base | 305M | 324MB | 768 | 70-75 | — | 8K |
| BGE-small-en-v1.5 | 33.4M | ~130MB | 384 | EN | — | 512 |

### Runtimes

| Runtime | Language | Format | Batching | Browser |
|---------|----------|--------|----------|---------|
| Sentence Transformers | Python | PyTorch/ONNX | Manual | No |
| FastEmbed | Python | ONNX | Yes | No |
| Ollama | Go/C++ | GGUF | Via server | No |
| llama.cpp | C++ | GGUF | Continuous | No |
| transformers.js | JS | ONNX | Yes | Yes |
| TEI | Rust | Safetensors | Dynamic | No |
| Intel Optimum | Python | OpenVINO | Manual | No |
