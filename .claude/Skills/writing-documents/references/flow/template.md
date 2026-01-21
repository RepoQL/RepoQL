# Flow Template

This template is a compass, not a form. Include what your flow needs.

---

```markdown
# [Process Name] Flow

[One sentence: what this achieves in business terms]

## Why This Matters

[Business context—why does this process exist? What problem does it solve?]

| Without | With |
|---------|------|
| [Pain point] | [Benefit] |

## Trigger

[What initiates the flow—user action, scheduled job, incoming event, etc.]

## Stages

### 1. [Stage Name]
**Actor**: [Who/what performs this—name the component, not "the system"]
**Action**: [What happens]
**Output**: [What's produced or changes]
**Failure**: [What can go wrong, what happens then]

[Include external API/event payloads where they establish viability]

### 2. [Stage Name]
...

## Termination

[What marks the flow as complete—state changes, events published, etc.]

## Flow Diagram

[Sequence diagram for multi-actor flows, flowchart for branching logic]

## Error Handling

| Error | Behaviour |
|-------|-----------|
| [Error condition] | [What happens—retry, DLQ, skip, etc.] |

## Verification

| Environment | How to verify |
|-------------|---------------|
| Local | [How to simulate events/callbacks] |
| Automated tests | [How to trigger, how to assert] |
| Production | [What telemetry, testing in prod if viable] |

## Related

- [Link to related flows, design docs, technologies]
```

---

## Deciding What to Include

| Element | Include when... |
|---------|-----------------|
| Business context table | Stakeholders need the "why" |
| External API payloads | Proves integration viability |
| Sequence diagram | Multiple actors with handoffs |
| Flowchart | Significant branching logic |
| Timing expectations | Async behaviour or SLAs matter |
| Error handling table | Failure modes aren't obvious |
| Verification section | Non-trivial to test locally |

---

## Self-Check

- Could domain experts verify the business logic?
- Could engineers verify technical viability?
- Are external boundaries concrete enough to prove it can be built?
- Is internal implementation left for design to decide?
- Can someone mentally simulate this start to finish?
- Do you know how to verify it's working locally, in tests, in production?

---

*If you can't explain how to test it, you may not understand it yet.*
