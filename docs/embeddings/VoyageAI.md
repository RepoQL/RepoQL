# Voyage AI Embedding Models

Comprehensive documentation covering Voyage AI's embedding model offerings, technical specifications, and integration guidance for RepoQL.

## Table of Contents

- [Company Overview](#company-overview)
- [Model Portfolio](#model-portfolio)
- [voyage-code-3 Deep Dive](#voyage-code-3-deep-dive)
- [Technical Specifications](#technical-specifications)
- [API Usage](#api-usage)
- [Benchmark Comparisons](#benchmark-comparisons)
- [Integration Considerations](#integration-considerations)
- [Pricing](#pricing)

---

## Company Overview

### Background

[Voyage AI](https://www.voyageai.com/) is an AI company specializing in embedding models and rerankers for precise and efficient search and retrieval-augmented generation (RAG). Founded in 2023 by **Tengyu Ma** (CEO, Stanford Assistant Professor), **Hong Liu**, and **Kaidi Cao**, the company is headquartered in Palo Alto, California.

The team combines academic expertise from Stanford, MIT, and UC Berkeley with industry experience from Google, Meta, Uber, and other leading technology companies.

**Key Advisors:**
- Christopher Manning (Stanford Professor)
- Fei-Fei Li (Stanford Professor)
- Christopher Ré (Stanford Professor)

### Funding and Acquisition

- **Total Funding:** $28 million
- **Series A (October 2024):** $20 million led by CRV, with participation from Wing VC, Conviction, Snowflake, Databricks, Pear VC, Tectonic Ventures, Mayfield Fund, and Fusion Fund
- **Acquisition:** MongoDB acquired Voyage AI on February 24, 2025

### Partnerships

Voyage AI has partnered with Databricks, Snowflake, Anthropic, Harvey, and Xayn, with integrations across leading vector database providers.

### Deployment Options

| Option | Description |
|--------|-------------|
| **Voyage API** | Hosted cloud API (primary access method) |
| **AWS SageMaker** | Deploy in your AWS VPC via Marketplace |
| **On-Premises** | Private deployment (contact Voyage AI directly) |

---

## Model Portfolio

### Current Recommended Models

| Model | Purpose | Context | Default Dims | Price/1M Tokens |
|-------|---------|---------|--------------|-----------------|
| **voyage-4-large** | Best quality general-purpose | 32K | 1024 | $0.12 |
| **voyage-4** | Balanced general-purpose | 32K | 1024 | $0.06 |
| **voyage-4-lite** | Lowest latency/cost | 32K | 1024 | $0.02 |
| **voyage-3-large** | State-of-the-art general | 32K | 1024 | $0.22 |
| **voyage-3.5** | Enhanced general-purpose | 32K | 1024 | $0.06 |
| **voyage-3.5-lite** | Cost-effective general | 32K | 1024 | $0.02 |
| **voyage-code-3** | Code retrieval | 32K | 1024 | $0.18 |
| **voyage-finance-2** | Finance domain | 32K | 1024 | $0.12 |
| **voyage-law-2** | Legal domain | 16K | 1024 | $0.12 |
| **voyage-multilingual-2** | Multilingual retrieval | 32K | 1024 | $0.12 |

### Domain-Specific Models

#### voyage-code-3 (Code Retrieval)
Optimized for code retrieval tasks. Outperforms OpenAI-v3-large by 13.80% and CodeSage-large by 16.81% on 32 code retrieval datasets. Supports 300+ programming languages.

#### voyage-finance-2 (Finance)
Specialized for financial document retrieval. Outperforms OpenAI v3 large by 7% and Cohere English v3 by 12% on 11 finance datasets. Excels at numerical reasoning over tabular data.

#### voyage-law-2 (Legal)
Trained on 1 trillion high-quality legal tokens. Outperforms OpenAI v3 large by 15%+ on legal retrieval tasks. 16K context supports lengthy legal documents.

#### voyage-multilingual-2 (Multilingual)
Supports 31+ languages including French, German, Japanese, Spanish, Korean, Bengali, Portuguese, and Russian. Outperforms alternatives by 5.6% on average across all language categories.

### Legacy Models

| Model | Status | Notes |
|-------|--------|-------|
| voyage-2 | Supported | General-purpose, predecessor to voyage-3 |
| voyage-large-2 | Supported | Higher quality variant of voyage-2 |
| voyage-code-2 | Supported | Predecessor to voyage-code-3 |
| voyage-lite-01 | Deprecated | Use voyage-3-lite or newer |

### Voyage 4 Series Features

The Voyage 4 model family introduces **shared embedding spaces** - an industry-first capability where all Voyage 4 models produce compatible embeddings. This enables:

- **Asymmetric Retrieval:** Use voyage-4-lite for queries and voyage-4-large for documents
- **Cost Optimization:** Embed documents once with a powerful model, query with a fast/cheap model
- **Seamless Upgrades:** Switch between model tiers without re-embedding

---

## voyage-code-3 Deep Dive

### Overview

voyage-code-3 is Voyage AI's next-generation embedding model optimized specifically for code retrieval. It represents the state-of-the-art for code embedding, outperforming both general-purpose and specialized code embedding models.

### Architecture and Training

**Training Methodology:**
- Matryoshka Representation Learning (MRL) for flexible dimensionality
- Quantization-aware training for precision flexibility
- Contrastive learning with carefully curated positive pairs

**Training Data:**
- Trillions of tokens comprising text, code, and mathematical content
- Carefully tuned code-to-text ratio
- Public GitHub repositories with docstring-code and code-code pairs
- 300+ programming languages covered
- Real-world query-code pairs from code assistant use cases
- Combined with general text pair dataset from voyage-3

### Supported Languages

voyage-code-3 supports **300+ programming languages** including but not limited to:

- **Popular Languages:** Python, JavaScript, TypeScript, Java, C++, C#, Go, Rust, Ruby, PHP
- **Systems Languages:** C, Assembly, Verilog, VHDL
- **Scripting Languages:** Bash, PowerShell, Perl, Lua
- **Data/ML Languages:** R, Julia, MATLAB, SQL
- **Web Technologies:** HTML, CSS, SCSS, JSON, XML, YAML
- **Functional Languages:** Haskell, Scala, F#, Clojure, Elixir
- **And many more...**

### Code vs Documentation Performance

voyage-code-3 handles multiple code retrieval subtasks:

| Task Type | Description | Performance |
|-----------|-------------|-------------|
| **Text-to-Code** | Natural language query to code | Excellent |
| **Code-to-Code** | Find similar code snippets | Excellent |
| **Docstring-to-Code** | Documentation to implementation | Excellent |
| **Query-Code** | Real-world search patterns | Excellent |

### Benchmark Results

**Evaluated on 32 datasets across 5 categories:**

| Comparison | Improvement |
|------------|-------------|
| vs OpenAI-v3-large (average) | +13.80% |
| vs CodeSage-large (average) | +16.81% |
| vs OpenAI at 1024 dimensions | +14.64% |
| vs OpenAI at 256 dimensions | +17.66% |
| Binary rescoring improvement | up to +4.25% |

**Context Length Advantage:**

| Model | Context Length |
|-------|----------------|
| voyage-code-3 | 32K tokens |
| OpenAI text-embedding-3-large | 8K tokens |
| CodeSage-large | 1K tokens |

### Comparison with Other Code Embedding Models

| Model | Quality | Context | Dimensions | Key Strength |
|-------|---------|---------|------------|--------------|
| **voyage-code-3** | Best | 32K | 256-2048 | Overall best, flexible dimensions |
| OpenAI text-embedding-3-large | Good | 8K | 3072 | Wide availability |
| CodeSage-large | Moderate | 1K | Fixed | Open source option |
| Jina-v2-code | Moderate | 8K | 768 | Open source |
| CodeRankEmbed | Moderate | 8K | 768 | Specialized ranking |

---

## Technical Specifications

### Embedding Dimensions

voyage-code-3 and other modern Voyage models support flexible dimensionality via Matryoshka learning:

| Dimensions | Use Case | Storage (float32) |
|------------|----------|-------------------|
| 2048 | Maximum quality | 8KB per embedding |
| 1024 (default) | Balanced quality/cost | 4KB per embedding |
| 512 | Reduced storage | 2KB per embedding |
| 256 | Minimum storage | 1KB per embedding |

**How Matryoshka Learning Works:**

The model is trained so that each prefix of the full embedding vector remains semantically meaningful. The first 256 dimensions contain the most semantically rich information, with subsequent dimensions adding progressively finer-grained details.

```
Full 2048-dim embedding:
[d1, d2, ..., d256] [d257, ..., d512] [d513, ..., d1024] [d1025, ..., d2048]
      ^                    ^                  ^                   ^
   Core meaning    Added detail       More detail          Fine detail
```

### Maximum Context Lengths

| Model | Max Context |
|-------|-------------|
| voyage-4-large, voyage-4, voyage-4-lite | 32K tokens |
| voyage-3-large, voyage-3.5, voyage-3.5-lite | 32K tokens |
| voyage-code-3 | 32K tokens |
| voyage-finance-2 | 32K tokens |
| voyage-law-2 | 16K tokens |
| voyage-multilingual-2 | 32K tokens |

### Input Types

The `input_type` parameter optimizes embeddings for retrieval:

| Value | Description | Internal Behavior |
|-------|-------------|-------------------|
| `None` | Direct conversion | No prompt prepended |
| `"query"` | Search queries | Prepends query-optimized prompt |
| `"document"` | Documents for indexing | Prepends document-optimized prompt |

**Recommendation:** Always specify `input_type` for retrieval tasks.

### Truncation Behavior

| Parameter | Default | Behavior |
|-----------|---------|----------|
| `truncation=True` | Yes | Automatically truncate over-length inputs |
| `truncation=False` | No | Raise error if input exceeds context |

When truncation is enabled (default), text exceeding the context length is silently truncated before embedding. For multimodal inputs, if truncation occurs mid-image, the entire image is discarded.

### Output Data Types (Quantization)

| Type | Bits | Storage | Quality | Use Case |
|------|------|---------|---------|----------|
| `float` | 32-bit | Baseline | Best | Default, maximum precision |
| `int8` | 8-bit | 4x smaller | Near-best | Production with quality focus |
| `uint8` | 8-bit | 4x smaller | Near-best | Unsigned variant of int8 |
| `binary` | 1-bit | 32x smaller | Good | Maximum compression |
| `ubinary` | 1-bit | 32x smaller | Good | Unsigned binary |

**Quantization + MRL Combination:**

voyage-code-3 at 512-dim int8 achieves:
- 8.56% better performance than OpenAI at 3072-dim float
- Uses only 1/24th the storage

---

## API Usage

### Authentication

```bash
# Set environment variable
export VOYAGE_API_KEY="your-api-key-here"
```

Or pass directly to the client:

```python
import voyageai
vo = voyageai.Client(api_key="your-api-key-here")
```

### Python SDK Installation

```bash
pip install -U voyageai
```

### Basic Usage

```python
import voyageai

vo = voyageai.Client()  # Uses VOYAGE_API_KEY env var

# Embed documents
documents = [
    "def hello_world():\n    print('Hello, World!')",
    "function greet() { console.log('Hello'); }"
]
result = vo.embed(
    documents,
    model="voyage-code-3",
    input_type="document"
)

# Access embeddings
embeddings = result.embeddings  # List of vectors
total_tokens = result.total_tokens

# Embed a query
query = "Python function that prints greeting"
query_result = vo.embed(
    [query],
    model="voyage-code-3",
    input_type="query"
)
query_embedding = query_result.embeddings[0]
```

### Specifying Output Options

```python
# Smaller dimensions for storage efficiency
result = vo.embed(
    texts,
    model="voyage-code-3",
    input_type="document",
    output_dimension=512,  # 256, 512, 1024, or 2048
    output_dtype="int8"    # float, int8, uint8, binary, ubinary
)
```

### Batch Embedding

```python
import voyageai

vo = voyageai.Client()

# Process large document sets in batches
batch_size = 128  # Recommended batch size
documents = [...]  # Your list of documents

all_embeddings = []
for i in range(0, len(documents), batch_size):
    batch = documents[i:i + batch_size]
    result = vo.embed(
        batch,
        model="voyage-code-3",
        input_type="document"
    )
    all_embeddings.extend(result.embeddings)
```

### Async/High-Concurrency Requests

```python
import asyncio
import aiohttp
import os

async def embed_async(texts: list[str], model: str = "voyage-code-3"):
    async with aiohttp.ClientSession() as session:
        async with session.post(
            "https://api.voyageai.com/v1/embeddings",
            headers={"Authorization": f"Bearer {os.getenv('VOYAGE_API_KEY')}"},
            json={
                "model": model,
                "input": texts,
                "input_type": "document"
            }
        ) as response:
            data = await response.json()
            return data["data"]

# Run multiple embedding requests concurrently
async def main():
    batches = [texts_batch_1, texts_batch_2, texts_batch_3]
    results = await asyncio.gather(*[embed_async(b) for b in batches])
    return results

embeddings = asyncio.run(main())
```

### Tokenization and Token Counting

```python
# Count tokens before embedding
token_count = vo.count_tokens(documents)
print(f"Total tokens: {token_count}")

# Get tokenized representation
tokenized = vo.tokenize(documents)
for i, doc_tokens in enumerate(tokenized):
    print(f"Document {i}: {len(doc_tokens.tokens)} tokens")
```

### REST API

```bash
curl https://api.voyageai.com/v1/embeddings \
  -H "Authorization: Bearer $VOYAGE_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "voyage-code-3",
    "input": ["def add(a, b): return a + b"],
    "input_type": "document",
    "output_dimension": 1024,
    "output_dtype": "float"
  }'
```

### Rate Limits

**Tier 1 (Base - payment method added):**

| Model | TPM (Tokens/Min) | RPM (Requests/Min) |
|-------|------------------|-------------------|
| voyage-code-3 | 3M | 2,000 |
| voyage-4, voyage-3.5 | 8M | 2,000 |
| voyage-4-lite, voyage-3.5-lite | 16M | 2,000 |

**Tier Progression:**

| Tier | Requirement | Rate Limit Multiplier |
|------|-------------|----------------------|
| Tier 1 | Payment method added | 1x |
| Tier 2 | $100+ billed usage | 2x |
| Tier 3 | $1,000+ billed usage | 3x |

**Free Tier:** Limited to 3 RPM (requests per minute)

### Error Handling

```python
from voyageai import VoyageAIError

try:
    result = vo.embed(texts, model="voyage-code-3")
except VoyageAIError as e:
    print(f"Status: {e.status_code}")
    print(f"Message: {e.message}")
    print(f"Body: {e.body}")
```

**Common Error Codes:**

| Code | Meaning | Resolution |
|------|---------|------------|
| 429 | Rate limit exceeded | Implement exponential backoff |
| 400 | Invalid request | Check input format and parameters |
| 401 | Authentication failed | Verify API key |
| 500 | Server error | Retry with backoff |

**Retry Configuration:**

```python
# Python SDK
vo = voyageai.Client(max_retries=3)

# TypeScript SDK
const response = await client.embed(texts, {
    maxRetries: 3,
    timeoutInSeconds: 30
});
```

### SDK Availability

| Language | Package | Installation |
|----------|---------|--------------|
| Python | `voyageai` | `pip install voyageai` |
| TypeScript/Node | `voyageai` | `npm install voyageai` |
| REST API | N/A | Direct HTTP requests |

---

## Benchmark Comparisons

### MTEB Leaderboard Performance

**General Retrieval (voyage-3-large):**

| Model | MTEB Score | Dimensions | Price/1M |
|-------|------------|------------|----------|
| Cohere embed-v4 | 65.2 | 1024 | $0.10 |
| OpenAI text-embedding-3-large | 64.6 | 3072 | $0.13 |
| Voyage AI voyage-3-large | 63.8 | 1536 | $0.12 |
| BGE-M3 | 63.0 | 1024 | Free |
| E5-Mistral-7B-Instruct | 61.8 | 4096 | Free |

### Code Search Benchmarks

**voyage-code-3 vs Competitors (32 datasets):**

| Model | Avg. NDCG@10 | vs voyage-code-3 |
|-------|--------------|------------------|
| **voyage-code-3** | Baseline | - |
| OpenAI text-embedding-3-large | -13.80% | |
| CodeSage-large | -16.81% | |
| voyage-code-2 | -8% (est.) | |
| Jina-v2-code | -15% (est.) | |

### Domain-Specific Performance

**voyage-3-large Across Domains:**

| Domain | vs OpenAI v3 Large | vs Cohere v3 |
|--------|-------------------|--------------|
| Code | +12% | +25% |
| Law | +8% | +18% |
| Finance | +9% | +22% |
| Multilingual | +11% | +19% |
| Long-context | +15% | +24% |
| **Average** | **+9.74%** | **+20.71%** |

### Quality vs Cost Analysis

| Model | Quality Tier | Price/1M | Value Rating |
|-------|--------------|----------|--------------|
| voyage-code-3 | Best (code) | $0.18 | Excellent for code |
| voyage-3.5-lite | Good | $0.02 | Best value general |
| voyage-3-large | Excellent | $0.22 | Premium quality |
| OpenAI v3-large | Good | $0.13 | Wide availability |
| Cohere v4 | Good | $0.10 | Balanced |

**Cost-Effectiveness Examples:**

- voyage-3.5-lite achieves within 0.3% of Cohere-v4 quality at 1/6 the cost
- voyage-code-3 at 512-dim int8 uses 1/24th the storage of OpenAI while being 8.56% better

---

## Integration Considerations

### Latency Characteristics

| Metric | voyage-code-3 | Notes |
|--------|---------------|-------|
| Single query (100 tokens) | ~90ms | API latency |
| Throughput | 12.6M tokens/hour | On ml.g6.xlarge |
| Batch overhead | Minimal | Use batches of 128 |

**Latency Optimization Tips:**

1. Use smaller dimensions (512 or 256) for faster similarity computation
2. Batch requests up to 128 documents per call
3. Consider voyage-4-lite for lowest latency requirements
4. Use int8 or binary quantization for faster vector operations

### Caching Strategies

**Recommended Caching Approach:**

```python
import hashlib
import redis

class EmbeddingCache:
    def __init__(self, redis_client, vo_client, model="voyage-code-3"):
        self.redis = redis_client
        self.vo = vo_client
        self.model = model

    def _cache_key(self, text: str, input_type: str) -> str:
        content = f"{self.model}:{input_type}:{text}"
        return f"emb:{hashlib.sha256(content.encode()).hexdigest()}"

    def get_embedding(self, text: str, input_type: str = "document"):
        key = self._cache_key(text, input_type)

        # Check cache
        cached = self.redis.get(key)
        if cached:
            return json.loads(cached)

        # Generate embedding
        result = self.vo.embed([text], model=self.model, input_type=input_type)
        embedding = result.embeddings[0]

        # Cache result (TTL: 7 days)
        self.redis.setex(key, 604800, json.dumps(embedding))

        return embedding
```

**Caching Considerations:**

- Cache by content hash + model + input_type combination
- Documents rarely change - use long TTL (days/weeks)
- Queries may vary - consider shorter TTL or no caching
- Pre-warm cache during off-peak hours

### Fallback Options

**Multi-Provider Fallback Strategy:**

```python
class EmbeddingService:
    def __init__(self):
        self.voyage = voyageai.Client()
        self.openai = openai.Client()

    async def embed(self, texts: list[str], input_type: str = "document"):
        try:
            # Primary: Voyage AI
            result = self.voyage.embed(
                texts,
                model="voyage-code-3",
                input_type=input_type
            )
            return result.embeddings
        except Exception as e:
            logger.warning(f"Voyage AI failed: {e}, falling back to OpenAI")

            # Fallback: OpenAI
            response = self.openai.embeddings.create(
                model="text-embedding-3-large",
                input=texts
            )
            return [d.embedding for d in response.data]
```

**Note:** Different embedding models produce incompatible vectors. A fallback should only be used for new documents, not for querying existing vector stores.

### Cost Optimization

**Dimension Reduction Strategy:**

| Scenario | Recommended Dims | Estimated Savings |
|----------|------------------|-------------------|
| High-volume, cost-sensitive | 256 | 87.5% storage |
| Balanced production | 512 | 75% storage |
| Quality-focused | 1024 | 50% storage |
| Maximum quality | 2048 | Baseline |

**Quantization Strategy:**

| Precision | Use Case | Storage vs float32 |
|-----------|----------|-------------------|
| float32 | Development, testing | 1x |
| int8 | Production standard | 4x smaller |
| binary | Maximum compression | 32x smaller |

**Batch API Discount:**

- 33% discount on Batch API
- 12-hour completion window
- Ideal for bulk re-embedding or non-urgent workloads

### Vector Database Integration

**Pinecone Example:**

```python
import pinecone
import voyageai

# Initialize
vo = voyageai.Client()
pinecone.init(api_key="your-key", environment="your-env")

# Create index matching Voyage dimensions
index = pinecone.create_index(
    name="code-search",
    dimension=1024,  # Match voyage-code-3 default
    metric="cosine"
)

# Embed and upsert
embeddings = vo.embed(documents, model="voyage-code-3", input_type="document")
vectors = [
    {"id": f"doc_{i}", "values": emb, "metadata": {"text": doc}}
    for i, (doc, emb) in enumerate(zip(documents, embeddings.embeddings))
]
index.upsert(vectors)
```

**Production Recommendations:**

1. Always use `cosine` similarity metric (Voyage embeddings are normalized)
2. Match index dimensions to your chosen output_dimension
3. Store original text in metadata for retrieval
4. Consider binary quantization at the vector DB level for additional compression

---

## Pricing

### Current Pricing (as of January 2026)

| Model | Price per 1M Tokens | Free Tier |
|-------|---------------------|-----------|
| voyage-4-large | $0.12 | 200M tokens |
| voyage-4 | $0.06 | 200M tokens |
| voyage-4-lite | $0.02 | 200M tokens |
| voyage-3-large | $0.22 | 200M tokens |
| voyage-3.5 | $0.06 | 200M tokens |
| voyage-3.5-lite | $0.02 | 200M tokens |
| voyage-code-3 | $0.18 | 200M tokens |
| voyage-context-3 | $0.18 | 200M tokens |
| voyage-finance-2 | $0.12 | 50M tokens |
| voyage-law-2 | $0.12 | 50M tokens |

### Rerankers

| Model | Price per 1M Tokens | Free Tier |
|-------|---------------------|-----------|
| rerank-2.5 | $0.05 | 200M tokens |
| rerank-2.5-lite | $0.02 | 200M tokens |

### Multimodal

| Model | Text Price | Image Price |
|-------|------------|-------------|
| voyage-multimodal-3.5 | $0.12/1M tokens | $0.60/1B pixels |

Free tier: 200M text tokens + 150B pixels

### Discounts

- **Batch API:** 33% discount (12-hour completion window)
- **Volume Discounts:** Contact sales for enterprise pricing

### Cost Estimation

**Example: Indexing 1 million code files (avg 500 tokens each)**

| Model | Tokens | Cost | With Batch Discount |
|-------|--------|------|---------------------|
| voyage-code-3 | 500M | $90 | $60 |
| voyage-3.5-lite | 500M | $10 | $6.70 |

---

## Summary for RepoQL

### Recommended Configuration

For RepoQL's code-focused use case:

```python
# Primary model for code indexing
MODEL = "voyage-code-3"
OUTPUT_DIMENSION = 1024  # Balanced quality/storage
OUTPUT_DTYPE = "float"   # Maximum precision for primary store

# Alternative for cost-sensitive deployments
LITE_MODEL = "voyage-3.5-lite"
LITE_DIMENSION = 512
LITE_DTYPE = "int8"
```

### Key Advantages for RepoQL

1. **Code Specialization:** voyage-code-3 significantly outperforms general-purpose models on code retrieval
2. **300+ Languages:** Comprehensive programming language coverage
3. **32K Context:** Handles large files and functions without truncation
4. **Flexible Dimensions:** Matryoshka learning enables quality/cost tradeoffs
5. **Quantization Options:** Binary quantization enables massive scale

### Considerations

1. **API Dependency:** Cloud-only without on-prem license
2. **Rate Limits:** Plan for batching and backoff at scale
3. **Caching Critical:** Implement aggressive caching to reduce API calls
4. **Fallback Strategy:** Consider OpenAI as fallback for resilience

---

## References

- [Voyage AI Documentation](https://docs.voyageai.com/)
- [Voyage AI Blog - voyage-code-3 Announcement](https://blog.voyageai.com/2024/12/04/voyage-code-3/)
- [Voyage AI Blog - voyage-3-large Announcement](https://blog.voyageai.com/2025/01/07/voyage-3-large/)
- [Voyage AI Pricing](https://docs.voyageai.com/docs/pricing)
- [Voyage AI Rate Limits](https://docs.voyageai.com/docs/rate-limits)
- [MongoDB - Matryoshka Embeddings with Voyage AI](https://www.mongodb.com/company/blog/technical/matryoshka-embeddings-smarter-embeddings-with-voyage-ai)
- [MTEB Leaderboard](https://huggingface.co/spaces/mteb/leaderboard)
- [GitHub - voyageai-python](https://github.com/voyage-ai/voyageai-python)
