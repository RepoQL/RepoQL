---
description: How to write documentation optimized for agent readers
tags: [audience, agent, documentation, writing, structure, tokens]
audience: { human: 5, agent: 100 }
purpose: { gestalt: 5, concepts: 40, reference: 35, research: 0, findings: 0, flow: 0, plan: 0, design: 0, high-agency-process: 20, low-agency-process: 0 }
---

# Writing for Agents

Agents read everything. The challenge is not attention but precision, retrieval, and context efficiency.

## Capsule: StructureOverProse

**Invariant**
Structured formats parse faster and more reliably than equivalent prose.

**Example**
Comparison as prose: 200 words, ambiguous relationships. Same comparison as table: 50 tokens, relationships explicit.
//BOUNDARY: Structure for data; prose for complex reasoning that resists tabulation.

**Depth**
- Tables for comparisons, attributes, decisions
- Capsules for concepts
- Bullets for unordered items
- Prose only when relationships are too complex for structure

## Capsule: ExplicitOverImplicit

**Invariant**
State relationships directly; do not require inference from context.

**Example**
Bad: Describes A, then B, expects reader to see dependency.
Good: A depends on B for X.
//BOUNDARY: Explicit is not verbose. State the relationship, not everything about it.

**Depth**
- Name the relationship: depends, triggers, blocks, contains
- Avoid ambiguous antecedents: it, this, that
- State constraints directly, not by implication
- If something is NOT true, say so explicitly

## Capsule: ReferenceOverRepetition

**Invariant**
Point to information once defined; never duplicate within context.

**Example**
Concept explained in section 2. Section 5 needs it. Say "see ConceptName" not re-explain.
//BOUNDARY: Reference assumes reader can retrieve. In single-file context, section references work.

**Depth**
- Duplication wastes tokens
- Worse: slightly different duplications create reconciliation burden
- Canonical location for each concept
- SeeAlso links for cross-document references

## Capsule: PreciseNaming

**Invariant**
Use consistent, searchable names that tokenize efficiently.

**Example**
Good: CircuitBreaker, RetryPolicy, IdempotencyKey
Bad: circuit-breaker, retry_policy, the idempotency mechanism
//BOUNDARY: Precision is not jargon. Names should be self-descriptive.

**Depth**
- CamelCase for concept names (fewer tokens)
- Same term everywhere for same thing
- No synonyms; pick one name and use it
- Names work as retrieval keys for search and grep

## Capsule: EvidenceHierarchy

**Invariant**
Mark confidence levels so readers can weight information appropriately.

**Example**
Code observation: high confidence. Synthesis from multiple docs: medium. Intuition from patterns: low, marked as such.
//BOUNDARY: Unmarked information is assumed verified. Mark uncertainty, not certainty.

**Depth**
- Source type near claim: code, docs, synthesis, expert, intuition
- Lower confidence needs explicit marking
- Enables appropriate trust calibration
- Readers can verify high-stakes claims, accept low-stakes ones

## Capsule: BoundariesExplicit

**Invariant**
State what something is NOT and when NOT to use it.

**Example**
Every reference entry: Use for X. Don't use for Y. Both equally important.
//BOUNDARY: Boundaries prevent misapplication, not just inform correct use.

**Depth**
- What is excluded from scope
- When this guidance does not apply
- Edge cases that break the pattern
- Adjacent concepts that might be confused

## Capsule: ProgressiveDisclosure

**Invariant**
Present gestalt first; reveal depth on demand via references.

**Example**
SKILL.md gives overview and orientation. References contain detailed guidance. Agent reads gestalt, then only the references needed for current task.
//BOUNDARY: Disclosure path must be clear. Gestalt without pointers to depth is incomplete.

**Depth**
- Reduces context load; agent loads only what's needed
- Gestalt enables navigation; references enable execution
- Links as selective loading mechanism
- SeeAlso: ProductiveAbsence in skillWriter

## Capsule: ProductiveUncertainty

**Invariant**
Mark low-confidence information as a cue for verification, not a reason for exclusion.

**Example**
Synthesis from patterns marked with confidence level. Agent knows to verify before high-stakes use, can accept for low-stakes exploration.
//BOUNDARY: Unmarked is assumed high confidence. Mark uncertainty, not certainty.

**Depth**
- Uncertainty markers guide investigation
- Agent calibrates effort to stakes
- Better than omitting: shows shape of knowledge gaps
- Evidence hierarchy: code > docs > synthesis > expert > intuition

## Capsule: DocumentChunking

**Invariant**
Size documents to be safe alone while enabling progressive disclosure to related depth.

**Example**
Gestalt document contains enough to avoid harmful misuse. Reference documents add depth. Agent can act on gestalt alone without danger; reads references for precision.
//BOUNDARY: Too small forces reading multiple docs for basic safety. Too large defeats progressive disclosure.

**Depth**
- Each document must be safe to use without reading sister documents
- Bad outcomes from not reading related docs = chunked wrong
- Size for the task: gestalt for orientation, reference for execution
- Links enable depth; document must not require them for safe use

## Capsule: LinkedData

**Invariant**
Large reference tables belong in CSV files that agents can query with tools.

**Example**
Service inventory: 50+ services with attributes. Embed in markdown: huge, hard to query. Link to CSV: agent uses tools to filter, sort, aggregate.
//BOUNDARY: Small tables (under 20 rows) can embed. Larger data links out.

**Depth**
- CSV enables tool-based querying
- Reduces document size and context load
- Keeps markdown readable for humans who don't need full data
- Agent capability: can process linked files on demand

## Capsule: ThreeSegmentsAgent

**Invariant**
Beginning frames interpretation; middle enables retrieval; end synthesizes and bounds.

**Example**
Beginning: what is this, key constraints. Middle: structured content. End: how pieces connect, what's not covered, checklist.
//BOUNDARY: Agents read everything. End carries weight humans miss.

**Depth**

**Beginning (Frame)**
- What is this document, what problem it solves
- Key constraints that shape interpretation of everything after
- Scope boundaries upfront
- Sets expectations for middle

**Middle (Retrieval)**
- Structured for finding specific facts later
- Consistent patterns: tables, capsules, entries
- Headers that work as search keys
- Cross-references to related content

**End (Synthesis)**
- How pieces fit together
- Meta-observations about the content
- Checklists for verification
- What is NOT covered (explicit boundaries)
- Agents read this; it can carry important content

## What Hinders Agents

| Pattern | Problem |
|---------|---------|
| Ambiguity | Multiple interpretations require guessing |
| Inconsistent terminology | Forces tracking of synonym mappings |
| Buried key facts | Important information hidden in prose |
| Redundancy | Wastes context; inconsistent copies worse |
| Missing context | Unstated assumptions cause misapplication |
| Temporal markers | "Currently" "now" "recently" become stale |
| Decorative formatting | Emojis, elaborate ASCII add tokens not meaning |

## Checklist

- [ ] Structure used where data permits: tables, capsules, bullets
- [ ] Relationships explicit: named, not implied
- [ ] No duplication; references to canonical locations
- [ ] Consistent precise names throughout
- [ ] Confidence levels marked for synthesis and intuition
- [ ] Boundaries stated: what it is NOT, when NOT to use
- [ ] Progressive disclosure: gestalt with clear paths to depth
- [ ] Information chunked: self-contained, independently processable
- [ ] Large tables linked as CSV, not embedded
- [ ] End section synthesizes and bounds
