# Writing Low-Agency Process Documents

## Where You Are

You scored high on low-agency process. You're encoding a sequence where order matters and steps cannot be safely skipped.

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

On prescription level:
- Are you prescribing tools, or outcomes?
- Which steps need exact commands vs general direction?
- Where does harm live? (Detail those parts)

## What Process Documents Need

**Prerequisites** → what must be true before starting

**Steps** → the sequence, with verification for each

**Recovery** → what to do when steps fail

**Completion** → how to know you're done

Not all processes need all sections. But missing recovery paths is a common and dangerous gap.

## Required Practices

- Detail where harm lives; trust elsewhere
- Include verification for each step
- Document failure paths, not just happy paths
- Prescribe outcomes over tools unless tool choice matters for safety
- Prefer deterministic verification (commands) over subjective ("looks right")

## Success Looks Like

- Reader can execute without guessing at critical junctures
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

3. Merge to main
   → Verify: deployment triggers

4. Check production
   → Verify: health checks pass
   → If failed: rollback immediately
```

The first is a checklist pretending to be a process. The second acknowledges it. The third is actual process.

## Exemplar

See `exemplar-low-agency-process.md` — a sequence encoded precisely.

## Template

See `low-agency-process-template.md` — a structural compass, not a form to fill.

## Final Thought

The sequence is the wisdom. It was learned the hard way.
