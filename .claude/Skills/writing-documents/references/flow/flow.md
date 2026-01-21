# Writing Flow Documents

A flow is a "how about this?" document—a proposal backed by research, real enough to discuss with peers and domain experts. It's what you bring to the whiteboard.

## Core Objective

Clarity of thought. Make a desirable process concrete enough that readers can mentally simulate it, spot gaps, and critique it. Too vague and there's nothing to discuss. Too detailed and you've made decisions that should be collaborative.

## Capsule: RightLevel

**Invariant**
External boundaries need concrete detail to prove viability; internal implementation stays abstract for design.

**Example**
Right: "Stripe PaymentIntent API, webhook for payment_intent.succeeded"
Wrong: "Notify payment system" (too vague) or "HttpClient with Polly retry" (too detailed)
//BOUNDARY: If the detail establishes viability, include it. If it's implementation preference, leave it for design.

**Depth**
- Concrete external boundaries prove it can be built, not hand-waved
- Abstract internals leave room for design decisions
- Test: Does this detail establish viability, or is it implementation preference?

## The Audience

Flow documents usually serve blended audiences—the people in the room when you discuss it.

| Audience | Needs to see | Will critique |
|----------|--------------|---------------|
| Domain expert | Business process in their language | "That's not how refunds work" |
| Engineer | Technical viability, integration points | "That API doesn't support webhooks" |
| Architect | System boundaries, failure modes | "That creates coupling between X and Y" |
| Agent | Structured stages, explicit relationships | Gaps in logic, unnamed actors |

Lead with business process so domain experts can follow. Include technical detail where it proves viability. Make failure paths visible for architects and engineers. Structure data for agents.

## The Right Level

**External boundaries need concrete detail** to establish viability—which APIs, what payloads, what responses. This proves the thing can be built, not hand-waved.

**Internal implementation stays abstract** for design to decide—library choices, timeout tuning, class structure.

| Too abstract | Right level | Too detailed |
|--------------|-------------|--------------|
| "Notify payment system" | "Stripe PaymentIntent API, webhook for payment_intent.succeeded" | "HttpClient with Polly retry, System.Text.Json..." |
| "Send event" | "EventBridge as CloudEvents, schema X" | "EventBridgeClient with 30s timeout, Serilog..." |

**Test**: Does this detail establish viability, or is it implementation preference?

**How will you know it's working?** These are forcing functions—if you can't answer them, you may have a design problem:
- On your machine (how to simulate events/callbacks locally)
- In automated tests (how to trigger the flow, how to verify it happened)
- In production (what telemetry, can you test in production)

## Worth Asking

Before writing:
- What triggers this? What ends it?
- Who are the actors at each stage?
- What can go wrong?
- What research backs this up?

After drafting:
- Can someone simulate this mentally, start to finish?
- Is it concrete enough to critique?
- Could domain experts verify the business logic?
- Could engineers verify technical viability?

## Structure

Diagrams help humans visualize. Tables help agents parse. Use both where the audience is blended.

```markdown
# [Process Name] Flow

[What this achieves, in business terms]

## Trigger
[What initiates the flow]

## Stages

### 1. [Stage Name]
**Actor**: [Who/what performs this]
**Action**: [What happens]
**Output**: [What's produced]
**Failure**: [What can go wrong]

### 2. [Stage Name]
...

## Termination
[What marks completion]
```

## What to Avoid

| Pattern | Problem |
|---------|---------|
| "The system" as actor | Too vague—who can verify this? |
| Happy path only | Failures are where understanding matters most |
| Implementation preferences | Collapsing flow and design phases |
| Hand-waving transitions | "Then it gets processed" hides complexity |
| Pure technical language | Domain experts can't verify business logic |

## Living Document

Flows have two lives:

1. **Before implementation**: proposal for discussion ("how about this?")
2. **After implementation**: documentation of how it actually works

Keep flows updated as systems evolve. What gets built may differ from what was proposed. The flow remains valuable as the canonical description of how the process works—for debugging, onboarding, and future changes.

## Relationship to Other Documents

Flow blends research and design. Research establishes what's possible (APIs, constraints). Flow proposes how it should work. Design decides how to build it.

A design doc without flow analysis may miss cross-cutting concerns. A flow without research may propose something that can't be built.

## Checklist

- [ ] Trigger and termination explicit
- [ ] Actors named at each stage
- [ ] Failure paths shown
- [ ] External boundaries have viability detail
- [ ] Internal implementation left for design
- [ ] Domain experts could verify business logic
- [ ] Engineers could verify technical viability
- [ ] Readable by blended audience

## Exemplar

See `exemplar.md` — email bounce handling from ISP rejection through suppression.

## Template

See `template.md` — structural compass with self-check questions.

*The flow makes the process discussable. Design makes it buildable.*
