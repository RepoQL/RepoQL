# Discovery: Writing Tool Descriptions

Research conducted 2026-03-23 across ~30 agent experiments, testing how MCP server description text affects agent tool adoption and answer quality.

## The Core Problem

Agents with access to a powerful indexed knowledge graph (RepoQL) consistently fell back to grep+read for discovery and understanding tasks. The server description was 650 tokens injected into every session and failed to change behavior for 2 of 3 agents in the initial test.

## Experimental Method

Created agent definition files with embedded descriptions, launched agents with identical tasks, compared tool choices, token usage, and answer quality against no-RepoQL control agents. Iterated the description through ~30 variants across multiple rounds, testing on Sonnet and Opus models.

## Key Findings

### What Changed Behavior

**The "extra senses" metaphor (breakthrough).** Framing the tool as "extra senses grafted onto your mind" — feel shape, see relationships, hear relevance, reach precisely — drove 100% RepoQL adoption. Agents who understood the tool as part of themselves used it naturally. The metaphor connected abstract capabilities to visceral instincts.

**Capsule format.** Invariant → Example → Depth structure taught understanding that changed tool choices. The invariant is the "if you read nothing else" line. The example makes it concrete. The depth adds nuance without requiring attention.

**Standalone Addressability capsule.** Every time this was folded into another capsule, tool adoption degraded. The insight "everything is addressable by URI — symbols, line ranges, globs" is separate from "there's an index." Merging them muddied both. Proven across 4 separate rounds.

**ExploreFirst as vocabulary discovery.** The insight that explore teaches you the codebase's vocabulary (actual class names, patterns, terms-of-art) — not just file locations — changed how agents composed subsequent calls. Agents who explored first used precise symbol addressing. Agents who skipped exploration guessed names and grepped blind.

**WieldWithCreativity / wild magic.** "The index is wild magic — composable, responsive to intent, and forgiving. Your instincts are probably right." This plus "a bad query costs 1500 tokens, a good one saves 50k" produced the most creative tool use of any variant. Agents tried symbol globs, scoped semantic search, multi-URI reads, SQL graph traversal — combinations never demonstrated in the description.

**Checklist questions (recency effect).** "Am I about to burn tokens rediscovering what the index already knows?" at the end of the description forced self-correction at tool selection time. Questions outperformed statement checklists because they force simulation rather than passive reading.

**Boundaries.** "Never read a file to discover its structure" and "never search without seeing the landscape first" prevented the two most common failure modes. Sparse, specific, actionable.

### What Failed

**Prescriptive instructions (MUST/DO NOT).** The most heavily prescriptive variant — with explicit mandated sequences, "you MUST follow this sequence," "DO NOT skip" — produced the WORST result. The agent completely ignored the instructions and fell back to native Grep/Read. 0% RepoQL adoption. This happened consistently across multiple rounds.

**Decision tables.** "When you need X, use tool Y" tables were either ignored or made agents less efficient. Agents who understood WHY made better choices than agents told WHAT.

**Life force / token scarcity framing.** "Tokens are life force — budget lets you choose how much life force an answer is worth." Evocative but didn't change tool choices. Agents already know tokens are precious. The problem was never motivation — it was understanding what the tools can do.

**Capability examples without conceptual frame.** Lists of "things you can do" (decision tables, feature examples) underperformed capsules that taught WHY the capability exists. Understanding generates correct tool choices for novel situations; examples only cover the demonstrated cases.

**Folded capsules.** Whenever two insights were merged into one capsule, both lost effectiveness. Addressability folded into PrebuiltIndex: adoption dropped to 19%. The capsule format's "one idea per capsule" rule is load-bearing.

**Parallel tool call encouragement.** Explicitly telling agents to "fire multiple explores simultaneously" didn't improve results. Agents who understood the tools parallelized naturally without being told.

### Quality vs Metrics

**An efficient agent that answers the wrong question is worthless.** The "winning" variant by metrics (fewest calls, fewest tokens) answered about x-ray summaries when asked about the embedding pipeline. Quality evaluation is non-negotiable.

**RepoQL agents found things control agents missed.** The caching layer (CachingEmbeddingProvider, CachingContextualEmbeddingProvider) was consistently found by RepoQL agents and missed by grep+read control agents despite spending 113k tokens. Tree headlines surface files by location; grep surfaces files by content. You need both, but tree catches what grep misses.

**The orient-first approach found documentation.** Agents who oriented with `tree: headlines` before searching found flow docs, design docs, and READMEs that keyword-based explore missed. Documentation has filenames that describe purpose but content that doesn't always match keyword searches.

### Model Differences

**Opus vs Sonnet.** Opus used tools more precisely across all variants. Control agents on Opus were notably more systematic. The RepoQL advantage held regardless of model, but the absolute quality ceiling was higher on Opus.

## The Final Description Structure

The winning description (~600 tokens) follows this structure:

1. **Opening** (primacy): what the tool IS + senses metaphor. Frames all subsequent interpretation.
2. **Capsule: Addressability**: URI power — symbols, line ranges, globs, modifiers. Standalone.
3. **Capsule: ExploreFirst**: vocabulary discovery — explore teaches names for everything after.
4. **Capsule: WieldWithCreativity**: wild magic — composable, forgiving, asymmetric risk.
5. **Tools**: 4 lines, minimal.
6. **Boundaries**: 3 lines — never read to discover, never search blind, never unscoped explain.
7. **Questions** (recency): 4 self-check questions that force simulation at tool selection time.

The PrebuiltIndex capsule was absorbed into the opening paragraph where it has maximum primacy effect. Three capsules instead of four — tighter.
