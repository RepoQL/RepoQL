---
name: writing-tool-descriptions
description: "Shape how agents perceive and reach for tools. Use when writing MCP server descriptions, tool descriptions, or agent prompts that introduce tool capabilities."
zones: { K: 15, P: 5, C: 10, W: 70 }
---

# Writing Tool Descriptions

A tool description is not a reference manual. It's a lens that reshapes how agents perceive and reach for capabilities.

## Context

You're writing text that agents receive when a tool becomes available — server instructions, tool descriptions, agent prompts. This text is injected into every session. Every token costs. The description must change behavior without prescribing it.

## What Must Remain True

- Understanding drives behavior. Instructions are ignored. MUST/DO NOT language is actively counterproductive — agents skip it or resist it.
- Each insight stands alone. If it's a separate idea, it earns a separate capsule. Folding two concepts together muddies both.
- The description must be safe to act on without reading additional docs.
- Quality over metrics. An efficient agent that answers the wrong question is worthless. Always test with real tasks and evaluate answer quality, not just tool adoption or token counts.

## What Good Looks Like

- An agent reads the description and naturally reaches for the right tool — not because it was told to, but because it understands why
- The agent composes tool features creatively, attempting combinations you never demonstrated
- The description works across diverse tasks without modification

---

### Capsule: SensesNotTools

**Invariant**
Frame capabilities as extensions of perception, not as separate instruments to invoke.

**Example**
"Think of it as extra senses — you can feel the shape of a thousand files, see relationships grep will never find, hear relevance ranked by meaning." Agents who understood the tool as part of themselves used it naturally. Agents who understood it as a separate thing reached for familiar tools instead.

**Depth**
- Find sensory mappings that connect capabilities to instincts: "feel" → shape/structure, "see" → relationships, "hear" → relevance, "reach" → precision
- The mappings must be specific to YOUR tool's actual capabilities — not generic "it's powerful"
- This works because it changes what the agent perceives as AVAILABLE, not what they're told to do

---

### Capsule: DescriptionLayers

**Invariant**
Server descriptions, tool descriptions, and agent prompts serve different purposes. Each layer has a job.

**Example**
The server description builds the mental model — WHY these tools exist, HOW to think about them. The tool descriptions teach mechanics — WHAT parameters to pass, WHAT the syntax is. The agent prompt adds context — WHICH approach suits THIS task. Together they layer: understanding → mechanics → context.

**Depth**
- Server description: the gestalt. Capsules, senses, boundaries. Teaches the mental model across all tools. ~600 tokens. Injected once per session.
- Tool descriptions: the reference. Parameters, examples, patterns. Teaches how to call each tool. Already exist on each tool — don't duplicate this in the server description.
- Agent prompts: the approach. Behavioral nudges for specific agent types. Can reference the mental model and add task-specific guidance.
- help:// docs: the depth. Full documentation, queryable on demand. Never required for correct behavior but available for mastery.
- Don't repeat across layers. Each layer trusts the others.

---

### Capsule: AsymmetricRisk

**Invariant**
When experimentation is cheap and success is valuable, agents compose creatively. When it's expensive, they reach for the familiar.

**Example**
"A bad query costs 1500 tokens. A good one saves 50k." Agents who understood this tried creative compositions — symbol globs, scoped semantic search, multi-URI reads. Agents who feared wasting tokens fell back to grep.

**Depth**
- State the cost of failure explicitly — make it feel safe to experiment
- "Wild magic — composable, responsive to intent, and forgiving" outperformed feature lists
- The invitation to experiment produced more creative tool use than any amount of documentation
- For tools with destructive actions: be honest about the risk. Don't encourage experimentation with `DROP TABLE`. The asymmetry must be real.

---

### Capsule: VocabularyDiscovery

**Invariant**
An agent's first contact with a tool teaches the vocabulary for everything after. Make that first contact reveal the terms-of-art.

**Example**
First explore returns `JwtTokenValidator`, `SessionMiddleware`, `OAuthConfig`. Now the agent knows the real names. Every subsequent call uses precise addressing instead of guessing. Without that first contact, it greps for "auth" and misses half the surface.

**Depth**
- Whatever a tool's natural first action is, it should teach the language for subsequent actions
- For search tools: the first search reveals what exists and what things are called
- For schema tools: the first describe reveals what's queryable
- For filesystem tools: the first listing reveals the structure
- The description should make this first-contact step feel natural, not prescribed. "Explore reveals the vocabulary" changed behavior more than "explore first."

---

### Capsule: PrimacyAndRecency

**Invariant**
The opening sentence frames interpretation of everything after. The closing section is most accessible when the agent acts.

**Example**
"Pre-built structural index" as the opening frame made agents think "query the index" rather than "search files." Checklist questions at the end ("Am I about to burn tokens rediscovering what the index already knows?") forced self-correction at the moment of tool selection.

**Depth**
- Opening: what the tool IS, framed through the senses metaphor
- Middle: capsules that build understanding (agents scan for what's relevant to their current task)
- Closing: questions or boundaries that reinforce the most important behaviors
- Statements in checklists are skimmed. Questions force simulation.

---

## Worth Exploring

- What would an agent lose by not having this tool? Frame the description around that absence, not around features.
- If you imagine the agent's hand reaching for a tool — which tool does it reach for, and why? Does your description redirect that reach?
- Are you teaching what the tool IS, or what to DO with it? One changes instincts. The other is skimmed.
- What metaphor makes the tool feel like part of the agent rather than something external?
- Where does the agent encounter the tool's most surprising capability — the thing they'd never guess from the parameter list?
- Can an agent experiment safely? If a wrong guess is expensive, they won't try. If it's cheap, say so.
- Are you explaining, or are you asking? Questions at the decision moment change behavior. Explanations get nodded past.

## Testing

Spawn a subagent with your description embedded in its prompt. Give it a real task — not a toy. Observe:
- Does it reach for your tool or fall back to familiar alternatives?
- Does it compose features creatively?
- Does it answer the right question with the right level of detail?
- Would a control agent (no description) do as well?

The metric that matters most is answer quality. Tool adoption and token efficiency matter only if the answer is correct.

## Boundaries

- This is not about reference documentation (API docs, parameter lists). Those serve lookup; descriptions serve perception.
- This applies to descriptions read by AI agents. Human-facing docs have different constraints.
- Don't repeat what tool parameter descriptions already teach. The server description's job is the mental model — the WHY and WHEN. Tool descriptions handle the HOW and WHAT.

## Final Thought

The best description disappears into the agent's thinking. It doesn't feel like instructions — it feels like understanding they already had.
