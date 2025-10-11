---
description: X-ray summaries for token-efficient repository exploration (headline, summary, structure)
documentationCategory: comprehensive
tags: [repoql, xray, summary, artifact, agents]
audience: AI agents implementing RepoQL producers
---

# X-ray Summaries (XRay)

## Purpose

You're implementing a producer that indexes files into RepoQL. When you create an `artifact`, populate three x-ray fields: `headline`, `summary`, `structure`. These enable efficient exploration by other agents (including future you).

**The problem**: Reading 1,000 files costs ~2M tokens. But agents need discovery before deep-dive.

**The solution**: Progressive disclosure. Scan hundreds → investigate dozens → deep-dive a few → read one.

Token costs: **2 → 10 → 30 → 2,000** (99.9% reduction)

---

## Three Levels

### Level 0: Headline

**Goal**: Enable scanning. Single line (strict). Enough to filter 1,000 files down to 20 candidates.

**What to include**:
- Filename or primary entity name
- Type or role (Library, class, API, configuration)
- 2-3 identifying facts (frameworks, version, key technology)
- Counts of significant elements (methods, endpoints, sections)

**Format**: Whatever fits in one line and is scannable. Pipe-delimited works well for most things, but use what makes sense.

**Examples**:
```
Pushpay.Core.csproj | Library | net48,net8.0 | 3 project refs, 25 packages
PaymentService.cs | 1 class, 12 methods, 3 interfaces | 450 LOC
docker-compose.yml | 8 services | 3 networks | 2 volumes
Authentication.md | 5 sections, 3 code examples | 2.1 KB
```

**Think**: Terminal `ls -l` with semantic richness. Grep-friendly. Humans scan it, agents filter on it.

---

### Level 1: Summary

**Goal**: Enable decision-making. Answer "should I open this file?"

**Target**: ~5-7 lines. Hard limit: 10 lines.

**What to include**:
- Identity (name, type, role)
- Purpose or primary function
- Key components (APIs, sections, dependencies)
- 1-2 notable facts that affect usage

**Format**: Whatever communicates structure clearly. YAML-style key-value works well for many cases. Lists work. Tables work. Use what fits the content.

**Examples**:

```yaml
# C# project
Name: Pushpay.Core
Type: Library
Frameworks: net48, net8.0
Project References: Pushpay.Base, Pushpay.RabbitFood, Equestria.Dynamo
Key Packages: AWSSDK.S3 (3.7.0), Dapper (2.0.78), Serilog (2.12.0)
Build: TreatWarningsAsErrors=true
```

```yaml
# C# class
Namespace: Pushpay.Services
Type: public class PaymentService : IPaymentService
Purpose: Process payments, refunds, and subscription billing
Public API: ProcessPayment(), RefundPayment(), CreateSubscription()
Dependencies: IPaymentGateway, IRepository, IEventBus
Notable: Async/await, retry logic, idempotent operations
```

```
# Could also be prose if that's clearer
Terraform configuration for AWS infrastructure. Defines VPC, subnets, security
groups, ECS cluster, and RDS instances. Uses remote state in S3. Requires
AWS credentials. Production environment configuration.
```

**Think**: The "80% use case". Most agents stop here. Either they got enough info, or they know to keep digging. Don't force them to read full files to answer basic questions.

---

### Level 2: Structure

**Goal**: Enable navigation. Show complete outline so agents can locate specific elements without reading implementations.

**Target**: ~15-20 lines. Hard limit: 25 lines.

**What to include**:
- Complete structural outline
- Full signatures for code (method declarations, not bodies)
- Hierarchies for documents (heading trees)
- Full configurations for configs (key hierarchies, not all values)
- Enough detail to answer "where is X?"

**Format**: Whatever shows the structure clearly. Hierarchical indentation for code. Heading trees for docs. Configuration outlines for configs. Use what fits.

**Examples**:

```yaml
# C# project - list form
Name: Pushpay.Core
Type: Library
SDK: Microsoft.NET.Sdk
Frameworks: net48, net8.0

Project References: (3)
  - file:///S:/Source/Pushpay.Base/Pushpay.Base.csproj
  - file:///S:/Source/Pushpay.RabbitFood/Pushpay.RabbitFood.csproj
  - file:///S:/Source/Equestria.Dynamo/Equestria.Dynamo.csproj

Package References: (25)
  AWSSDK.Kinesis: 3.7.0.8
  AWSSDK.S3: 3.7.0.9
  EPPlus: 4.5.3.3
  [... 22 more packages]

Build Properties:
  OutputPath: build/debug/
  TreatWarningsAsErrors: true
```

