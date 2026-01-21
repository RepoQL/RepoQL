# Creation Notes: UDF Author Skill

Feedback on authoring this skill. Rewrite entirely when maintaining.

---

## What Helped

1. **Reading DuckDB research first**: The `docs/duckdb/UDFs.md` document gave context for why RepoQL's UDF framework works the way it does. Understanding vectorized execution and the C API foundation made the all-VARCHAR strategy make sense.

2. **Exploration agent**: Using the Task tool with Explore subagent to understand the codebase was efficient. The agent found all the key files and provided line numbers.

3. **Zone assessment forcing function**: Explicitly allocating 100 points clarified that this is primarily knowledge (framework details) with significant constraints (hard rules). Helped avoid over-prescribing process.

4. **Exemplar skills**: The constraint exemplar (`secret-handling`) showed how terse constraint content should be. The wisdom template showed the questions-not-answers approach.

5. **Iterative refinement**: Initial version identified gaps (testing, debugging, performance). Adding these as separate references kept each file focused.

## What Was Difficult

1. **Balancing depth**: The framework has many details. Deciding what goes in SKILL.md vs references required judgment. Erred toward putting core capsules (ScalarUdf, StructuredUdf, MacroPattern, DI) in SKILL.md and everything else in references.

2. **Patterns vs constraints overlap**: Some patterns exist because of constraints (parameterless UDF workaround). Chose to put the workaround in patterns.md with a reference to the constraint, and the rule in constraints.md.

3. **Working examples**: Creating complete, working examples required mental simulation of what would actually compile and run. The patterns.md examples are designed to be copy-paste ready, not pseudo-code.

4. **Testing reference**: Had to infer testing patterns from RepoQL conventions (TUnit, AwesomeAssertions, FakeItEasy) and apply them to UDF context. No existing UDF-specific test examples to reference.

## Structure Evolution

Initial structure (v1):
- SKILL.md
- references/framework.md
- references/patterns.md
- references/constraints.md

Final structure (v2):
- SKILL.md
- references/framework.md (core knowledge)
- references/patterns.md (examples)
- references/constraints.md (hard rules)
- references/testing.md (verification)
- references/debugging.md (troubleshooting)
- references/performance.md (optimization)

The split into Core and Operations references sections in SKILL.md helps signal the difference: Core is "how to build", Operations is "how to verify and optimize".

## Suggestions for skillWriter

1. **Template for knowledge-heavy skills with significant constraints**: This skill is K:50/C:25. A hybrid template might help.

2. **Guidance on reference file granularity**: Ended up with six references. Could have consolidated (debugging+performance) or split further (patterns by type).

3. **Operations section pattern**: Testing, debugging, and performance are common needs. Consider a standard "operations" reference pattern.

---

*Second iteration. Added testing, debugging, performance based on identified gaps.*
