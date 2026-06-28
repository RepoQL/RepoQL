# The `.repoql/` folder

RepoQL keeps all of its per-repository state in a `.repoql/` directory at the workspace root. Knowing this layout is what lets you find a log, spot a stale artifact, or confirm where the live database actually is — especially when the host is down and SQL/`help://` are unavailable.

`.repoql/` lives at the **workspace root** — the nearest ancestor with a `.git` repo or a `.repoql/.gitignore` marker. From a subdirectory, walk up to find it.

## Two zones

The directory is deliberately split. `RepoqlPaths` is the single source of truth in the engine; this mirrors it.

```
.repoql/                      ← ZONE 1: committable config
├── .gitignore                  presence == "this dir is a designated workspace"; contains `cache/`
├── concepts/                   captured concepts (concept:// — committable)
├── mounts.json                 import registry (gitignored despite living here)
└── cache/                     ← ZONE 2: transient runtime state (gitignored)
    ├── host_NNN.log            rolling host log — read the highest-numbered / newest
    ├── repoql.sock             gRPC domain socket the MCP bridge dials
    ├── socket.path             socket-path map (used when the real socket path is too long)
    ├── index.duckdb            the index database (+ index.duckdb.wal write-ahead log)
    ├── dashboard-bind.json     URL the live dashboard bound to (also via `dashboard_url()`)
    ├── imports/                clones of imported repos (github://…, git://…)
    └── otel/watch.duckdb       telemetry captured by `watch`
```

- **Zone 1 (`.repoql/` root)** is meant to be committed. The `.gitignore` here *is* the workspace designation marker — `rql init` (or first serve) writes it, and it excludes `cache/`. `concepts/` holds source-controlled concepts. `mounts.json` records imports (it lives at the root but is gitignored).
- **Zone 2 (`.repoql/cache/`)** is disposable. Deleting it loses the index and import clones but nothing committable; the next serve rebuilds it.

## The migration trap (read this before trusting any root file)

Old `repoql` wrote runtime files **directly into `.repoql/`** (`.repoql/host.log`, `.repoql/index.duckdb`, `.repoql/repoql.sock`, a `.repoql/diagnostics/` folder of startup JSONs, `host.stderr.log`, `host.version`). Current `rql` writes them under **`.repoql/cache/`**.

A workspace served by both versions over time will have **both** — a stale set at the root and the live set under `cache/`. The root files can be months old and will send you down the wrong path.

- **The live host log is the newest `.repoql/cache/host_*.log`** — these roll (`host_013.log`, `host_014.log`, …), so pick the highest-numbered / newest-mtime file. A bare `.repoql/host.log` at the root is almost certainly stale old-`repoql`.
- **The live database is `.repoql/cache/index.duckdb`.**
- **A root `.repoql/host.lock` is stale.** It records an old PID; current `rql` may not write one under `cache/` at all. For the live PID, read the newest `cache/host_*.log`'s `PID` line or `ps aux | grep '[r]ql serve'`.
- A `.repoql/diagnostics/*.json` folder (`socket-bind.json`, `database-init.json`, `existing-host.json`, `services-start.json`) is an **old-repoql** artifact — current `rql` does not write it. Don't cite it as current state.
- Check mtimes when in doubt: the freshest copy is the one the running host owns.

## What to read for what

| Question | Look at |
|---|---|
| Why did the host crash / fail to start? | newest `.repoql/cache/host_*.log` (tail) |
| Is a host running, and which PID? | `PID` line in newest `host_*.log`, or `ps aux \| grep '[r]ql serve'` (root `host.lock` is stale) |
| Is the socket present? | `.repoql/cache/repoql.sock` exists? |
| Which repos are imported? | `.repoql/mounts.json` (or `import list` / `Filesystems` when up) |
| How big is the index? | `.repoql/cache/index.duckdb` size |
| Is this even a workspace? | `.repoql/.gitignore` exists? |

All of these are plain files — read them with your **native file tools**, no host required. That is the whole point of knowing the layout: it is your floor when everything above the filesystem is down. See `host-wont-respond.md`.
