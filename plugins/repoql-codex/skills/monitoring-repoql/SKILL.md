---
name: monitoring-repoql
description: "Wait on RepoQL with one blocking command instead of sleep-and-repoll loops — rql monitor streams host progress line by line and turns index state into an exit code. Use when waiting for indexing or an import to finish, checking whether semantic search is ready or the index has settled, watching a long index build from the background, gating a CI step or script on index readiness, or asking what the host just did."
---

# Monitor RepoQL

You cannot sit and watch a stream — you act through calls that return. `rql monitor` is the waiting primitive shaped for that: it renders the host's signal feed one line per meaningful transition (imports starting, milestones landing, failures, restarts — silence when nothing changes), and its wait conditions turn a state or a line into an **exit code**. Run it foreground and block until the thing you need is true; run it in the background and let its exit be your wake-up. If you are about to `sleep` and re-poll engine state, use the monitor instead.

```text
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

`--until idle` waits for the whole engine to settle — every filesystem, every queue drained. It is the strictest bar: on a cold large repo it runs tens of minutes past searchable (full-content embeddings). Reach for it when the artifact matters, such as a cache snapshot or CI cache save, not for “can I start working”.

**Edge vs level.** `--exit-on` and `--fail-on` are edge-triggered: they match lines as they render, and a milestone that fired before you attached produced no line for you. Cover the attach gap with `--since 30s`, which replays the recent journal and lets conditions match replayed lines. `--until idle` is level-triggered: the host answers from its present state on connect, so attaching to an already-settled host exits immediately. Prefer a level condition when one exists.

## Recipes

Wait for an import to become usable:

```sh
rql import github://vendor/sdk
rql monitor --since 30s --exit-on 'vendor/sdk searchable' \
            --fail-on 'failed|stopped unexpectedly' --timeout 20m
```

Wake me when it is done; run in the background and let the process exit be the notification:

```sh
rql monitor --since 30s --exit-on 'searchable' --timeout 30m
```

Quiet sentinel during long work; it prints only failures and imports going searchable:

```sh
rql monitor --alerts
```

Replay recent host activity and then return:

```sh
rql monitor --since 15m --timeout 5s
```

CI gate:

```sh
nohup rql serve > rql-host.log 2>&1 &
rql monitor --exit-on 'semantic search live' --fail-on 'stopped unexpectedly' --timeout 35m
```

## Exit codes

- **0** — `--exit-on` matched, `--until idle` succeeded, or an unconditioned watch ended.
- **1** — `--fail-on` matched, a condition timed out, or the arguments were invalid.

## Footguns

- **Your own tool timeout kills the monitor first.** Raise the shell call timeout above the monitor timeout, or run it in the background.
- **The monitor never summons a host.** Start one first; any `rql` client command auto-launches one, while CI should use the persistent `rql serve` process shown above. Always pass `--timeout` in automation.
- **`--alerts` filters what conditions can see.** Routine milestones do not print under `--alerts`, so they cannot trip `--exit-on`.
- **Prose is not an API.** Match a stable word or two, such as `searchable`, rather than a full rendered sentence. Prefer `--until` when it covers the state you need.
- **Version floor: rql 1.6.18.** Older binaries have only `--since` and `--alerts`. Run `rql monitor --help` to inspect the installed version and `rql update` to upgrade.
- The workspace comes from the working directory, like every `rql` command. Run it at the repository you mean.

## Vocabulary for regexes

Rendered phrases worth matching include: `importing <fs> — started` · `indexing workspace — N files` · `reindexing — N files` · `index ready — N files` · `<fs> indexed|searchable|complete …` · `N failed` · `engine idle — all work settled` · `settled — all indexing work resolved` · `host idle — shut down; recovers on next use` · `host restarted — old → new; index intact` · `host stopped unexpectedly …` · `signals missed — N`.

Full reference: `help:///commands/monitor.md` or `rql monitor --help`.
