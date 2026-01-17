---
name: writing-documents
description: Guides effective documentation creation. Use when writing new documents, improving existing docs, or reviewing documentation quality. Activates existing guidance based on document purpose. Prevents cascading harm from wrong information.
tags: [documentation, writing, skill, constraints, quality]
zones: { constraint: 40, knowledge: 30, wisdom: 25, process: 5 }
---

# Writing Documents

Every document written becomes context for future agents. Wrong information compounds—one fabricated fact becomes gospel. Prevent harm first, enable quality second.

---

## Before Writing: Two Assessments

Both must be completed and recorded in YAML frontmatter before writing begins.

### Document Frontmatter

All documents require:

```yaml
---
description: One sentence explaining what this document is and why it exists
tags: [searchable, terms, for, discovery]
audience: { human: 70, agent: 40 }
purpose: { gestalt: 60, concepts: 0, reference: 20, research: 0, findings: 0, flow: 0, plan: 0, design: 0, high-agency-process: 15, low-agency-process: 5 }
---
```

### Assessment 1: Audience

Score each independently (0-100). Be honest - some files will never be read by humans.

| Audience | Score |
|----------|-------|
| human | 0-100 |
| agent | 0-100 |

SeeAlso: `references/human.md`, `references/agent.md`

### Assessment 2: Purpose

Distribute 100 points:

| Purpose | Points | What Reader Wants |
|---------|--------|-------------------|
| gestalt | ___ | Condensed understanding of how something works and relates to wider context. High understanding, low detail. |
| concepts | ___ | Experience crystallized into wisdom. Things that fit nicely into capsules. Mental models. |
| reference | ___ | Store of knowledge or facts. Makes finding or understanding things easier. |
| research | ___ | Information to make well-informed decisions later. Pure knowledge transfer, NO conclusions or next steps. |
| findings | ___ | Answer a question effectively and comprehensively. |
| flow | ___ | High-level steps of how a thing happens. Understanding what needs to occur to achieve a goal. |
| plan | ___ | What will be done and in what order. Alignment on approach before execution. |
| design | ___ | How something should be built. Architectural decisions, structure, trade-offs. |
| high-agency-process | ___ | Generalizable guidance that leaves space for judgment calls. |
| low-agency-process | ___ | Formalized steps to effectively achieve a specific goal. |
| **Total** | **100** | |

---

## Constraints: Hard Rules

These prevent harm. Violations compound across every future agent.

### Rule 1: Only Record What You Can Verify

Evidence hierarchy (prefer higher):
1. 📄 Code — Directly observed in source
2. 📚 Docs — Stated in existing documentation
3. 🧠 Synthesis — Derived from multiple verified sources
4. 👤 User — Confirmed by domain expert
5. 💭 Intuition — Inferred from patterns (mark as low confidence)

### Rule 2: When in Doubt, Omit

- **Wrong information** → Incorrect decisions, breaking changes
- **Missing information** → Extra research time, clarification

Missing is recoverable. Wrong is destructive.

### Anti-Patterns

| Pattern | Problem | Instead |
|---------|---------|---------|
| Timestamps | Git tracks this; becomes misleading | Omit |
| "Currently" | Will become false | State the pattern |
| "Planned for" | Plans change | Document what IS |
| Quarterly data | Stale immediately | Omit or mark point-in-time |
| Unverified claims | Compound as gospel | Verify or omit |
| Speculation about intent | Often wrong | Describe what exists |
| Estimates | Always wrong | Omit |

### Honest Incompleteness

```markdown
🚧 **STUB** - Limited context, needs domain expert input
```

Better to mark gaps than to fabricate.

---

## Purpose Capsules

### Capsule: GestaltDoc

**Invariant**
Condense understanding of how something works and fits into wider context.

**Example**
Service gestalt explains what it does, key patterns, dependencies, and pointers to depth.
//BOUNDARY: High understanding, low detail. Not a reference or how-to.

**Depth**
- Purpose upfront, three concepts that matter
- Decision tables, progressive disclosure via links
- SeeAlso: `writing-gestalt-documents.md`

### Capsule: ConceptDoc

**Invariant**
Crystallize experience into transferable mental models using capsule format.

**Example**
Wrong information is worse than missing information. Captures truth that reshapes behavior across contexts.
//BOUNDARY: Must use capsule format. Cannot be done without reading full guidance.

**Depth**
- Invariant ≤30 tokens, Example, Boundary, Depth
- Questions do the work; one screen maximum
- SeeAlso: `writing-capsules.md` (MUST read in full)

### Capsule: ReferenceDoc

**Invariant**
Store knowledge for lookup that would otherwise require extensive reading.

**Example**
Mermaid guide with When to Use, When NOT to Use, decision trees, copy-paste examples.
//BOUNDARY: Structured entries, not prose. Not for understanding; use gestalt.

**Depth**
- When to Use and When NOT to Use equally important
- Consistent entry structure throughout
- Exemplar: `mermaid-diagram-guide.md`

### Capsule: ResearchDoc

**Invariant**
Transfer knowledge for future decisions without conclusions or recommendations.

