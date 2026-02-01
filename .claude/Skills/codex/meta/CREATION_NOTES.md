# Creation Notes - codex skill

## What Helped

1. **Direct conversation with Codex** - Asked Codex to explain its own strengths, failure modes, and best prompting patterns. It gave honest, specific answers.

2. **Building on codex-review** - Already had the core insight about lacking intuition from that skill. This skill extends to other use cases.

3. **User feedback** - "Codex is much more literal... not stupid, very much the opposite. It just lacks intuition for what you wanted but didn't say." This framing was key.

4. **Zone assessment** - Knowledge (40) + Wisdom (30) pattern. Users need facts AND judgment about when to delegate.

## Key Insight

The core wisdom: Codex excels at execution when given clear context. Claude excels at inference when context is incomplete.

- Delegate to Codex: well-defined tasks, evidence-rich debugging, scoped implementation
- Keep in Claude: exploration, ambiguous requirements, "help me understand"

## What's Different From codex-review

- `codex-review` is for reviewing code (read-only analysis)
- `codex` is for doing work (implementation, debugging, tests)
- Shared insight about lacking intuition applies to both

## Prompt Templates

The templates for ticket completion, debugging, and race condition analysis are the highest-value content. These encode patterns that work well.

## What Might Be Missing

1. **Examples of actual Codex output** - Could add sample responses
2. **Integration with CI/CD** - `codex exec` in pipelines
3. **Error recovery** - What to do when Codex gets stuck

## SkillWriter Feedback

The polymorphic skill pattern might apply here - `codex` as parent skill with `codex-review` as a specialized variation. Currently separate skills, but they share the core insight capsule.
