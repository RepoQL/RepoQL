---
name: skillWriter
description: This skill guides intentional skill design. Use when creating, improving, or reviewing Claude Code skills. Requires a zone assessment to clarify what kind of skill is being built before writing content.
---

# skillWriter

A skill encodes what cannot be derived from first principles. Before writing, understand what you're encoding.

## Track Progress

Use your todo tool to track these checkpoints:
- [ ] Discovery sufficient to attempt zone assessment
- [ ] Zone assessment completed (100 points distributed)
- [ ] `writing-documents` skill invoked before writing each document
- [ ] Self-check passed
- [ ] Testing completed

---

## Skill Structure

```
skill-name/
├── SKILL.md           # Gestalt and cross-cutting concepts
├── meta/
│   ├── DISCOVERY.md       # Research notes, domain understanding
│   └── CREATION_NOTES.md  # Feedback on authoring experience
└── references/
    └── ...            # Detailed guidance by topic
```

---

## Capsule: Discovery

**Invariant**
You cannot write an effective skill without understanding the subject in excruciating detail first.

**Example**
Before writing a mermaid skill: read all mermaid documentation, create dozens of diagrams, discover what breaks, learn which features are experimental, understand why certain patterns fail.
//BOUNDARY: Zone assessment comes after discovery, not before. You can't assess what you don't understand.

**Depth**
- Sources: the user (ask questions), the internet (research), existing code (what actually works), your intuition (you have good instincts)
- Document findings in `meta/DISCOVERY.md`—this is your working memory
- Identify what leads to great outcomes vs what causes failure
- Only then can you assess which zones matter and what to encode
- Discovery documents are messy and complete; skills are clean and selective

---

## The Four Zones

Skills contain different kinds of content. Most skills blend these, but the blend matters.

### Capsule: Knowledge

**Invariant**
Inject context that cannot be derived from first principles.

**Example**
API schemas defining valid request shapes. Company naming conventions. Business rules for edge cases.
//BOUNDARY: If Claude could figure it out by reasoning, it belongs elsewhere.

**Depth**
- Distinction: Knowledge is facts Claude lacks; explanation is facts Claude has but needs prompted.
- NotThis: Generic best practices Claude already knows.
- SeeAlso: `references/knowledge.md`

### Capsule: Process

**Invariant**
Coordinate sequences where order matters and steps cannot be safely skipped.

**Example**
Deployment: build → test → stage → approve → deploy. Skipping "test" or reordering causes harm.
//BOUNDARY: If steps can be reordered freely, use a checklist instead.

**Depth**
- Distinction: Process enforces order; checklists verify completion.
- Trade-off: More structure reduces errors but limits flexibility.
- SeeAlso: `references/process.md`

### Capsule: Constraint

**Invariant**
Enforce rules where deviation is the failure mode.

**Example**
Security policy: Never log credentials. Never commit secrets. Always validate external input.
//BOUNDARY: If deviation is acceptable in some contexts, it's guidance.

**Depth**
- Distinction: Constraints are non-negotiable; guidance permits judgment.
- Trade-off: Stricter constraints reduce risk but may block valid approaches.
- SeeAlso: `references/constraint.md`

### Capsule: Wisdom

**Invariant**
Introduce ways of thinking that generalize across contexts.

**Example**
"Write for intelligent agents who need structure, not scripts." This reshapes approach to documentation, skills, communication—everything.
//BOUNDARY: If it only applies to one domain, it's knowledge.

**Depth**
- Distinction: Wisdom shapes thinking; knowledge provides facts.
- NotThis: Templates, procedures, or domain-specific rules.
- SeeAlso: `references/wisdom.md`

---

## Zone Assessment

**After discovery, before writing**, distribute 100 points across the zones.

This is not optional. The distribution clarifies what you're building and guides every subsequent decision.

| Zone | Points | What You're Encoding |
|------|--------|---------------------|
| Knowledge | ___ | Facts and context Claude lacks |
| Process | ___ | Sequences where order matters |
| Constraint | ___ | Rules that must be followed |
| Wisdom | ___ | Ways of thinking |
| **Total** | **100** | |

### Calibration Examples

| Skill | K | P | C | W | Why |
|-------|---|---|---|---|-----|
| API integration | 70 | 15 | 10 | 5 | Mostly injecting facts Claude doesn't have |
| Deployment workflow | 25 | 50 | 20 | 5 | Sequence matters, some rules |
| Security review | 15 | 20 | 55 | 10 | Constraints dominate |
| Writing for agency | 5 | 5 | 5 | 85 | Pure philosophy |
| Code review | 30 | 15 | 25 | 30 | Balanced blend |

---

## Interpreting Your Distribution

### Primary Zone

Where did most points go? That zone's reference file is your primary guide.

| Highest Zone | Primary Reference |
|--------------|-------------------|
| Knowledge | `references/knowledge.md` |
| Process | `references/process.md` |
| Constraint | `references/constraint.md` |
| Wisdom | `references/wisdom.md` |

### Agency Level