```
# C# class - pseudo-code outline
namespace Pushpay.Services
  public class PaymentService : IPaymentService
    public PaymentService(IPaymentGateway gateway, IRepository repo, IEventBus bus)

    // Payment processing
    public async Task<PaymentResult> ProcessPayment(PaymentRequest request)
    public async Task<RefundResult> RefundPayment(Guid paymentId, decimal amount)
    public async Task<PaymentStatus> GetPaymentStatus(Guid paymentId)

    // Subscription management
    public async Task<Subscription> CreateSubscription(SubscriptionRequest request)
    public async Task UpdateSubscription(Guid id, SubscriptionUpdate update)

    // Private helpers
    private async Task<bool> ValidatePaymentMethod(PaymentMethod method)
    private async Task PublishPaymentEvent(PaymentEvent evt)
```

```markdown
# Markdown - heading tree
# Authentication Guide

## Overview
Introduction and architecture diagram

## Setup
### Prerequisites
### Installation
### Configuration

## Token Flow
### Login Flow
### Refresh Flow
Diagram: token-lifecycle.png

## Error Handling
Common errors table

## Testing
Example requests

## References
Links: security-architecture.md, api-reference.md
```

**Think**: Before reading the full file, the agent needs a map. Show them the territory. Signatures matter (types, parameters). Bodies don't (implementations). Organization matters (grouping, hierarchy). Details don't (individual property values).

**Truncation**: If you have 100 methods, show the structure, not all 100. Show enough to understand the organization, then truncate with `[... N more items]`. Same for dependencies, configuration keys, etc.

---

## Principles

### 1. Progressive Disclosure

Each level must provide enough information to decide whether to continue.

- **Headline**: Is this file relevant? (scan 1000 → filter to 20)
- **Summary**: Should I open this? (investigate 20 → select 3)
- **Structure**: Where should I look? (navigate 3 → read 1)

Don't force agents to jump levels. If the answer is in level 1, they should get it there.

### 2. Format Follows Content

Different content types need different formats:
- **Structured data** (projects, configs): Key-value or hierarchical lists
- **Code**: Pseudo-code outlines with signatures
- **Prose** (docs, READMEs): Paragraph summaries and heading trees
- **APIs**: Endpoint lists and schema definitions

Use whatever format makes the structure clear. YAML-style works for many cases, but it's not a requirement. Consistency within a content type matters more than consistency across types.

### 3. Deterministic When Possible

Prefer parsing over guessing:
- Parse ASTs for code
- Parse DOM for documents
- Parse YAML/JSON for configs

Fallback hierarchy when parsing fails:
1. Full parse (AST/DOM) - best quality
2. Regex patterns - good quality
3. Line counting + keywords - basic quality
4. File metadata only - minimal quality
5. NULL - binary files or complete failures

Never fabricate. Better to have minimal x‑ray content than incorrect content.

### 4. Token Efficiency Matters

The point is to save tokens. Make choices that optimize for this:
- **Show vs. Tell**: `3 methods` not "has several methods"
- **Structure vs. Prose**: Key-value pairs not paragraphs (except for docs where prose is the content)
- **Truncate intelligently**: Show 10-15 items then `[... N more]`, don't try to fit everything
- **Public > Private**: Show public APIs always, private details only if they reveal important structure

### 5. Full URIs for Navigation

