# MCP Failure Modes

How the system detects failure conditions and surfaces diagnostic information.

Connection failures are invisible by default. Detection enables action.

| Without | With |
|---------|------|
| "It's broken" | "Channel stuck in TransientFailure - restart MCP server" |
| Silent staleness | "Index 47% complete, 12 files pending" |
| Repeated 120s timeouts | "Host crashed - see stderr" |

## Trigger

Diagnostics run when:
- User executes `:diagnostics:` query
- Any tool call fails with connection error
- Health check returns unhealthy
- Proactive monitoring detects anomaly

## Diagnostics

See `diagnostics.md` for how diagnostic data is collected and presented.

Key principle: **Probes collect facts → Structured data → Formatters render output**

## Failure Modes

| Mode | Auto-recoverable? | Document |
|------|-------------------|----------|
| Host not running | ✅ Auto-launch | `host-not-running.md` |
| Host crashed | ✅ Auto-relaunch | `host-crashed.md` |
| Channel stuck | ❌ **Gap** | `channel-stuck.md` |
| Lease expired | ❌ **Gap** | `lease-expired.md` |
| Database locked | Depends | `database-locked.md` |
| Index incomplete | N/A - in progress | `index-incomplete.md` |
| WSL socket path | ✅ Path redirect | `wsl-socket-path.md` |
| Host unhealthy | Depends on cause | `host-unhealthy.md` |
| Wrong working directory | ✅ Mitigated | `wrong-working-directory.md` |

## Related

- Research: `docs/research/repoql/client-server-failure-modes.md`
- North star: `docs/north-star/reliability.md`, `docs/north-star/diagnostics.md`
- Implementation: `src/RepoQL.Protocol/RepoQlClient.cs`
