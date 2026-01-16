---
description: Research and domain understanding for the writing-documents skill, documenting patterns learned from exemplary docs
tags: [skill-development, documentation, writing, discovery, meta]
audience: { human: 30, agent: 80 }
purpose: { gestalt: 10, concepts: 15, reference: 25, research: 35, findings: 15, high-agency-process: 0, low-agency-process: 0 }
---

# Discovery: Writing Documents

This skill shapes how future agents think and act. Every document written becomes part of their context, their beliefs, their decisions. Wrong information compounds—one fabricated fact becomes gospel for every agent that reads it. This skill must prevent harm first, enable quality second.

---

## The Two Assessments

Documents vary on two independent axes. Both must be assessed before writing.

### Audience: Who Will Read This?

| Audience | Characteristics |
|----------|-----------------|
| **Human** | Scans, skips, needs motivation. Diagrams valuable. Lower density. |
| **Agent** | Reads everything, trusts task importance. Higher density, structured. Token-efficient. |
| **Both** | Human-readable sections first, agent-focused detail at bottom. |

### Purpose: What Is This Document For?

Distribute 100 points across purposes:

| Purpose | What Reader Wants | Points |
|---------|-------------------|--------|
| **Gestalt** | Understand what something IS and how it fits | ___ |
| **Reference** | Look up specific facts when needed | ___ |
| **Wisdom** | Learn how to THINK about something | ___ |
| **Guide** | Learn how to DO something | ___ |
| **Findings** | What was discovered when investigating X | ___ |
| **Catalog** | Find items and their attributes | ___ |
| **Total** | | **100** |

**Calibration Examples:**

| Document | Gest | Ref | Wis | Guide | Find | Cat | Why |
|----------|------|-----|-----|-------|------|-----|-----|
| gestalt.md | 85 | 10 | 5 | 0 | 0 | 0 | Pure orientation |
| mermaid-diagram-guide.md | 5 | 70 | 5 | 20 | 0 | 0 | Lookup with how-to |
| investigation-thinking.md | 5 | 5 | 80 | 10 | 0 | 0 | Mental models |
| answering-human-questions.md | 0 | 15 | 10 | 75 | 0 | 0 | How to write findings |
| failure-modes.md | 5 | 10 | 10 | 0 | 60 | 15 | Investigation results |
| service-catalog.md | 5 | 10 | 0 | 0 | 0 | 85 | Inventory |

---

## Constraints: The Hard Rules

These prevent harm. Violations compound across every future agent.

### From Ethos: Non-Negotiable

**Rule 1: Only Record What You Can Verify**

Hierarchy of evidence (prefer higher):
1. 📄 Code - Directly observed in source
2. 📚 Documentation - Stated in existing docs
3. 🧠 Synthesis - Derived from multiple verified sources
4. 👤 User - Confirmed by domain expert
5. 💭 Intuition - Inferred from patterns (mark as low confidence)

**Rule 2: When in Doubt, Omit**

The cost calculation:
- **Wrong information** → Incorrect decisions, wasted time, breaking changes
- **Missing information** → Extra research time, asking for clarification

Missing is recoverable. Wrong is destructive.

### Agent Anti-Patterns to Prevent

| Anti-Pattern | Why It's Harmful | Instead |
|--------------|------------------|---------|
| Timestamps ("Last Updated: X") | Git tracks this; becomes misleading | Omit entirely |
| "Currently" statements | Will become false | State the pattern, not the moment |
| "Planned for" promises | Plans change; readers treat as fact | Document what IS, not what will be |
| Quarterly data ("Q2 2025: 6.4%") | Stale immediately | Omit or mark as point-in-time |
| Unsubstantiated assumptions | Compound as future agents take as gospel | Verify or omit |
| Special interest bias | Links everything to current work | Step back; what does a fresh reader need? |
| Estimates and timelines | Always wrong | Omit entirely |
| "Used to be X" annotations | We have git | Just state current state |
| Making new file versions | We have git | Replace the file |
| Speculation about intent | Often wrong, rationalizes | Describe what exists |

### Honest Incompleteness is Good

```markdown
🚧 **STUB** - Limited context, needs domain expert input
🚧 **TODO**: How does org failure manifest?
```

Better to mark gaps than to fabricate. Shows the shape without pretending to have the content.

