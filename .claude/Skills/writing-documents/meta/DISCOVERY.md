---
description: Research on best practices for documentation serving both human and AI agent readers
tags: [documentation, dual-audience, AI, LLM, writing, research, structure]
audience: { human: 40, agent: 60 }
purpose: { gestalt: 0, concepts: 0, reference: 15, research: 85, findings: 0, flow: 0, plan: 0, design: 0, high-agency-process: 0, low-agency-process: 0 }
---

# Dual-Audience Documentation Research

Research for decisions about documentation practices that serve both human and AI agent readers effectively.

*Research date: January 2026*

## Context

Documentation increasingly serves two distinct audiences: humans who scan, skip, and need motivation, and AI agents who read everything, parse structure, and operate within token constraints. This research gathers practices from industry sources and this repository's existing patterns to inform documentation strategy decisions.

---

## The Dual-Audience Challenge

### How Audiences Differ

| Aspect | Human Readers | AI Agent Readers |
|--------|---------------|------------------|
| Attention | Scans, skips, decides in seconds | Reads everything sequentially |
| Motivation | Needs "why should I care?" upfront | Trusts task importance |
| Processing | Visual pattern recognition | Token-based parsing and chunking |
| Memory | ~7 items working memory | Context window limits |
| Repetition | Aids retention | Wastes tokens |
| Structure | Scannable headers, prose | Tables, capsules, structured entries |
| Validation | Trust signals (sources even if unclicked) | Explicit confidence markers |

