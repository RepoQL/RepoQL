# Mermaid Skill Creation Notes

Feedback on using the skillWriter skill to create this mermaid skill.

---

## What Was Helpful

### Zone Assessment Framework

The 100-point dot voting was genuinely useful. It forced me to think about what the skill was actually encoding before writing. For mermaid:
- Knowledge (55) - Syntax, diagram types, best practices
- Constraint (25) - Cardinal rules, accessibility requirements
- Wisdom (15) - When to use diagrams vs lists/tables
- Process (5) - Minimal sequential aspects

This clarified that I was building a **knowledge-heavy skill with strong constraints**, which guided content decisions.

### Capsule Patterns

The capsule format (Invariant -> Example -> Depth) worked well for cross-cutting concepts in SKILL.md:
- DiagramValue - when diagrams earn their place
- SyntaxSafety - quoting rules
- MeaningComments - documentation requirements

### PolymorphicSkill Concept

The idea of "shared concepts upfront, branch to variations" directly shaped the structure:
- SKILL.md = gestalt + diagram selection + cross-cutting concerns
- References = one per diagram type, self-contained

### ProductiveAbsence

SKILL.md intentionally omits enough detail that you MUST read the reference files to succeed. This prevents bloat and guides reading.

### Knowledge.md Guidance

"Give them the map and compass, not the territory" shaped my approach:
- Patterns, not exhaustive lists
- Pointers to mermaid.js.org for syntax details that change
- Examples with semantic weight

---

## What Was Confusing

### Template Selection

The knowledge-template.md mentions three structural patterns (concept-heavy, reference-heavy, schema-heavy) but doesn't clearly define when to use each. I had to infer:
- SKILL.md = concept-heavy (capsules)
- Reference files = reference-heavy (structured entries)

**Suggestion**: Add decision criteria for choosing between the three patterns.

### Reference File Granularity

Unclear guidance on:
- How many reference files is too many?
- When to combine related topics (I combined pie/xy/quadrant/sankey/treemap into data-charts.md)
- When to split (flowchart vs sequence vs state are separate)

I used judgment: combine when they share decision criteria, split when they have distinct use cases.

### Exemplars Location

The knowledge.md references exemplars:
- `docs:///guidance/writing-and-documentation/writing-capsules.md`
- `docs:///guidance/writing-and-documentation/mermaid-diagram-guide.md`

These were helpful but required going outside the skill to find. Would be cleaner to have exemplar links in the template itself.

### Zone Inheritance

When creating a polymorphic skill, should each reference file inherit the parent's zone assessment, or have its own? I assumed inheritance since the reference files are variations of the same skill.

---

## Questions Not Answered

1. **Testing with subagent**: The self-check mentions "Tested with a subagent" but no guidance on HOW to test. What prompts should I use? What counts as passing?

2. **Maintenance**: How do I know when the skill is working well vs needs revision? What signals should I watch for?

3. **Scope boundaries**: The existing mermaid-diagram-guide.md is 1475 lines. My skill is significantly smaller. Is this appropriate progressive disclosure, or am I missing critical content?

4. **Canonical source**: Now that this skill exists, should the original mermaid-diagram-guide.md be deprecated, or do they serve different audiences?

---

## What Would Improve skillWriter

### Add Decision Tree for Template Selection

```
Is it mostly concepts? -> Use capsules
Is it mostly reference data? -> Use structured entries
Is it mostly schemas/APIs? -> Use tables
Blend? -> Lead with mental model, structure sections by content type
```

### Add Exemplar Skill for Knowledge-Heavy

The skill has `exemplar-wisdom-skill.md` and `exemplar-constraint-skill.md`. A knowledge-heavy exemplar would be helpful - perhaps link to this mermaid skill once validated.

### Clarify Reference File Boundaries

Add guidance on:
- When to create separate reference files
- When to combine related topics
- Maximum reference file count guidance

### Add Testing Guidance

The self-check says "Tested with a subagent" but needs:
- Example test prompts
- What "passing" looks like
- When to iterate vs ship

### Clarify Polymorphic Skill Zones

Add: "Reference files inherit the parent skill's zone assessment unless the reference has a fundamentally different character."

---

## Process I Followed

1. Read foundation docs (gestalt, terminology, ethos)
2. Read skillWriter SKILL.md
3. Read knowledge.md (primary zone reference)
4. Read knowledge-template.md
5. Read writing-capsules.md (capsule format)
6. Read existing mermaid-diagram-guide.md (source material)
7. Complete zone assessment
8. Write SKILL.md (gestalt layer)
9. Write reference files (variations)
10. Write this feedback

Total time: ~2 hours

---

## Summary

The skillWriter skill provided a solid framework for thinking about what I was building. The zone assessment was particularly valuable for clarifying intent. Main gaps are around template selection, reference file granularity, and testing guidance.

The mermaid skill structure:
- SKILL.md: 180 lines (gestalt + selection + cross-cutting)
- 12 reference files: ~1400 lines total
- Progressive disclosure from gestalt to specific diagram types

---

*Created using skillWriter v1.0*
