# Plan: Database Init

Implements: [Reliability Design](../designs/reliability.md) — Host Startup section

## Scope

**Covers:**
- Database file opening with lock handling
- Lock holder identification
- Corruption detection and recovery
- Temp directory validation
- DuckDB configuration validation

**Does not cover:**
- Preflight path validation (Plan: Preflight Validation)
- Socket binding (Plan: Host Takeover)
- Service startup (Plan: Service Degradation)

## Enables

Once Database Init exists:
- **Lock conflicts diagnosed** — user knows exactly what process holds the lock
- **Zombie repoql processes killed** — auto-recovery when we hold our own lock
- **Corruption handled gracefully** — rebuild instead of crash
- **Temp directory issues caught** — DuckDB needs writable temp space

## Prerequisites

- `.repoql/` directory exists and is writable (from preflight)
- Socket successfully bound (from Host Takeover)

## North Star

When database open fails, the error message shows exactly what's wrong: lock holder process name and PID, corruption details, or temp directory issue — not just "database locked."

## Done Criteria

### Database Open

- The host shall attempt to open `.repoql/index.duckdb` with exclusive access
- When open succeeds, proceed to schema validation
- When open fails with lock error, identify lock holder
- When open fails with corruption error, attempt recovery

### Lock Holder Identification

- When database locked, identify the process holding the lock
- On Windows, use `Restart Manager` API or file handle enumeration
- On Unix, use `lsof` or `/proc/*/fd` scanning
- Report lock holder as "PID {pid} ({process_name})"

### Lock Classification

- When lock holder is repoql process, classify as "zombie repoql"
  - Attempt to kill the zombie process
  - Retry database open after kill
- When lock holder is external process (DBeaver, DataGrip, etc.), classify as "external tool"
  - Report process name and guidance to close it
- When lock holder cannot be identified, report "unknown process"

### Corruption Recovery

- When DuckDB reports corruption, log the error details
- Delete the corrupted database file
- Create fresh database with schema
- Log "Database rebuilt due to corruption"
- **Only attempt rebuild once** — if fresh database also fails, exit with error
- Proceed with startup (index will rebuild)

### Temp Directory

- Check that DuckDB temp directory exists and is writable
- When DUCKDB_TEMP_DIRECTORY set, validate that path
- When temp directory invalid, report path and issue
- Default temp directory: `.repoql/temp/`

### Schema Validation

- After successful open, validate schema version
- When schema outdated, run migrations
- When migration fails, delete database and recreate from scratch
- When schema newer than code (downgrade), delete database and recreate
- When schema validation fails for any reason, delete and recreate
- Log "Database recreated due to schema incompatibility"
- **Only attempt recreation once** — if fresh database also fails, exit with error
- Don't loop: if it didn't work the first time, there's a fundamental problem
- Index will rebuild from source files - this is a cache, not source of truth

## Constraints

- **Don't delete user data without corruption** — only delete database when DuckDB itself reports corruption
- **Identify before killing** — only kill processes identified as repoql
- **Temp directory must be local** — network paths cause DuckDB issues

## References

- [Reliability Design](../designs/reliability.md) — database state in diagnostics
- [Database Init Flow](../flows/future/host/failure-modes/database-init.md) — detailed scenarios
- [DuckDB Documentation](https://duckdb.org/docs/) — lock behavior, temp directory

## Error Policy

Database errors are fatal for startup:
1. Identify the specific issue (lock, corruption, permissions)
2. Attempt recovery if possible (kill zombie, rebuild corrupted)
3. If unrecoverable, report clear error and exit