---

## Purpose Patterns: What I Learned From Exemplars

### Gestalt Pattern (from New Relic README, gestalt.md)

**Goal**: Orient the reader to what something IS and how it fits.

**Structure**:
- Purpose statement upfront
- "Three Concepts That Matter" - distilled mental models
- Decision tables for instant lookup
- Investigation workflow (if applicable)
- Progressive disclosure via links to depth
- Philosophy closing statement

**Key Elements**:
- Immediate utility (account tables, first steps)
- Protection from mistakes (foot-guns section)
- What it's NOT for (boundaries)

**Existing Guidance**: `docs:///guidance/writing-and-documentation/writing-gestalt-documents.md`

---

### Reference Pattern (from mermaid-diagram-guide.md, analyze_golden_metrics.md)

**Goal**: Enable lookup of specific facts when needed.

**Structure**:
- When to Use / When NOT to Use (equally important)
- Decision tree for choosing approaches
- Structured entries with consistent sections:
  - Use for / Don't use for / Example / Best practices / Common mistakes
- Parameters with copy-paste examples
- Response structure (what to expect)
- Foot-guns section (even if empty, the section exists)
- Validation checklist
- Philosophy closing

**Key Elements**:
- Cardinal rules prominently featured
- Accessibility requirements as non-negotiable constraints
- Examples that follow the rules they teach

**Existing Guidance**: mermaid-diagram-guide.md serves as exemplar

---

### Wisdom Pattern (from investigation-thinking.md, writing-capsules.md)

**Goal**: Change how someone thinks, not just what they know.

**Structure**:
- Questions do the work (not explanations)
- Capsule format for mental models:
  - Invariant (≤30 tokens) - the timeless truth
  - Example - binds it to practice
  - Boundary - prevents unsafe extrapolation
  - Depth - clarifies without changing the idea
- Anti-patterns section (what NOT to do)
- Decision tree for complex choices
- Example applying all models together
- One quotable closing line

**Key Elements**:
- Space for thinking (not filled)
- Transfers to contexts not anticipated
- One screen maximum
- "When to Apply" for each model

**Existing Guidance**: `docs:///guidance/writing-and-documentation/writing-capsules.md`

---

### Guide Pattern (from skillWriter references)

**Goal**: Teach how to DO something.

**Structure**:
- Prerequisites - what must be true before starting
- Steps with verification for each
- Recovery paths - what to do when steps fail
- Completion criteria - how to know you're done
- Diagrams only if branching (never for linear sequences)

**Key Elements**:
- The test: What happens if you skip or reorder?
  - Harm occurs → process (strict order)
  - Incompleteness → checklist (any order)
  - Suboptimal outcome → guidance (suggestions)
- Prescribe outcomes, not tools (where possible)

---

### Findings Pattern (from answering-human-questions.md)

**Goal**: Report what was discovered when investigating something.

**Structure**:
```markdown
# Title

**Question**: What was asked

---

**Topic 1**: Complete answer in prose (2-3 paragraphs)

**Topic 2**: More complete answers

---

## Topic 1 Detail

[Diagram — structure/flow]

[Table — details that would clutter the diagram]

[1-2 sentences — the key insight]

> [Source](link) — what it proves
```

**Key Elements**:
- Question stated at top (documents get forwarded)
- Answers front-loaded BEFORE diagrams/evidence
- Diagrams show structure; tables show detail
- Sources as blockquotes near claims (not at end)
- Effect, not just action ("Sets X — stops email")
- No speculation about intent
- No timelines or estimates
- No unsolicited recommendations

**Existing Guidance**: `docs:///guidance/writing-and-documentation/answering-human-questions.md`

---

### Catalog Pattern (from service-catalog.md)

**Goal**: Let readers find items and their attributes.

**Structure**:
- Consistent structure per item
- Attributes as columns (sortable/filterable concept)
- Links to detailed docs
- Grouping by meaningful categories

**Key Elements**:
- Pattern, not exhaustive list (lists rot)
- Include enough to identify; link for detail
- Clear scope (what's included, what's not)

---

## What Makes Great Documentation (Synthesized)

From analyzing mermaid-diagram-guide.md, New Relic suite, and other exemplars:

