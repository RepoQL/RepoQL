# Plan: Preflight Validation

Implements: [Reliability Design](../designs/reliability.md) — Host Startup section

## Scope

**Covers:**
- Preflight validation phase before host services start
- Path validation (working directory, socket path, database path)
- Platform-specific checks (socket path length, WSL DrvFS)
- Configuration file validation (JSON parse errors)
- Environment variable validation (REPOQL_*, DUCKDB_*)
- Clear error messages with facts and guidance

**Does not cover:**
- Shutdown of existing host (Plan: Host Takeover)
- Socket binding and cleanup (Plan: Host Takeover)
- Database open and lock handling (Plan: Database Init)
- Service startup and degradation (Plan: Service Degradation)
- Exit record writing (Plan: Host Persistence)
- Client-side diagnostics (Plan: Diagnostics)

**Already mitigated:**
- Wrong working directory → REPOQL_CWD env var, primary:// URI scheme
- WSL socket path → REPOQL_SOCKET env var

## Enables

Once Preflight Validation exists:
- **Fail fast with clear errors** — no cryptic exceptions during service init
- **Platform issues detected early** — WSL DrvFS, path length limits caught upfront
- **Guidance provided** — user knows exactly what to do (set REPOQL_SOCKET, etc.)

## Prerequisites

- Host startup entry point accessible for adding preflight phase
- Environment variable handling exists (REPOQL_CWD, REPOQL_SOCKET already implemented)

## North Star

When startup fails, the error message tells you exactly what's wrong and exactly how to fix it — no stack traces, no guessing.

## Done Criteria

### Validation Phase

- The host shall run preflight validation before building services
- When any check fails, the host shall exit with clear error message
- When all checks pass, the host shall proceed to service initialization

### Working Directory Check

- The validator shall check that current directory exists
- The validator shall check for `.git/` or `.repoql/` marker
- When no marker found, report "Not a repository" with guidance to use REPOQL_CWD

### Socket Path Check

- The validator shall compute the full socket path
- The validator shall check path length against platform limit:
  - Linux: 108 characters
  - macOS: 104 characters
  - Windows: 108 characters (AF_UNIX)
- When path exceeds limit, report exact length, limit, and guidance to set REPOQL_SOCKET

### Platform Check

- On WSL, the validator shall detect DrvFS mounts (`/mnt/c/...`)
- When on DrvFS, report the issue and guidance (use WSL-native path or set REPOQL_SOCKET to `/tmp/...`)

### Database Path Check

- The validator shall check that `.repoql/` directory is writable
- When not writable, report path and permission error

### Configuration Validation

- The validator shall parse appsettings.json if present
- When JSON is malformed, report file path, line number, column, and syntax error
- The validator shall check REPOQL_* environment variables against known names
- When unknown REPOQL_* variable found, warn about possible typo
- The validator shall validate DUCKDB_* variables:
  - DUCKDB_MEMORY_LIMIT: must match pattern `\d+[KMGT]B?`
  - DUCKDB_THREADS: must be positive integer
  - DUCKDB_TEMP_DIRECTORY: must exist and be writable
- When env var has invalid format, report variable name, value, expected format, and default being used

### Error Message Format

- Each error shall follow the pattern:
  ```
  ❌ [What failed]
     [Observable facts]

     [Guidance]
  ```
- Facts shall include actual values (path, length, limit)
- Guidance shall include specific action (set ENV_VAR, run command)

## Constraints

- **No service initialization during preflight** — validation only, no side effects
- **Fast checks only** — preflight should add minimal startup latency
- **Exit on first failure** — don't collect all errors, fix one at a time

## References

- [Reliability Design](../designs/reliability.md) — error message format, preflight checks list
- [Host Failure Modes](../flows/future/host/failure-modes/) — detailed failure scenarios
- [Unix socket path limits](https://unix.stackexchange.com/questions/367008/) — platform differences

## Error Policy

Preflight errors are fatal — exit immediately with clear message. No partial startup, no degraded mode for preflight failures.
