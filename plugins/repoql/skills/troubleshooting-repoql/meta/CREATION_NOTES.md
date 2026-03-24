# Creation Notes — Diagnose Skill

## Authoring Experience

The discovery phase was extensive — the diagnostic surface area is much larger than expected. 12 probes, 9+ SQL functions, 4 diagnostic commands, queue control, error classification with auto-diagnostics, health trailers, degradation tracking, persistent artifacts. The challenge was compression, not completeness.

## What Helped

- Reading the actual implementation files (DiagnosticsCollector, ErrorClassifier, HealthDiagnosticsInterceptor) rather than just the docs. The docs describe the design; the code shows what's actually built.
- The failure modes documentation in `docs/flows/current/*/failure-modes/` gave the layered mental model that became the skill's backbone.
- Zone assessment (K:45 P:40 C:10 W:5) correctly identified this as knowledge+process blend. The escalation path IS the process; the command/function catalog IS the knowledge.

## What Was Hard

- Deciding what to put in SKILL.md vs reference files. Ended up putting everything in SKILL.md because diagnostic situations are urgent — agents need all context immediately, no progressive disclosure through reference files.
- Balancing completeness with token cost. Included the escalation path in detail because order is load-bearing, but kept the SQL functions as a compact table.
- The existing `commands/diagnose.md` in the plugin uses outdated syntax (`:diagnostics:` instead of `command(command="diagnostics")`). Need to retire or replace it.

## What Would Improve skill-builder

- Guidance on "urgent context" skills — where the agent needs everything NOW, progressive disclosure to reference files would slow them down. The zone model doesn't capture urgency.
