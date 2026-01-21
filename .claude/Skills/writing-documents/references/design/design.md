# Writing Design Documents

Design creates the structure that makes flows real. Its primary job is containing complexity.

## Capsule: ContainComplexity

**Invariant**
When you find complexity: eliminate it, contain it behind a clean interface, or accept and document it. Never let it spread.

**Example**
A complex component with a simple interface is manageable. A simple component that forces complexity onto its callers is a virus.
//BOUNDARY: The worst outcome is complexity that spreads.

**Depth**
- Eliminate: simplify the requirement, remove the feature, change the approach
- Contain: encapsulate behind a clean interface; complexity exists but doesn't leak
- Accept: sometimes inherent; document it, don't hide it

## Before You Draft

**Establish the north star.** What does great look like for this specific system? Use questions to surface what matters, then synthesize into a statement that anchors every decision.

**Check your inputs.** Design builds on research and flows. Before drafting:
- Is there research that informs this design? Reference it.
- Are there gaps in the research? Flag them—it may be better to research first than design on assumptions.
- What flows does this enable? Are they documented?

**Surface what's hidden.** What constraints aren't obvious? What would make this design wrong? What are you assuming?

A design built on assumptions will be relitigated.

## What Design Does

**Enables flows** — Flows describe what happens. Design creates the structure that makes them happen and resolves where they intersect.

**Resolves cross-cutting concerns** — How all the flows come together. Authentication that spans them. Error handling that's consistent. Logging that's coherent. The concerns no single flow owns but all flows need.

**Contains complexity** — The most important job. Identify complexity, then either eliminate it or contain it so it doesn't leak into the rest of the system. Complexity that escapes containment infects everything it touches.

**Commits to technologies** — Database, queue, runtime, deployment. Choices made with eyes open to their constraints and trade-offs.

**Addresses non-functionals** — Failure modes, scalability, deployment topology. The -ilities that flows don't capture but systems require.

**Identifies extension points** — Where flexibility is needed vs. where premature abstraction adds cost. What to leave open, what to close.

## The North Star

Build the simplest possible thing that achieves the goal, avoids problems, and will be simple to maintain and evolve during its life.

Not the cleverest thing. Not the most elegant thing. The simplest thing that works and stays simple.

## Worth Asking

Before writing:
- What flows does this enable?
- Where do flows intersect? What cross-cutting concerns emerge?
- Where is the complexity? Can it be eliminated? If not, how do we contain it?
- What are the failure modes?
- What technologies are we committing to and why?
- What needs to stay open for extension?

After drafting:
- Could this be simpler?
- Where might complexity leak?
- Have we addressed the -ilities (scalability, reliability, maintainability)?
- Are trade-offs explicit?
- Will this be easy to change when we learn more?

## The Right Level

**The test:** A skilled professional should be able to look at it and say "yep, that will work" before any code is written. If they can't evaluate it, it's too abstract. If they're reviewing implementation choices, it's too detailed.

Design is architectural—above the code, below the flow.

| Too abstract | Right level | Too detailed |
|--------------|-------------|--------------|
| "Store data" | "PostgreSQL for transactional data, Redis for session cache" | "Use Npgsql with connection pooling, 30s timeout" |
| "Handle errors" | "Circuit breaker on external calls, retry with backoff" | "Polly with 3 retries, exponential backoff starting at 100ms" |
| "Process async" | "SQS queue with DLQ, Lambda consumer" | "BatchSize 10, VisibilityTimeout 30s" |

External interfaces need detail (APIs, contracts, schemas). Internal implementation stays architectural.

## Structure

```markdown
# [System/Component] Design

## Context
[What problem we're solving, what flows this enables]

## Constraints
[What shapes the decision space—technical, business, organizational]

## Design

### [Aspect 1]
[Decision and rationale]

### [Aspect 2]
[Decision and rationale]

### Cross-Cutting Concerns
[How flows intersect, what's shared]

## Trade-offs
[What we gave up for what we got]

## Alternatives Considered
[What we didn't choose and why—preserves context for future decisions]

## Risks and Mitigations
[What could go wrong, how we've addressed it]
```

## Complexity Containment

When you find complexity, you have three options:

1. **Eliminate** — Can you simplify the requirement? Remove the feature? Change the approach?
2. **Contain** — Encapsulate it behind a clean interface. The complexity exists but doesn't leak.
3. **Accept** — Sometimes complexity is inherent. Document it, don't hide it.

The worst outcome is complexity that spreads. A complex component with a simple interface is manageable. A simple component that forces complexity onto its callers is a virus.

## What to Avoid

| Pattern | Problem |
|---------|---------|
| Decision without rationale | Future you won't know why |
| Missing alternatives | Loses context for future changes |
| Implementation details | That's the plan's job |
| Happy path only | Failure modes matter most |
| Premature abstraction | Flexibility you don't need is cost you pay |
| Clever solutions | Clever is the enemy of simple |

## Living Document

Design evolves as implementation reveals unknowns. When you amend:
- Record what changed and why
- Update trade-offs if they shifted
- Note what you learned

The design remains the canonical source for architectural intent—even as it evolves.

## Checklist

- [ ] A skilled professional could say "yep, that will work"
- [ ] Flows this enables are clear
- [ ] Cross-cutting concerns resolved
- [ ] Complexity contained or eliminated
- [ ] Technologies chosen with rationale
- [ ] Trade-offs explicit
- [ ] Alternatives recorded
- [ ] Failure modes addressed
- [ ] Extension points identified
- [ ] Could not be simpler

## Exemplar

See `exemplar.md` — local semantic search tool design.
