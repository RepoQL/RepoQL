/**
 * repoql_explore tool implementation.
 *
 * Purpose: Registers the explore tool for intent-based repository discovery.
 * Complexity: Delegates to InstanceManager and normalizes errors.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";
import type { ResolvedConfig } from "../config/types.js";
import { TOOL_TIMEOUTS } from "../config/types.js";
import { normalizeError } from "../mcp/errors.js";
import { ExploreParams, type ExploreParamsType } from "./schemas.js";
import type { McpToolResult } from "../mcp/types.js";

const EXPLORE_DESCRIPTION = `<CONCEPT>
The best tool for 95% of your reading and understanding needs.
X-ray vision files in your repo (code/docs/config/everything). See structure, find things without reading files or knowing keywords.
</CONCEPT>

<INTENT_SELECTION>
CRITICAL: Choose intent based on YOUR CURRENT KNOWLEDGE STATE, not the task type.

### Capsule: ExploreIntent
**Invariant**
Intent matches knowledge state: Inventory (discovery), Locate (location), Inspect (structure), Explain (synthesis).
**Example**
Inventory  → tokenBudget=1000 keywords="payment" scope="file:///docs/**"
Locate     → tokenBudget=1500 keywords="settlement batch" boost="(?i)payment"
Inspect  → tokenBudget=3000 keywords="reconciliation logic"
Explain → tokenBudget=2000 keywords="Why does TokenService use refresh tokens?"
**Depth**
- All intents accept: tokenBudget, keywords, scope, boost, penalize
- Inventory: keywords optional (ranks when present); broad results
- Locate: keywords required; ranked results with snippets
- Inspect: keywords required; deep structure with line numbers
- Explain: keywords required as question; prose synthesis
- Budget by intent: Inventory 800-2000, Locate 1000-2000, Inspect 2000-5000, Explain 1000-3000
- Workflow: Inventory→Locate→Inspect→Explain (accumulates knowledge)
---

### Capsule: XRayTargeting
**Invariant**
Keywords target semantically; boost/penalize adjust ranking (regex); scope filters path (glob).
**Example**
keywords="authentication flow"              semantic targeting
boost="(?i)oauth|jwt|session"               elevate matches
penalize="(?i)test|mock|fixture"            demote matches
scope="file:///docs/service/**/*.md"        path filter
**Depth**
- All parameters work with all intents
- Keywords: 2-5 word phrases; question format for Understand
- boost/penalize: RE2 regex (\`(?i)\` case-insensitive, \`|\` alternation)
- scope: glob pattern (\`*\` single level, \`**\` recursive, \`*.md\` extension)
- boost adjusts ranking; scope excludes—choose based on need
---

### Capsule: ExplainNarrow
**Invariant**
When using explain, queries must be self-contained; keywords become search terms directly.
**Example**
✓ "What is AuthService responsible for?"
✓ "Why does PaymentProcessor use idempotency keys?"
✗ "Explain everything about authentication"
✗ "What does this service do?"
**Depth**
- No pronouns or references—explore has no conversation context
- Include entity names, service names, specific concepts
- Broad queries dilute relevance; focused queries concentrate it
- Derivation section shows evidence; verify citations before trusting
---

<KNOBS>
tokenBudget:
    investment level → more tokens = richer detail. You set the budget, explore maximizes value.
    if you want to be sure you found everything, set a high budget
    important: budget is exactly how many tokens you want to spend on seeing the answer, it is not a maximum.
    the underlying query is the same regardless of budget - budget controls the level of detail in the response, and attempts to maximize value given the budget and intent

keywords: search terms for hybrid (semantic + lexical) search
  - Questions + boost patterns work best: keywords="How does auth work?" boost="(?i)Auth.*|Validate.*"
  - Questions alone find conceptually related content via semantic search
  - Control results with patterns to boost or penalize matches

Filter with scope (glob), guide with keywords (semantic), rank with patterns (regex). Results ranked by confidence.
</KNOBS>

<PATTERNS>
boost: RE2 regex patterns to boost matching results (comma-separated)
  - Validate.*Token → boost results containing "ValidateToken", "ValidateAccessToken", etc.
  - (?i)error|exception → boost error handling code (case-insensitive)
  - Auth.* → boost anything starting with "Auth" (AuthService, Authentication, etc.)

penalize: RE2 regex patterns to de-rank matching results (comma-separated)
  - (?i)test|spec|mock → de-rank test files and mocks
  - \\.generated\\. → de-rank generated code
  - deprecated|obsolete → de-rank deprecated code

Note: RE2 regex (no backreferences/lookahead). Patterns applied at SQL level for true filtering.
</PATTERNS>

<EXAMPLES>
Inventory → Locate → Inspect workflow:
1. tokenBudget=1000, intent=inventory, scope=file:///src/** → See what modules exist
2. tokenBudget=1200, intent=locate, keywords="authentication validation" → Locate auth code
3. tokenBudget=2000, intent=inspect, scope=file:///src/Auth/**, keywords="JWT validation" → Read the code

Quick references:
- What docs exist? → intent=inventory, scope=repoql-docs://**
- Understand architecture → intent=inventory, scope=file:///src/**, keywords="How is this organized?"
- Find a feature → intent=locate, keywords="Where is caching implemented?"
- Debug specific code → intent=inspect, scope=file:///path/to/file.cs, keywords="error handling"
- Get synthesized explanation → intent=explain, keywords="How does authentication work?"
</EXAMPLES>

<REMEMBER>
Start with INVENTORY when you don't know the codebase vocabulary yet.
Use LOCATE once you know what concepts/terms to search for.
Use INSPECT only after LOCATE has shown you which files matter.
Use EXPLAIN when you want a prose explanation synthesized by LLM.
Each intent serves a different knowledge state - don't skip steps.
</REMEMBER>`;

/**
 * Registers the repoql_explore tool.
 */
export function registerExploreTool(
  api: any,
  manager: InstanceManager,
  config: ResolvedConfig,
  getWorkdir: () => string
): void {
  api.registerTool({
    name: "repoql_explore",
    description: EXPLORE_DESCRIPTION,
    parameters: ExploreParams,
    async execute(_id: string, params: ExploreParamsType): Promise<McpToolResult> {
      try {
        const workdir = getWorkdir();
        const client = await manager.getInstance(workdir);
        const timeout = TOOL_TIMEOUTS.explore ?? config.defaultTimeoutMs;

        const args: Record<string, unknown> = {
          intent: params.intent,
          tokenBudget: params.tokenBudget,
        };

        if (params.scope !== undefined) {
          args.scope = params.scope;
        }
        if (params.keywords !== undefined) {
          args.keywords = params.keywords;
        }
        if (params.boost !== undefined) {
          args.boost = params.boost;
        }
        if (params.penalize !== undefined) {
          args.penalize = params.penalize;
        }

        return await client.callTool("explore", args, timeout);
      } catch (err) {
        return normalizeError(err);
      }
    },
  });
}
