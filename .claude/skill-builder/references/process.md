# Writing Process-Heavy Skills

## Where You Are

You scored high on Process. You're encoding a sequence where order matters and steps cannot be safely skipped.

## Core Objective

Capture the path that was learned through experience—often failure—so others don't repeat the mistakes.

## The Key Distinction

Process: order is load-bearing; skipping or reordering causes harm.
Checklist: items need completion; order doesn't matter.
Guidance: suggested approach; deviation is acceptable.

**The test**: What happens if you skip or reorder?
- Harm occurs → process
- Incompleteness → checklist
- Suboptimal outcome → guidance

## Worth Exploring

- Is order truly load-bearing here?
- What happens if step N fails?
- Where does flexibility exist within this sequence?
- What must be true before starting?
- How do you verify each step succeeded?
- Does this process live elsewhere (runbook, CI config)?

On prescription level:
- Are you prescribing tools, or outcomes?
- Which steps need exact commands vs general direction?
- Where does harm live? (Detail those parts)

## What Process Skills Need

**Prerequisites** → what must be true before starting

**Steps** → the sequence, with verification for each

**Recovery** → what to do when steps fail

**Completion** → how to know you're done

Not all processes need all sections. But missing recovery paths is a common and dangerous gap.

## When Diagrams Help

**The test**: Are there branches, decisions, or parallel paths?
- Yes → diagram may add value
- No → use a numbered list (clearer, fewer tokens)

| Process shape | Diagram type | When it helps |
|---------------|--------------|---------------|
| Branching logic | Flowchart | Multiple paths through decisions |
| State transitions | State diagram | Valid states and transitions matter |
| Multi-party coordination | Sequence diagram | Who does what, in what order |
| Linear sequence | None - use list | Never diagram linear sequences |

See `docs:///guidance/writing-and-documentation/mermaid-diagram-guide.md` for syntax and best practices.

## Required Practices

- Detail where harm lives; trust elsewhere
- Include verification for each step (how do you know it worked?)
- Document failure paths, not just happy paths
- Prescribe outcomes over tools unless tool choice matters for safety
- If the process lives elsewhere, point to it—don't duplicate what will rot

## Success Looks Like

- Agent can execute without guessing at critical junctures
- Failure modes have clear recovery paths
- Prerequisites are explicit, not assumed
- Flexibility exists where order doesn't matter
- The sequence protects against the mistakes that created it

## The Transformation

You will write this:
```
1. Update README
2. Add changelog entry
3. Bump version number
4. Update dependencies
```

Write this instead:
```
Release Checklist (any order):
- [ ] README updated
- [ ] Changelog entry added
- [ ] Version bumped
- [ ] Dependencies current
```

Or if order truly matters:
```
1. Run tests locally
   → Verify: all pass
   → If failed: fix before proceeding

2. Create PR
   → Verify: CI passes

3. Get approval
   → Verify: at least one reviewer

4. Merge to main
   → Verify: deployment triggers

5. Check production
   → Verify: health checks pass
   → If failed: rollback immediately
```

The first is a checklist pretending to be a process. The second acknowledges it. The third is actual process—each step depends on the previous, with verification and recovery.

## Scripts as Automation

Scripts can execute steps instead of describing them.

Instead of:
```
3. Run database migration
   → Verify: all migrations applied
```

Consider:
```
3. Run database migration
   → Execute: scripts/migrate.sh
   → Verify: output shows "All migrations complete"
   → If failed: check output, run scripts/rollback.sh
```

**When to use scripts for process:**
- Repeatable steps that benefit from consistency
- Complex commands that are error-prone to type
- Steps that need specific environment setup
- Verification checks that can be automated

Scripts reduce human error and make verification explicit.

## Exemplar

Look for deployment runbooks, incident response procedures, or database migration guides in your codebase. Good ones have: prerequisites, steps with verification, and recovery paths.

## Template

See `process-template.md` for structural patterns.

## Final Thought

The sequence is the wisdom. It was learned the hard way.
