# Creation Notes - codex-review skill

## What Helped

1. **Direct experimentation first** - Using Codex via MCP before writing taught me things docs wouldn't: the threadId pattern, that it doesn't see Claude history, that it CAN use RepoQL.

2. **Asking Codex to explain itself** - "What are your limitations?" produced honest, specific answers about hallucination risks and context requirements.

3. **Zone assessment forced focus** - At 45% Knowledge, I knew to prioritize facts (tool names, flags) over process.

4. **Capsule format** - ReviewModeChoice and EffectiveReviewPrompt compress the key insights into scannable chunks.

## What Was Confusing

1. **MCP server vs CLI overlap** - Both can review code, docs conflate them. Had to tease apart which features belong to which mode.

2. **RepoQL integration** - Codex claims it can use RepoQL but the subagent test showed it didn't actually invoke any RepoQL tools. Requires explicit prompting.

3. **Codex is literal** - Key insight from user: Codex does not infer like Claude. If you want it to run git diff, you must say "run git diff". This is the most important thing to communicate in the skill.

## What's Missing

Based on subagent feedback:

1. **Response format example** - Show what `{threadId, content}` actually looks like
2. **Context clarity** - Does Codex read local files, or only what you paste in the prompt?
3. **RepoQL prompting** - How to explicitly get Codex to use RepoQL during review

## Improvement Ideas

1. Add a `references/examples.md` with full request/response transcripts
2. Clarify that Codex MCP mode relies entirely on prompt context - it doesn't automatically read the diff
3. Add example of prompting Codex to use RepoQL: "Use mcp__repoql__query to find call sites before reviewing"

## SkillWriter Feedback

The zone assessment was valuable - it clarified this is mostly Knowledge injection with some Wisdom about when to use which mode. The template in `references/knowledge.md` would have been useful if it existed.
