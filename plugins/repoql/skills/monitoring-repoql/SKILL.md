---
name: monitoring-repoql
description: "Wait on RepoQL with one blocking command instead of sleep-and-repoll loops — rql monitor streams host progress line by line and turns index state into an exit code. Use when waiting for indexing or an import to finish, checking whether semantic search is ready or the index has settled, watching a long index build from the background, gating a CI step or script on index readiness, or asking what the host just did."
argument-hint: "[what to wait for]"
allowed-tools: Bash(rql monitor:*)
---

# Monitor RepoQL

You cannot sit and watch a stream — you act through calls that return. `rql monitor` is the waiting primitive shaped for that: it renders the host's signal feed one line per meaningful transition (imports starting, milestones landing, failures, restarts — silence when nothing changes), and its wait conditions turn a state or a line into an **exit code**. Run it foreground and block until the thing you need is true; run it in the background and let its exit be your wake-up. If you are about to `sleep` and re-poll `engine_status`, use the monitor instead.

```
rql monitor [--since 10m] [--alerts]                                # watch
rql monitor --until idle [--timeout 35m]                            # wait for full quiescence
rql monitor --exit-on 'regex' [--fail-on 'regex'] [--timeout 10m]   # wait for a line
```

## Pick the bar you are waiting for

Each filesystem announces three milestones, in order:

| Line | What it unlocks |
|---|---|
| `<fs> indexed — structural queries ready` | query/read — structure, symbols, grep |
| `<fs> searchable — semantic search live` | explore/explain — **usually your bar** |
| `<fs> complete — N files` | everything for that filesystem, full-text embeddings included |

`--until idle` waits for the whole engine to settle — every filesystem, every queue drained. It is the strictest bar: on a cold large repo it runs tens of minutes past searchable (full-content embeddings). Reach for it when the artifact matters (a cache snapshot, a CI cache save), not for "can I start working".

**Edge vs level.** `--exit-on`/`--fail-on` are edge-triggered: they match lines as they render, and a milestone that fired before you attached produced no line for you — cover the attach gap with `--since 30s`, which replays the recent journal and lets conditions match replayed lines. `--until idle` is level-triggered: the host answers from its current state on connect, so attaching to an already-settled host exits immediately. Prefer a level condition when one exists.

## Recipes

Wait for an import to become usable:
```
rql import github://vendor/sdk
rql monitor --since 30s --exit-on 'vendor/sdk searchable' \
            --fail-on 'failed|stopped unexpectedly' --timeout 20m
```

Wake me when it's done — run in the background; the process exiting is the notification:
```
rql monitor --since 30s --exit-on 'searchable' --timeout 30m
```

Quiet sentinel during long work — prints only failures and imports going searchable; glance at its output when curious:
```
rql monitor --alerts
```

What has the host been doing? Replay recent history, then return:
```
rql monitor --since 15m --timeout 5s     # bare timeout = bounded watch, exit 0
```

CI gate — index, then hand the next step an exit code (shape proven in a real pipeline):
```
nohup rql serve > rql-host.log 2>&1 &    # persistent host; auto-launched hosts stop themselves when idle
rql monitor --exit-on 'semantic search live' --fail-on 'stopped unexpectedly' --timeout 35m
```

## Exit codes

- **0** — `--exit-on` matched, `--until idle` satisfied, or an unconditioned watch ended (bare `--timeout`, Ctrl-C).
- **1** — `--fail-on` matched (it wins over `--exit-on` on the same line), or `--timeout` expired with a condition unmet (`timeout — condition not met after 35m`), or invalid arguments.

## Footguns

- **Your own tool timeout kills the monitor first.** A shell tool call's default limit (often 2 minutes) beats `--timeout 20m` and looks like a monitor hang. Raise the tool-call timeout above the monitor's, or run it in the background.
- **The monitor never summons a host.** No host running → it waits silently for one to appear. Start one first (any rql client command auto-launches one; CI wants the persistent `rql serve` above) and always pass `--timeout` in automation.
- **`--alerts` filters what conditions can see.** Conditions match printed lines, and under `--alerts` routine milestones don't print — the workspace going searchable will not trip `--exit-on` there. Combine `--alerts` with conditions only when the target is itself wake-worthy (a failure, an import going searchable).
- **Prose is not an API.** `--exit-on`/`--fail-on` match human-facing lines that may improve between versions. Anchor on a word or two (`searchable`), not a full sentence; prefer `--until` when it covers you. Today `--until` has one condition: `idle`.
- **Version floor: rql 1.6.18.** Older binaries have only `--since`/`--alerts`. `rql monitor --help` shows what you have; `rql update` fixes it.
- The workspace comes from the working directory, like every rql command — run it at the repo you mean.

## Vocabulary for regexes

Currently rendered phrases worth matching (verify with `rql monitor --since 10m --timeout 5s` if a match surprises you): `importing <fs> — started` · `indexing workspace — N files` · `reindexing — N files` · `index ready — N files` · `<fs> indexed|searchable|complete …` · `N failed` (plus `under <scope>/**` when a scope is known) · `engine idle — all work settled` · `settled — all indexing work resolved` (the `--until idle` success line) · `host idle — shut down; recovers on next use` · `host restarted — old → new; index intact` · `host stopped unexpectedly … host log <path>:<lines>` · `signals missed — N`.

Full reference: `help:///commands/monitor.md` (host-served, always current) or `rql monitor --help`.

---

*Pick the bar, bound the wait, and let the exit code wake you.*