**Example**
Vendor comparison listing features, pricing, limitations. Reader synthesizes; document stays neutral.
//BOUNDARY: No recommendations, no next steps, no opinion. Sources essential.

**Depth**
- Enables well-informed decisions later
- Let the reader do the thinking

### Capsule: FindingsDoc

**Invariant**
Answer a specific question comprehensively with grounded evidence.

**Example**
Question at top, answer front-loaded, sources as blockquotes near claims they support.
//BOUNDARY: No speculation, no timelines, no unsolicited recommendations.

**Depth**
- Answers BEFORE diagrams and evidence
- Effect, not just action: Sets X which stops email
- SeeAlso: `references/findings.md`

### Capsule: FlowDoc

**Invariant**
Describe high-level steps of how a thing happens for understanding, not instruction.

**Example**
SMS flow: User triggers, Queue, Rate limit, Provider, Delivery receipt, Status update.
//BOUNDARY: Descriptive, not prescriptive. Use before design to find cross-cutting concerns.

**Depth**
- Sequence diagrams, flowcharts valuable
- Identifies stages, actors, handoffs

### Capsule: PlanDoc

**Invariant**
Specify what will be done and in what order for alignment before execution.

**Example**
Phase 1 Core messaging, Phase 2 Templates, Phase 3 Analytics. Goals, dependencies, milestones.
//BOUNDARY: Actionable and specific. Granular but above code level. Evolves with work.

**Depth**
- Used after flow and design are understood
- Makes architectural decisions actionable

### Capsule: DesignDoc

**Invariant**
Document how something should be built with architectural decisions and rationale.

**Example**
SMS Service: Queue-based with provider abstraction. Trade-off noted: complexity vs vendor lock-in.
//BOUNDARY: Problem, constraints, options, choice, trade-offs. Record of decisions.

**Depth**
- Shaped by concepts, flows, research
- Reviewed against high-agency process guidance

### Capsule: HighAgencyProcess

**Invariant**
Provide generalizable guidance that leaves space for judgment calls.

**Example**
Ensure the user understands the impact before proceeding. Outcome prescribed, method trusted.
//BOUNDARY: Prescribe outcomes, not steps. Achieve X not Do A then B then C.

**Depth**
- Trust the agent to find the path
- Used to guide research, review designs, shape plans

### Capsule: LowAgencyProcess

**Invariant**
Formalize steps where order matters and skipping causes harm.

**Example**
Deployment: build, test, stage, approve, deploy. Skipping test breaks production.
//BOUNDARY: Use only when skip or reorder causes harm. Otherwise use high-agency.

**Depth**
- Prerequisites, steps with verification, recovery paths, completion criteria
- Test: Skip or reorder causes harm? Low-agency. Suboptimal? High-agency.

---

## How Purposes Work Together

Documents form an ecosystem. Example: building an SMS service.

```
┌─────────────────────────────────────────────────────────────────┐
│                        UNDERSTANDING                            │
├─────────────────────────────────────────────────────────────────┤
│  high-agency-process ──→ informs HOW to do research             │
│           │                                                     │
│           ↓                                                     │
│       research ──────────→ captured findings, vendor analysis   │
│           │                                                     │
│           ↓                                                     │
│        flow ─────────────→ how SMS sending conceptually happens │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                          SHAPING                                │
├─────────────────────────────────────────────────────────────────┤
│  concepts ───────────────→ encode design goals and ethos        │
│      +                                                          │
│   flow + research ───────→ shape design documents               │
│      +                                                          │
│  high-agency-process ────→ review designs                       │
│           │                                                     │
│           ↓                                                     │
│       design ────────────→ architectural decisions, trade-offs  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                         EXECUTING                               │
├─────────────────────────────────────────────────────────────────┤
│  concepts + flows + designs + high-agency-process               │
│           │                                                     │
│           ↓                                                     │
│        plan ─────────────→ granular specification (above code)  │
│           │                                                     │
│           ↓                                                     │
│  low-agency-process ─────→ implement specific plan elements     │
└─────────────────────────────────────────────────────────────────┘

     findings ─────────────→ answer ad-hoc questions throughout
     gestalt ──────────────→ orient newcomers at any stage
     reference ────────────→ lookup facts when needed
```

**Key insight**: Documents support each other. A design doc without flow analysis misses cross-cutting concerns. A plan without design lacks architectural grounding. Research without process guidance produces inconsistent results.

---

## Final Step: Rewrite as Whole

After completing a document:

1. Read what you've written
2. Think about coherence
3. Rewrite as replacement (not edits)

Many edits seldom result in documents that flow. This produces documents that communicate clearly.

---

## Checklist (non-negotiables)

- [ ] Frontmatter complete: description, tags, audience, purpose
- [ ] Audience and purpose scores recorded before writing
- [ ] Only verified information included; when in doubt, omit
- [ ] No timestamps, no currently statements, no planned-for promises
- [ ] No speculation about intent; describe what exists
- [ ] Purpose capsule guidance followed for dominant purpose
- [ ] Rewritten as cohesive whole at end
- [ ] Document findable, trustworthy, maintainable

---

*Wrong information is worse than missing information.*
