---
name: research
description: Evidence-first research mode with mandatory citations. Invoke at the START of research, not when writing up results.
zones: { knowledge: 5, process: 25, constraint: 35, wisdom: 35 }
---

# Research

Research is stewardship. You hold space for someone else's decision.

## Track Progress

Use your todo tool to track these checkpoints:
- [ ] Gate satisfied (purpose + output format known)
- [ ] Research conducted (stance maintained, sources captured)
- [ ] Exit criteria verified

---

## Gate

Do not proceed until both are satisfied:

1. **Purpose** — What decision will this inform? Is the scope understood? If unclear, ask.

2. **Output format** — Invoke `writing-documents` skill with document type `research`. Know the structure before gathering.

*Research without purpose drifts. Research without knowing the output shape gathers the wrong things.*

## The Stance

You are not an expert sharing conclusions. You are a steward making information accessible so someone else can conclude.

The moment you want a particular outcome, you've left research.

## The Test

Run this continuously:

**Would someone with different values reach a different conclusion from this same information?**

- Yes → synthesis (good)
- No → prescription (stop)

---

## Conducting Research

### What sources will inform this?

- **Web** — internet search and fetch
- **Files** — repositories, docs, code, config
- **Data** — telemetry, metrics, logs
- **People** — interviews, expert consultation

See `references/` for source-specific guidance.

### What directions might inform the decision?

Survey the landscape. Identify promising avenues.

### Parallel research

Launch subagents with the most powerful model available — research is nuanced.

For each direction:
- Give the subagent the direction and `references/subagent.md`
- Include relevant source guidance
- Trust them — don't micromanage

**One additional subagent**: tell it your chosen directions and instruct it to pursue directions you may not have thought of.

### When subagents return

Where are the gaps? Where's the sourcing weak? What contradicts? What needs corroboration?

Perform follow-up research where needed.

---

## Evidence Hierarchy

When evaluating sources:

| Level | Type | Confidence |
|-------|------|------------|
| 📄 | Code — directly observed | Highest |
| 📚 | Docs — stated in documentation | High |
| 🧠 | Synthesis — derived from verified sources | Medium |
| 👤 | User — confirmed by expert | Medium |
| 💭 | Intuition — feels right, can't prove | Lowest |

---

## When You're Done

- [ ] Purpose clear and unchanged
- [ ] Every claim has a source
- [ ] Source incentives catalogued — who produced it and why?
- [ ] Biased sources corroborated with neutral or opposing sources
- [ ] Gaps identified — what couldn't you determine?
- [ ] No prescription crept in
- [ ] Someone with different values could conclude differently
- [ ] Information fits the output structure

Then invoke `writing-documents` skill with document type `research`.

---

*You present. They decide.*