Your distribution suggests how much to trust the agent versus prescribe behavior.

| Pattern | Agency Implication |
|---------|-------------------|
| High Knowledge, low Constraint | **High agency** — inject context, trust application |
| High Process | **Medium agency** — flexibility within structure |
| High Constraint | **Variable** — rules are firm, but application may flex |
| High Wisdom | **High agency** — ideas reshape thinking, not behavior |

Note: High constraint doesn't automatically mean low agency. "These outcomes must be achieved" (high agency) differs from "Follow these exact steps" (low agency). Both enforce constraints.

### Content Style

| Zone | Content Feels Like |
|------|-------------------|
| Knowledge | Reference material, structured for retrieval |
| Process | Sequenced steps, checkpoints, validation |
| Constraint | Rules simply stated, boundaries explicit |
| Wisdom | Ideas introduced, not templates provided |

---

## Before Writing

**Invoke the `writing-documents` skill now.** Skills are documents. Their effectiveness depends on writing quality. Know how to write well before writing skill files.

With your distribution clear, ask:

**On purpose:**
- What would Claude get wrong without this skill?
- What cannot be derived from first principles?

**On scope:**
- What does this skill NOT cover?
- Where should Claude go for adjacent needs?
- Is this skill focused at the right level? Are we likely to have many variations of it?

**On form:**

- Does the content style match your primary zone?
- Are you prescribing where you should trust?
- Are you trusting where you should constrain?

---

## Reference Files

Consult based on your distribution:

- **`references/knowledge.md`** — Structuring facts for retrieval, when high agency fits
- **`references/process.md`** — When sequences matter, balancing flexibility with structure
- **`references/constraint.md`** — Rules without justification, when prescription is appropriate
- **`references/wisdom.md`** — Introducing ideas that change thinking, not templates

Skills with balanced distributions: consult multiple references, weight by allocation.

### Cross-Cutting

- **`references/scripts.md`** — When to use scripts, best practices, platform considerations. Scripts serve Knowledge (fetch current truth), Process (automate steps), and Constraint (verify compliance).

---

## Self-Check

Before shipping, verify:

- [ ] Zone assessment completed and recorded in skill.md yaml front matter
- [ ] Content style matches primary zone
- [ ] Agency level appropriate for the blend
- [ ] Boundaries explicit where non-obvious
- [ ] Description triggers for intended queries
- [ ] Every section earns its token cost
- [ ] Tested with a subagent

## Testing

Launch a subagent to use the skill for real work:

1. **Subagent works** — uses the skill for its intended purpose
2. **Subagent self-checks** — verifies output follows the skill's rules
3. **Human reviews** — qualitatively assesses the result
4. **Subagent reports** — documents experience via FeedbackLoop

The subagent catches rule violations (linear sequences in a mermaid diagram, missing error handling in a process). The human catches qualitative issues (is this actually useful? does it feel right?). The feedback captures what the skill got wrong.

---

## Capsule: PolymorphicSkill

**Invariant**
A polymorphic skill presents shared concepts upfront, then branches to specific variations via progressive disclosure.

**Example**
This skill: SKILL.md defines four zones and assessment. Each zone has a reference file with specific guidance. You learn the shared model first, then dive into the variant you need.
//BOUNDARY: If variations share nothing, they're separate skills, not a polymorphic one.

**Depth**
- Shared concepts reduce duplication and establish common vocabulary
- Progressive disclosure keeps the gestalt readable
- Variations inherit context from the shared layer
- The agent reads only the branch they need, not all branches
- Zone inheritance: references inherit the parent's zones as a starting point; adjust per reference based on what needs to be presented
- SeeAlso: ProductiveAbsence (forces reading the right branch)

---

## Capsule: ProductiveAbsence

**Invariant**
Omit enough that the reader cannot succeed without engaging the referenced material.

**Example**
The zone capsules above tell you what each zone IS, not how to write one well. You cannot write a good wisdom skill from this page alone—you must read `references/wisdom.md`.
//BOUNDARY: Absence is productive only when the path forward is clear. Missing information with no pointer is just incomplete.

**Depth**
- Distinction: Productive absence drives engagement; incompleteness causes failure.
- The gap is a forcing function. Enough to orient, not enough to fake competence.
- This document applies the principle to itself.

---

## Capsule: FeedbackLoop

**Invariant**
Each skill includes `meta/CREATION_NOTES.md` documenting the authoring experience, rewritten by each maintainer.

**Example**
```
1. Create or update the skill using skillWriter
2. Read existing meta/CREATION_NOTES.md (if exists)
3. Rewrite with your experience synthesized (replacement, not append)
```
//BOUNDARY: Notes are about creating/maintaining the skill, not using it. Read only when rewriting.

**Depth**
- Work first, read notes only when rewriting—fresh perspective during creation
- Rewrite entirely—synthesize prior experience with yours
- Focus on: what helped, what confused, what's missing, what would improve skillWriter
- Aggregating CREATION_NOTES.md across skills periodically improves skillWriter

---

*You can't teach what you don't understand.*