> [Mintlify](https://www.mintlify.com/blog/structure-documentation-AI-human-readers) — "Human developers skim, jump around, and search for specific answers. AI assistants parse sequentially, pull sections into context windows, and need complete information to avoid hallucination."

### The Convergence Insight

Good documentation for AI agents is also good documentation for humans. The practices overlap more than they diverge.

> [Mintlify](https://www.mintlify.com/blog/structure-documentation-AI-human-readers) — "One source of truth. Two audiences. No duplication."

> [Alation](https://www.alation.com/blog/how-to-write-ai-ready-documentation/) — "The practices that make software more accessible to AI agents also make it better for humans: clearer documentation, more consistent APIs, better code organization."

---

## Structural Practices

### Heading Hierarchy

Semantic structure allows both audiences to understand concept relationships. AI systems map heading hierarchies to understand topic relationships; humans use headers for navigation and scanning.

| Level | Purpose | Example |
|-------|---------|---------|
| H1 | Document identity | # Authentication Service |
| H2 | Major concepts | ## Token Validation |
| H3 | Specific implementations | ### JWT Signature Verification |
| H4 | Edge cases | #### Expired Token Handling |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Replace vague titles like 'Overview' with specific ones like 'AuthenticationFlow' or 'API Rate Limits.'"

> [Microsoft Learn](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/markdown-elements) — "The Layout API uses standard Markdown heading syntax with 1-6 hash symbols (#) corresponding to heading levels."

### Chunking and Section Design

AI systems break documents into small chunks for vector similarity retrieval. Structure directly impacts retrieval accuracy.

| Principle | Rationale |
|-----------|-----------|
| One concept per section | LLM retrieval pulls sections, not documents |
| Self-contained sections | Context may not include adjacent content |
| Complete context per page | AI may retrieve individual pages without navigation context |
| Separate procedures from references | Mixing confuses retrieval systems |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Treat every documentation page as a standalone entry point. Include necessary background information, prerequisites, and explicit references to related concepts."

> [Mintlify](https://www.mintlify.com/blog/structure-documentation-AI-human-readers) — "Long blocks of text can make it harder for AI models to accurately interpret content."

### Three-Segment Pattern

Both audiences benefit from intentional document segments, though for different reasons.

**Beginning (Framing)**
- Human: Must catch attention AND convey core understanding in first screen
- Agent: Establishes interpretive frame via primacy effect; constraints stated early color all subsequent content

**Middle (Retrieval/Depth)**
- Human: For those who want more; structure for scanning
- Agent: Structured for finding specific facts later; consistent patterns enable retrieval

**End (Synthesis/Reinforcement)**
- Human: For skeptics; show mental working
- Agent: Recency effect makes final content most accessible during output generation; checklists effective here

> Repository pattern from `references/audiences/agent.md` — "Beginning frames interpretation; middle enables retrieval; end synthesizes and bounds."

---

## Content Practices

### Terminology Consistency

| Practice | Human Benefit | Agent Benefit |
|----------|---------------|---------------|
| One term per concept | Reduces cognitive load | Eliminates synonym mapping |
| No vague pronouns | Clearer comprehension | Explicit relationships |
| Acronyms spelled out first use | Accessibility | Disambiguation |
| Consistent naming (CamelCase) | Scannable | Fewer tokens, searchable |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Define terms clearly and maintain consistent usage across documentation. Spell out acronyms on first mention."

> Repository pattern — "Same term everywhere for same thing. No synonyms; pick one name and use it. Names work as retrieval keys for search and grep."

### Explicit Over Implicit

State relationships directly rather than requiring inference from context.

| Pattern | Human Impact | Agent Impact |
|---------|--------------|--------------|
| Named relationships | Clearer understanding | Parseable dependencies |
| Stated constraints | Prevents mistakes | Enables verification |
| Explicit negatives | Sets expectations | Prevents misapplication |
| Boundaries stated | Knows when not to apply | Scope limiting |

> Repository pattern — "Name the relationship: depends, triggers, blocks, contains. Avoid ambiguous antecedents: it, this, that. If something is NOT true, say so explicitly."

### Code Examples

| Practice | Rationale |
|----------|-----------|
| Complete, runnable snippets | Incomplete examples force generic AI responses |
| Include imports, file paths | Context for both audiences |
| Fenced code blocks | Distinguishes code from prose for AI parsing |
| Configuration context | Prevents misapplication |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Provide runnable code snippets with imports, file paths, and configuration context. Incomplete examples force LLMs to resort to generic advice."

> [Mintlify](https://www.mintlify.com/blog/structure-documentation-AI-human-readers) — "Always use fenced code blocks to distinguish code from prose, helping AI understand where the code begins and ends."

### Visual Content

| Element | Human Value | Agent Handling |
|---------|-------------|----------------|
| Diagrams | High — visual pattern recognition | Many agents cannot process images |
| Tables | Quick comparison scanning | Structured, parseable |
| Screenshots | Shows exact UI state | Requires text description |
| Flowcharts | Relationship comprehension | Needs text alternative |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Include descriptive text for UI screenshots and interactive flows since many AI assistants cannot process images."

> Repository pattern — "Diagrams add production cost. Use when structure genuinely complex."

---

## Metadata and Discovery

### Frontmatter

YAML frontmatter aids both discovery systems and reader orientation.

```yaml
---
description: One sentence explaining what this document is
tags: [searchable, terms, for, discovery]
audience: { human: 60, agent: 40 }
purpose: { gestalt: 60, reference: 20, ... }
---
```

> [Alation](https://www.alation.com/blog/how-to-write-ai-ready-documentation/) — "Taxonomy tags categorize content by function. Tags like 'Database configuration', 'Error handling' help agents route queries to the right kind of answer."

### llms.txt Standard

A proposed standard for AI-specific documentation discovery. Acts as a sitemap for AI systems.

| File | Purpose |
|------|---------|
| llms.txt | Index file with links and descriptions |
| llms-full.txt | All content in single Markdown file |

> [llmstxt.org](https://llmstxt.org/) — "A proposal to standardize on using an /llms.txt file to provide information to help LLMs use a website at inference time."

> [Mintlify](https://www.mintlify.com/blog/what-is-llms-txt) — "AI agents are actually visiting a site's llms-full.txt over twice as much as llms.txt."

> [Mintlify](https://www.mintlify.com/blog/ai-documentation-trends-whats-changing-in-2025) — "By the end of 2025, any doc site without llms.txt will struggle to surface in AI interfaces."

**Adoption**: Pinecone, Windsurf, LangChain, and hundreds of others have implemented llms.txt.

> [llmstxt.org](https://llmstxt.org/) — "You can browse hundreds of live examples at llmstxt.site and directory.llmstxt.cloud."

### Token Efficiency

AI systems operate within context window constraints. Token-efficient documentation improves retrieval quality and reduces costs.

| Practice | Token Impact |
|----------|--------------|
| Tables over prose for comparisons | 50 tokens vs 200 words |
| No duplication | Eliminates reconciliation burden |
| CamelCase naming | Fewer tokens than hyphenated |
| Progressive disclosure | Loads only what's needed |
| Large tables as linked CSV | Reduces document size |

> Repository pattern — "Duplication wastes tokens. Worse: slightly different duplications create reconciliation burden."

> [Gianfranco Bordoni](https://www.gianfrancobordoni.eu/2025/12/22/the-token-economy-a-strategic-guide-to-the-engine-behind-ai-for-business-and-the-public-sector/) — "Quality degrades when the context is saturated."

---

## Content Quality

### Audit and Maintenance

Outdated or duplicate content degrades AI retrieval accuracy.

| Action | Rationale |
|--------|-----------|
| Remove outdated tutorials | Noise confuses retrieval |
| Consolidate duplicates | Prevents inconsistent answers |
| Archive internal-only docs | Reduces search space |
| Eliminate placeholders | Quality over quantity |

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Too much content creates noise that confuses LLM-based retrieval systems. When documentation includes outdated information, duplicate content, or irrelevant details, LLMs struggle to identify the most accurate and current answers."

### Evidence and Confidence

Both audiences benefit from knowing how trustworthy information is.

| Level | Human Perception | Agent Handling |
|-------|------------------|----------------|
| Sources linked | Feels trustworthy | Can verify claims |
| Confidence marked | Knows weight to assign | Calibrates responses |
| Limitations acknowledged | Builds trust | Prevents overstatement |
| Gaps explicit | Knows what's unknown | Avoids fabrication |

> Repository pattern — "Source type near claim: code, docs, synthesis, expert, intuition. Lower confidence needs explicit marking."

### Anti-Patterns

| Pattern | Problem for Both Audiences |
|---------|---------------------------|
| "Currently" statements | Becomes false, misleads |
| Timestamps without git | Maintenance burden, staleness |
| Buried key facts | Humans miss; agents may not weight appropriately |
| Hidden/collapsed content | AI may not capture interactive elements |
| JavaScript-rendered content | AI crawlers cannot retrieve |

> [Mintlify](https://www.mintlify.com/blog/structure-documentation-AI-human-readers) — "Interactive elements like tabs and collapsibles may not always be reliably captured by AI models."

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "Ensure documentation exists in static HTML source files, not dynamically loaded via JavaScript. AI crawlers can access hidden CSS content but cannot retrieve JavaScript-generated elements."

---

## Technical Integration

### Model Context Protocol (MCP)

Emerging standard for exposing documentation to AI systems programmatically.

> [Mintlify](https://www.mintlify.com/blog/ai-documentation-trends-whats-changing-in-2025) — "MCP is already gaining traction. OpenAI supports MCP across both ChatGPT and its Agents SDK. Docs teams are also starting to expose OpenAPI definitions, Markdown pages, and structured config files as machine-readable context streams."

### Document Parsing Tools

Tools for converting documents to AI-friendly formats:

| Tool | Approach |
|------|----------|
| Microsoft MarkItDown | Format conversion preserving structure |
| IBM Docling | Layout analysis into structured hierarchy |
| LlamaParse | Extracts text, tables, hierarchy into Markdown/JSON |

> [Remio AI](https://www.remio.ai/post/microsoft-markitdown-open-source-tool-for-markdown-conversion-and-ai-document-parsing) — "MarkItDown offers format conversion and semantic parsing. The conversion engine handles common input types and emits Markdown while keeping document elements such as headings, tables, code blocks intact."

### Static Content Requirement

AI crawlers require static HTML. Dynamic content loaded via JavaScript is invisible to most AI systems.

> [Biel.ai](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide) — "AI crawlers can access hidden CSS content but cannot retrieve JavaScript-generated elements loaded after page load."

---

## Repository Patterns

Patterns established in this repository's documentation:

### Primacy and Recency Effects

**Primacy**: Information presented first frames interpretation of everything after. Constraints and scope boundaries belong at the start.

**Recency**: Information presented last is most accessible during output generation. Checklists and key points effective at document end.

> Repository `references/audiences/agent.md` — "Early content establishes the lens through which all subsequent content is processed."

### Progressive Disclosure

Present gestalt first; reveal depth on demand via references. Reduces context load for agents while enabling depth when needed.

> Repository pattern — "Gestalt document contains enough to avoid harmful misuse. Reference documents add depth. Agent can act on gestalt alone without danger."

### Document Chunking

Size documents to be safe alone while enabling progressive disclosure. Each document must be safe to use without reading sister documents.

> Repository pattern — "Bad outcomes from not reading related docs = chunked wrong."

### Capsule Format

Distilled wisdom in consistent structure: Invariant (<=30 tokens), Example, Boundary, Depth. Optimized for agent comprehension and retrieval.

> Repository `references/concepts/capsules.md` — capsule format specification

---

## Comparison: Human-Only vs Dual-Audience

| Dimension | Human-Only Docs | Dual-Audience Docs |
|-----------|-----------------|-------------------|
| Repetition | Reinforces learning | Minimized; references instead |
| Prose density | Flowing narrative | Structured entries, tables |
| Visual content | Heavy diagram use | Diagrams with text alternatives |
| Data tables | Inline, small | Large tables linked as files |
| Terminology | May vary for style | Strictly consistent |
| Hidden content | Collapsibles acceptable | Must be static/visible |
| Metadata | Optional enhancement | Essential for discovery |

---

## Gaps

- **Quantified retrieval impact**: No controlled studies found measuring documentation structure changes on AI retrieval accuracy
- **llms.txt tooling maturity**: Standard is new; tooling ecosystem still developing
- **MCP documentation patterns**: Emerging standard; best practices not yet established
- **Accessibility intersection**: How screen reader requirements align with AI parsing requirements not well documented
- **Multilingual considerations**: Most research focuses on English documentation
- **Version-specific documentation**: How to handle documentation for multiple product versions with AI agents

---

## Summary

| Practice | Human Benefit | Agent Benefit |
|----------|---------------|---------------|
| Semantic heading hierarchy | Scannable navigation | Topic relationship mapping |
| One concept per section | Focused reading | Clean retrieval chunks |
| Complete code examples | Usable immediately | Avoids generic responses |
| Consistent terminology | Reduced cognitive load | No synonym mapping |
| Explicit relationships | Clearer understanding | Parseable dependencies |
| Visual with text alternatives | Multiple learning modes | Accessible content |
| Frontmatter metadata | Quick orientation | Discovery and routing |
| Sources near claims | Trust signals | Verification capability |
| Static content | Fast loading | Crawlable |
| llms.txt/llms-full.txt | (Indirect benefit) | Optimized discovery |

---

## Sources

- [Biel.ai - Optimizing Docs for AI Agents](https://biel.ai/blog/optimizing-docs-for-ai-agents-complete-guide)
- [Mintlify - Structure Documentation for AI and Human Readers](https://www.mintlify.com/blog/structure-documentation-AI-human-readers)
- [Mintlify - AI Documentation Trends 2025](https://www.mintlify.com/blog/ai-documentation-trends-whats-changing-in-2025)
- [Mintlify - What is llms.txt](https://www.mintlify.com/blog/what-is-llms-txt)
- [Alation - How to Write AI-Ready Documentation](https://www.alation.com/blog/how-to-write-ai-ready-documentation/)
- [llmstxt.org - The /llms.txt file](https://llmstxt.org/)
- [Microsoft Learn - Document Intelligence Markdown Elements](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/markdown-elements)
- [Bismart - Markdown: The Best Text Format for Training AI Models](https://blog.bismart.com/en/markdown-ai-training)
- Repository patterns: `/Users/stuartwilson/Documents/Source/pushpay-for-robots/.claude/skills/writing-documents/references/audiences/agent.md`
- Repository patterns: `/Users/stuartwilson/Documents/Source/pushpay-for-robots/.claude/skills/writing-documents/references/audiences/human.md`
- Repository patterns: `/Users/stuartwilson/Documents/Source/pushpay-for-robots/Ethos.md`

---

*The reader decides. This research presents; it does not prescribe.*
