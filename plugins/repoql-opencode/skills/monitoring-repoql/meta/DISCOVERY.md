# Discovery — monitoring-repoql

## Outcome in the world

Agents stop sleep-polling `engine_status` or re-running queries to see whether the index is ready. One monitor call blocks (or wakes them from the background) exactly when the state they need is true, and failures surface as exit codes instead of being discovered later. The silent failure this kills: an agent that "waited" with sleeps, missed the milestone, and either worked against a half-built index or burned minutes it didn't need to.

## Zone calibration

Knowledge primary — flag semantics, exit-code table, line vocabulary, and interaction footguns are facts an agent cannot derive. No process zone: the recipes are freely reorderable. A dash of constraint in the footguns (tool-timeout shadowing, alerts×conditions) where deviation is the failure mode.

## Evidence (verified 2026-07-03/04, rql 1.6.18)

- **Iterate-then-crystallize:** this skill was extracted from a solved task — a real CI pipeline (`opencode-review.yml` in RepoQL.Core) built and debugged around `rql monitor --exit-on 'semantic search live' --timeout 35m`. Hit live along the way: `--until idle` timing out at 35m on a cold index (full-text embedding track), the auto-launched implicit host exiting under the monitor, the silent host-down wait.
- **Implementation read**, not remembered: `MonitorCommand.cs`, `SignalMonitorLoop.cs`, `SignalMonitorRenderer.cs`, `SignalMonitorConditions.cs` (RepoQL.Core at 1.6.18). Confirmed from code: fail-on beats exit-on on the same line; `HasPendingCondition = until || exit-on`, so a bare `--timeout` (or `--fail-on`-only) watch exits 0 on expiry; `--since` replay flows through the same line matcher (race-free attach); `--alerts` filters lines *before* condition matching (renderer `Emit` keeps only non-routine lines); host probe backs off 2s → 10s after 60s down; reconnect survives host restarts and narrates them.
- **Line vocabulary** transcribed from `SignalMonitorRenderer`, cross-checked against live CI monitor output from this session.
- **Version floor** from CHANGELOG 1.6.18 ("`rql monitor` gains `--until idle`, `--exit-on`, `--fail-on`, `--timeout`"); a local 1.6.17 binary confirmed to expose only `--since`/`--alerts`.

## Key insights the skill encodes

- Edge-triggered (`--exit-on`, needs `--since` to cover the attach gap) vs level-triggered (`--until idle`, answers from current state) — the mental model that prevents both missed-milestone hangs and needless replays.
- Searchable, not idle, is the working bar; idle is for artifacts (cache snapshots).
- The harness's own tool timeout shadows `--timeout` and masquerades as a monitor hang.
- `--alerts` narrows what conditions can match — a real trap when combining flags.

## Frontmatter (verified 2026-07-04 against code.claude.com/docs/en/skills + /permissions)

- `allowed-tools: Bash(rql monitor:*)` — pre-approves (grants, does not restrict) the monitor while the skill is active, so an agent that activates the skill can run it without a permission prompt. Safe to grant broadly because the monitor is read-only: it attaches, renders, and exits — it never summons a host (verified in the implementation read above). Deliberately narrow: `rql serve` and `rql update` mutate state and stay behind normal permissions. Matcher notes: `:*` ≡ trailing ` *` (end-of-pattern only, word-boundary enforced); `timeout`/`nohup`/`nice` wrappers are stripped before matching, so wrapped invocations are covered; compound commands are split and each part must match on its own.
- `argument-hint: "[what to wait for]"` — display affordance for user invocation (`/repoql:monitoring-repoql`). The command name comes from the skill directory, not `name`, for plugin subdir skills.
- `tags` dropped — parsed and ignored by Claude Code; absent from the frontmatter reference and the Agent Skills standard. Activation is driven by `description` (+ optional `when_to_use`) only. (troubleshooting-repoql still carries a dead `tags` field.)
- Considered and omitted: `when_to_use` (description already carries the triggers; the two are concatenated in the listing), `paths` (waiting on the index is not file-scoped), `disable-model-invocation`/`user-invocable` (defaults — both invocation routes are wanted), `context: fork` (the knowledge must land in the calling context), `version`/`license` (plugin.json fields, not skill fields).

## Deliberately not encoded

- The full line catalog — prose may improve between versions; `help:///commands/monitor.md` is the host-served source of truth and the skill points there.
- `--until` conditions beyond `idle` — only `idle` exists today; the skill says so rather than speculating.
