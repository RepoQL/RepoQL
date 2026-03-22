# Creation Notes: writing-tool-descriptions

## Context

Created after a full-day session of empirical testing — ~30 agent variants, same tasks, measuring tool adoption and answer quality. The findings are grounded in observed behavior, not theory.

## What Helped

- The skill-builder's zone assessment immediately clarified this is wisdom (70), not knowledge. The temptation was to write a reference doc of "what works" — but the real value is the way of thinking.
- The wisdom template's emphasis on questions over explanations directly mirrors the experimental finding: questions in checklists force simulation, statements are skimmed.
- Writing DISCOVERY.md first (the raw experimental findings) then distilling to SKILL.md (the transferable wisdom) was the right sequence. The discovery doc is 5x longer and captures specifics; the skill captures principles.

## What Was Hard

- Deciding what to cut from the capsules. Every finding felt important. But wisdom skills should be one screen. The capsules earned their place by being transferable (SensesNotTools applies to any tool description, not just RepoQL's).
- Resisting the urge to include "how to structure a description" as a process. The experiments showed that prescribed structure is ignored — understanding generates structure naturally. This is wisdom about that exact phenomenon, so including process steps would be ironic.

## What Would Improve This

- Testing the skill itself by having an agent use it to write a description for a completely different tool. The RepoQL bias is strong — all findings come from one tool. Validation on a second tool would strengthen confidence.
- The DISCOVERY.md is currently a synthesis. The raw experimental data (agent outputs, tool audits, token counts) was in a conversation that's now gone. Future experiments should save raw data.

## For Future Maintainers

The four capsules (SensesNotTools, AsymmetricRisk, VocabularyDiscovery, PrimacyAndRecency) are the load-bearing insights. If you add a capsule, it must be equally transferable — applicable to ANY tool description, not just ones like RepoQL. If it's RepoQL-specific, it belongs in RepoQL's docs, not here.