In structure, use full RepoQL URIs (file:///...) not relative paths. This enables direct navigation. Agents should be able to jump to any referenced artifact without path resolution.

---

## Inclusion & Duplication Policy

- X‑ray fields are designed to be composed by consumers. Keep each field self-contained and non-overlapping:
  - Do NOT include the headline inside the summary or structure fields.
  - Do NOT repeat summary lines inside structure.
- Rationale: avoids double counting tokens and lets UIs compose the three levels depending on context (e.g., show headline + summary; or show headline + structure).
- Consumers are encouraged to render headline above summary/structure when presenting higher x‑ray levels.

---

## Common Counts (Guidance)

Use short, canonical keys when you include counts in the headline or summary. Omit zeros.

- Code: `classes`, `methods`, `functions`, `interfaces`, `enums`
- Docs: `headings`, `links`, `images`, `codeblocks`
- APIs/specs: `endpoints`, `schemas`
- Config/data: `keys`, `sections`
- Tests: `tests`, `suites`

Examples: `PaymentService.cs | class: 1, methods: 12 | 450 LOC`, `openapi.yaml | endpoints: 42, schemas: 18`.

Keep names lowercase and prefer domain terms an agent will recognize.

---

## Persisting the Structured Model (Optional)

If you want to cache or inspect the inputs used to render x‑ray text, persist a structured model as an annotation instead of adding new columns:

- `annotation.kind`: `metadata`
- `severity`: `info`
- `source`: `metadata-generator` (or your producer name)
- `scope_document_id`: the document
- `semantic_key`: stable key (e.g., `metadata:{digest}:{generator}:{profile}`) for idempotent upsert
- `data` (example):

```json
{
  "generator_version": "v1",
  "templates": {
    "headline": "xray/headline.v1",
    "summary":  "xray/summary.v1",
    "structure": "xray/structure.v1"
  },
  "model": {
    "uri": "file:///repo/AuthService.cs",
    "file_name": "AuthService.cs",
    "media_kind": "cs.class",
    "size_bytes": 12845,
    "line_count": 420,
    "counts": { "classes": 1, "methods": 12 },
    "primary_symbols": ["PaymentService"],
    "dependencies": ["Serilog", "Dapper"],
    "structure_items": [ { "kind": "method", "label": "ProcessPayment(...)", "uri": "file:///...#line=120" } ]
  }
}
```

Renderers can re-hydrate this to produce `artifact.headline/summary/structure` when templates evolve, without reparsing the full file.

---

## How Agents Use X‑ray

Understanding consumption patterns helps you generate better x‑ray summaries.

### Pattern 1: Scanning

Agent: "Find all files related to authentication"

```sql
SELECT repository_uri_file_name(n.uri), a.headline
FROM node n JOIN artifact a ON a.id = n.artifact_id
WHERE lower(a.headline) LIKE '%auth%'
ORDER BY a.headline;
```

Cost: ~2 tokens/file × 1000 files = **2,000 tokens**

**Your goal**: Make headlines grep-friendly. Put identifying keywords where agents can filter on them.

---

### Pattern 2: Investigating

Agent: "What does AuthService.cs do?"

```sql
SELECT a.summary FROM node n JOIN artifact a ON a.id = n.artifact_id
WHERE n.uri = 'file://.../AuthService.cs';
```

Cost: **~10 tokens**

**Your goal**: Answer "should I read this file?" The agent is deciding whether to go deeper or look elsewhere.

---

### Pattern 3: Deep Diving

Agent: "Where is the RefreshToken method in AuthService.cs?"

```sql
SELECT a.structure FROM node n JOIN artifact a ON a.id = n.artifact_id
WHERE n.uri = 'file://.../AuthService.cs';
```

Cost: **~30 tokens**

**Your goal**: Show enough detail to locate any element. Include signatures so the agent understands what it's looking at without reading the implementation.

---

### Pattern 4: Progressive Drill-Down

Complete workflow: "Implement token refresh in authentication system"

1. Scan headlines for "auth" → 3 files → **6 tokens**
2. Read summary of AuthService.cs → confirms RefreshToken() exists → **10 tokens**
3. Read structure → finds signature and location → **30 tokens**
4. Read full file → gets implementation → **2,000 tokens**

Total: **2,046 tokens** vs. 2,000,000 (reading all files) = **99.9% reduction**

---

## Edge Cases

### Tiny Files (< 10 lines)

Scale down. All three levels can be similar if the file is simple. The progressive disclosure still works because token costs scale down.

Headline might be just "config.json | 3 keys"
Summary might be just "Name: config.json, Type: Configuration, Keys: database, logging, features"
Structure might be 5 lines showing the actual keys.

### Huge Files (> 1000 lines)

X‑ray becomes critical. Focus on entry points and organization:
- Headline: Aggregate counts
- Summary: High-level architecture
- Structure: Top-level organization, truncate aggressively

Don't try to represent everything. Show what matters for navigation.

### Multiple Entities in One File

Common in some languages (Python, JavaScript). Show all entities with counts:
- Headline: `utils.py | 5 functions, 2 classes | 280 LOC`
- Summary: List all top-level entities
- Structure: Show all with signatures/hierarchies

### Parse Failures

Graceful degradation:
1. Try full parse
2. Try regex patterns
3. Fall back to file metadata (name, size, extension)
4. Store NULL if nothing works (binary files)

Never make up structure you didn't parse.

---

## Storage

Three nullable VARCHAR columns in `artifact` table:

```sql
headline     VARCHAR,  -- single line
summary      VARCHAR,  -- ~5-10 lines
structure    VARCHAR   -- ~15-25 lines
```

**Encoding**: UTF-8, LF line breaks (`\n`), no tabs (use spaces), strip trailing whitespace

**NULL is okay**: If you can't generate x‑ray content (binary file, parse failure), store NULL. Better than garbage.

---

## Summary

Three fields, three purposes:

| Field | Purpose | Lines | Token Cost |
|-------|---------|-------|------------|
| headline | Scanning - filter candidates | 1 | ~2 |
| summary | Investigating - decide to open | 5-10 | ~10 |
| structure | Navigating - locate elements | 15-25 | ~30 |

Key principles:
1. **Progressive disclosure** - each level answers specific questions
2. **Format follows content** - use what makes sense
3. **Deterministic when possible** - parse, don't guess
4. **Token efficiency** - that's the whole point
5. **Full URIs** - enable navigation

Result: 99.9% token reduction for typical workflows.

Generate x‑ray summaries carefully. Other agents (including future you) depend on it.
