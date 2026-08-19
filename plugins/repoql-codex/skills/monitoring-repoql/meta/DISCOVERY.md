# Discovery — monitoring-repoql

## Outcome in the world

Agents stop sleep-polling engine state or re-running queries to see whether the index is ready. One monitor call blocks or wakes them from the background exactly when the state they need is true, and failures surface as exit codes instead of being discovered later.

## Zone calibration

Knowledge primary: flag semantics, exit-code behavior, line vocabulary, and interaction footguns are facts an agent cannot derive. The recipes are freely reorderable. Constraints appear only where deviation is the failure mode, such as tool-timeout shadowing and alerts combined with conditions.

## Evidence

This skill was extracted from the implementation and a live CI pipeline using `rql monitor --exit-on 'semantic search live' --timeout 35m`. The implementation establishes that `--fail-on` wins over `--exit-on` on the same line; a bare `--timeout` watch exits successfully; `--since` replay flows through the line matcher; `--alerts` filters before condition matching; and reconnect survives host restarts.

The version floor comes from the RepoQL 1.6.18 changelog, where `--until idle`, `--exit-on`, `--fail-on`, and `--timeout` were added.

## Key insights the skill encodes

- Edge-triggered conditions need `--since` to cover the attach gap; `--until idle` is level-triggered.
- Searchable is the working bar; idle is the artifact bar.
- A harness timeout can shadow the command's own timeout.
- `--alerts` narrows what conditions can match.

## Codex adaptation

Claude-specific `allowed-tools` and `argument-hint` frontmatter are omitted. Codex controls approvals at the parent-session level and activates this skill from its name and description.
