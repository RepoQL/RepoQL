# Writing Constraint-Heavy Skills

## Where You Are

You scored high on Constraint. You're encoding rules where deviation is the failure mode—not guidance, not best practice, but lines that must not be crossed.

## Core Objective

State the rules. Trust the agent to understand why.

## The Key Distinction

Constraint: deviation causes harm (security breach, compliance violation, data loss).
Knowledge: misunderstanding causes confusion.
Guidance: deviation causes suboptimal outcomes.

**The test**: What happens if they deviate?
- Harm occurs → constraint
- Confusion results → knowledge gap
- Suboptimal outcome → guidance

## Constraints vs Knowledge

Both are "facts Claude needs." The difference is purpose and tone.

| Aspect | Knowledge | Constraint |
|--------|-----------|------------|
| Purpose | Enable understanding | Enforce compliance |
| Tone | Explain, provide context | State simply, no justification |
| Failure mode | Thinks incorrectly | Crosses a line |
| Length | As needed for clarity | As short as possible |

A PCI skill might have both:
- Knowledge: "CHD is cardholder data. CDE is the cardholder data environment. Scope is determined by..."
- Constraint: "Never store CVV after authorization. Never log full PAN."

The zone score calibrates the mix and signals how to write each section.

## Worth Exploring

- Is deviation truly unacceptable, or just undesirable?
- Can an agent know if they're complying?
- Is this enforced by tooling, or just documented?
- What's the scope? Where doesn't this apply?
- Are there legitimate exceptions? How are they handled?
- Are you stating rules or explaining them?

## Required Practices

- State rules simply; don't justify
- Be specific enough to follow ("never log credentials" not "be secure")
- Define scope explicitly
- Note enforcement mechanism if it exists
- Make exceptions explicit if they exist
- Keep it short—brevity is a feature

## Success Looks Like

- Agent can comply without interpretation
- Rules are specific and testable
- Scope is clear
- No drift from unenforced rules
- Constraints don't paralyze (not everything is "must")

## The Transformation

You will write this:
```
Credentials should never be logged because they could be exposed
in log aggregation systems, potentially allowing unauthorized access.
When handling credentials, always ensure they are masked or omitted
from any logging statements. This is especially important in
production environments where logs may be accessed by multiple teams.
```

Write this instead:
```
## Credential Logging

- Never log credentials
- Never log tokens
- Never log API keys

Scope: all services, all environments
Enforcement: log scanner in CI
```

One explains. One constrains.

## Scripts as Enforcement

Scripts can verify compliance instead of trusting it.

```
## Credential Logging

- Never log credentials
- Never log tokens

Enforcement: scripts/scan-logs.sh
→ Output: violations found, or "No violations"
```

**When to use scripts for constraints:**
- Automated scanning (secrets, credentials, patterns)
- Pre-commit/CI verification
- Compliance checks that can be codified
- Audit trail generation

A constraint with automated enforcement is stronger than documentation alone. If the rule matters enough to state, consider whether it matters enough to verify.

## On Pure Constraint Skills

They're rare. Constraints almost always need some knowledge for context—scope, definitions, terms. A realistic constraint-heavy skill is probably C:55-70, K:20-35.

The zone's value isn't enabling pure constraint skills. It's forcing you to ask: "How much of this is non-negotiable rules?" That question changes how you write those sections.

## Exemplar

See `exemplar-constraint-skill.md` - rules stated simply, scope explicit, enforcement noted, no justification.

## Template

See `constraint-template.md` for structural patterns.

## Final Thought

A constraint is a decision already made.
