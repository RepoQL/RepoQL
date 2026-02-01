# Creation Notes - codex skill

## What Helped

1. **Direct conversation with Codex** - Asked Codex to explain its own strengths, failure modes, and best prompting patterns. It gave honest, specific answers.

2. **Building on codex-review** - Already had the core insight about lacking intuition from that skill. This skill extends to other use cases.

3. **User feedback** - "Codex is much more literal... not stupid, very much the opposite. It just lacks intuition for what you wanted but didn't say." This framing was key.

4. **Zone assessment** - Knowledge (40) + Wisdom (30) pattern. Users need facts AND judgment about when to delegate.

5. **Live experimentation** - Running actual Codex calls revealed the yin/yang symbiosis:
   - Explicit step-by-step prompts dramatically outperform vague ones
   - Codex finds issues you wouldn't think to look for (3 race conditions I didn't know existed)
   - Iterative refinement (identify → propose → implement) is powerful
   - The handoff—translating vague intent into explicit steps—is where Claude adds value

## Key Insight

The yin/yang: Claude and Codex are complementary partners.

- **Claude**: inference, intent, synthesis. Asks "what did they probably mean?"
- **Codex**: execution, precision, depth. Asks "what did they say?"
- **Together**: vague intent → explicit steps → systematic execution → synthesized insight

Neither is complete alone. The handoff point is Claude's unique contribution.

## What's Different From codex-review

- `codex-review` is for reviewing code (read-only analysis)
- `codex` is for doing work (implementation, debugging, tests)
- Both share the core insight; `codex` has the deeper wisdom (YinYang capsule)

## Experimental Evidence

| Experiment | Finding |
|------------|---------|
| Vague prompt | Reasonable results, less structured |
| Step-by-step prompt | Thorough, actionable, precise file:line refs |
| Thread continuation | Full context retained, iterative refinement works |
| Investigation task | Found 3 race conditions I wouldn't have looked for |

## What Might Be Missing

1. **Examples of actual Codex output** - Could add sample responses
2. **Integration with CI/CD** - `codex exec` in pipelines
3. **Error recovery** - What to do when Codex gets stuck
4. **When Codex disagrees with Claude** - How to reconcile different perspectives

## SkillWriter Feedback

The polymorphic skill pattern might apply here - `codex` as parent skill with `codex-review` as a specialized variation. Currently separate skills, but they share the core insight. codex-review now references the main skill for the symbiotic wisdom.
