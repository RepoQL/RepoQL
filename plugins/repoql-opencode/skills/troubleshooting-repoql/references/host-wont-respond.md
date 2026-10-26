# When the host won't respond

The floor. `query`, `read`, `explore`, `command`, and every `help:///**` doc travel through the MCP→host bridge — **if the host is dark, all of them are dark.** This is the one playbook that uses *only* native file/shell tools and never assumes RepoQL works.

You're here when: a RepoQL tool call returns a connection/`Unavailable`/timeout error, and `command("host status")` errors the same way, and one retry (which should auto-relaunch the host) didn't fix it.

## 1. Read the log — natively

The host log lives under `.repoql/cache/` and **rolls**, so it's a numbered series — `host_013.log`, `host_014.log`, … There is usually no bare `host.log`; **read the highest-numbered / newest-mtime file**:

```
tail -n 100 "$(ls -t .repoql/cache/host_*.log | head -1)"
```

The cause is usually in the last 50–100 lines.

> Watch the migration trap: a bare `.repoql/host.log` at the **root** is stale old-`repoql`. The live, rolling logs are under **`cache/`**. If both exist, trust the newer mtime.

Look for: OOM / exit codes, native-library load failures, DuckDB lock errors ("could not set lock on file" → an external process holds `index.duckdb`, see `database-layer.md`), socket-bind failures.

## 2. Is a host process actually alive?

Find the live PID — **not** from a lock file. Current `rql` may not write a `host.lock` under `cache/`, and the bare `.repoql/host.lock` at the *root* is stale old-`repoql`; trusting it points you at a long-dead PID and can trick you into deleting a healthy host's socket. Get the PID from the newest log or the process list instead:

```
grep -o 'PID [0-9]*' "$(ls -t .repoql/cache/host_*.log | head -1)" | tail -1   # PID this host recorded
ps aux | grep '[r]ql serve'        # live host(s) — one per workspace; match this repo
ls -l .repoql/cache/repoql.sock    # does the socket exist?
```

- **No live `rql serve` for this workspace, socket present** → stale socket from a crash. Remove the stale `repoql.sock` under `cache/`, then make any RepoQL call to trigger relaunch. Deleting files under `cache/` is safe — it's disposable. **Never delete the socket while a matching `rql serve` is alive** — that cuts off a healthy host.
- **Process alive but unresponsive** → the host is hung, not dead. `command("host restart")` *if the bridge will take it*; otherwise kill that PID and let the next call relaunch.
- **No process, no socket** → nothing is running; the next RepoQL call should launch one. If it doesn't, the host can't start — go to step 3.

## 3. Host can't start at all

Capture the evidence and stop guessing:

- The tail of `.repoql/cache/host_*.log` (and `.repoql/cache/` for any `*.stderr`/crash files).
- Disk space (`df -h .`) and that `.repoql/cache/` is writable — a full or read-only disk stops the DB opening.
- Whether `.repoql/index.duckdb` (root, old) vs `.repoql/cache/index.duckdb` (current) is the one in play, and whether anything else has it open.

Try once: remove a stale `repoql.sock`/`host.lock` under `cache/`, then a single RepoQL call to relaunch. If it still won't come up, **escalate** — don't loop restarts.

## 4. Escalate well

```
RepoQL host won't start after <N> relaunch attempts.
Environment: <OS>, rql/plugin <version from .repoql/cache/ or `! rql --version`>
Observed:    <key line(s) from .repoql/cache/host_*.log>
Tried:       <relaunch ×N, removed stale socket, checked disk/permissions/lock holder>
Recommend:   <the specific next step the log points at>
Logs:        .repoql/cache/host_*.log
Note:        structural queries are down, but I can still read files directly.
```

## What still works with no host

Your native tools. You can read source, grep, inspect `.repoql/` artifacts, and run `! rql …` from the terminal. Structural/semantic search and `help://` are the only things truly unavailable — say so plainly rather than pretending a degraded answer is a full one.