1. **Immediate utility** - Reader can start using it right away
2. **Protection from mistakes** - Foot-guns, cardinal rules, "Don't use for"
3. **Decision frameworks** - Tables and trees for choosing approaches
4. **Mental models** - How to think, not just what to do
5. **Progressive disclosure** - Gestalt here, depth linked
6. **Self-demonstrating** - Document follows its own advice
7. **Validation** - Checklists for completion
8. **Philosophy** - Clear statement of values that generated the rules

---

## Audience Considerations

### For Human Readers

| Aspect | Approach |
|--------|----------|
| Density | Lower - they scan and skip |
| Motivation | "Why care?" upfront |
| Structure | Scannable headers, prose paragraphs |
| Diagrams | Very useful - visual learners |
| Examples | Illustrative, motivational |
| Data | Inline tables (small) |
| Links | "Learn more" optional |
| Repetition | Reinforces learning |

### For Agent Readers

| Aspect | Approach |
|--------|----------|
| Density | Higher - reads everything |
| Motivation | Trusts task importance |
| Structure | Tables, capsules, structured entries |
| Diagrams | Moderate - add tokens, may not render |
| Examples | Pattern templates, copy-paste ready |
| Data | CSV files for large lookups |
| Links | "See X" essential for progressive disclosure |
| Repetition | Wastes tokens |

### For Both

- Human-readable summary/introduction first
- Agent-focused detail and structure at bottom
- Diagrams with text alternatives
- Tables that work for both scanning and lookup

---

## Relationship to Existing Guidance

This skill activates existing guidance, doesn't replace it.

| Purpose | Existing Guidance | Role of This Skill |
|---------|-------------------|-------------------|
| Gestalt | writing-gestalt-documents.md | Introduce, link |
| Reference | mermaid-diagram-guide.md (exemplar) | Introduce pattern, link |
| Wisdom | writing-capsules.md | Introduce, link (MUST read in full) |
| Guide | (patterns in skillWriter) | Introduce pattern |
| Findings | answering-human-questions.md | Introduce, link |
| Catalog | (no dedicated guidance) | Define pattern |

**Critical**: Capsules cannot be done properly without reading the full guidance. The skill must force engagement with the source, not summarize poorly.

---

## Zone Assessment for This Skill

| Zone | Points | Rationale |
|------|--------|-----------|
| Constraint | 40 | Hard rules ARE the skill - violations cause cascading harm |
| Knowledge | 30 | Document types, patterns, audience differences, evidence hierarchy |
| Wisdom | 25 | When to write vs omit, cost calculation, temporally stable thinking |
| Process | 5 | "Rewrite as whole" at end, validation checklist |

**Key insight**: Constraints dominate because documentation failures compound. One wrong fact becomes gospel for future agents. Prevent harm first, enable quality second.

---

## Process: The Critical Final Step

**"Rewrite as cohesive whole"** at the end is very high value.

Many edits seldom result in a document that flows well and says what it means to. After completing a document:

1. Read existing content
2. Think about what you've written
3. Rewrite as a cohesive whole (replacement, not edits)

This produces documents that flow and communicate clearly.

---

## Skill Structure

```
writing-documents/
├── SKILL.md                    # Two assessments, constraints, decision criteria
├── meta/
│   ├── DISCOVERY.md            # This file
│   └── CREATION_NOTES.md       # Feedback on authoring experience
└── references/
    ├── constraints.md          # Hard rules with examples (not well covered elsewhere)
    ├── human-audience.md       # Audience-specific patterns
    └── agent-audience.md       # Audience-specific patterns
```

Purpose-specific guidance links to existing docs rather than duplicating.

---

## Success Criteria

**An agent using this skill should:**
- Assess audience and purpose before writing
- Know which existing guidance to read for their purpose type
- Avoid all anti-patterns (timestamps, assumptions, special interest bias)
- Produce documents that are discoverable (good headlines, frontmatter)
- Include only verified, high-value information
- End with rewrite-as-whole for cohesion

**The resulting documentation should:**
- Be findable in tools (RepoQL, search)
- Be trustworthy (verified, sourced)
- Be maintainable (reasoning included, low-value omitted)
- Serve its stated purpose
- Not mislead future agents

---

*Wrong information is worse than missing information. Missing is recoverable. Wrong is destructive.*
