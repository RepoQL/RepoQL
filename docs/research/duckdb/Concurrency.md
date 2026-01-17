# DuckDB Concurrency

> MVCC, transactions, and the single-writer model

## Overview

DuckDB provides full ACID compliance through Multi-Version Concurrency Control (MVCC) while maintaining a **single-writer** concurrency model. This design prioritizes analytical query performance over multi-writer scenarios.

## Concurrency Model

### Single-Writer, Multiple-Reader

```
┌─────────────────────────────────────────────────┐
│                  DuckDB Instance                 │
├─────────────────────────────────────────────────┤
│                                                  │
│   Writer ─────┐                                  │
│               │                                  │
│               ▼                                  │
│         ┌──────────┐                             │
│         │  Data    │                             │
│         │  Store   │                             │
│         └──────────┘                             │
│               ▲                                  │
│               │                                  │
│   Reader 1 ───┤                                  │
│   Reader 2 ───┤                                  │
│   Reader 3 ───┘                                  │
│                                                  │
└─────────────────────────────────────────────────┘
```

| Operation | Concurrency |
|-----------|-------------|
| Read + Read | Parallel (unlimited) |
| Read + Write | Parallel (readers see consistent snapshot) |
| Write + Write | **Serial** (one writer at a time) |

### Why Single-Writer?

This design choice enables:

1. **Memory caching**: Data stays in RAM for fast analytical queries
2. **No write contention**: No lock management overhead
3. **Simplified architecture**: Easier to optimize and maintain
4. **Predictable performance**: No unexpected lock waits

### Design Intent

DuckDB is optimized for:
- **Bulk operations**: Large inserts/updates, not many small transactions
- **Analytical workloads**: Complex reads, infrequent writes
- **Embedded use**: Single application, not shared database

## MVCC

Multi-Version Concurrency Control allows readers and writers to operate without blocking each other.

### How MVCC Works

```
Time ────────────────────────────────────────────────▶

Transaction 1 (Reader):   [────────────────────]
                          sees v1    sees v1    sees v1

Transaction 2 (Writer):        [─────────]
                               modify → commit (v2)

Transaction 3 (Reader):              [────────────]
                                     sees v1 (started before commit)

Transaction 4 (Reader):                    [────────]
                                           sees v2 (started after commit)
```

### Version Visibility

- Each transaction sees a **consistent snapshot** of data
- Snapshot is determined at transaction start
- Changes from concurrent/later transactions are invisible
- No dirty reads, no phantom reads

### Implementation

DuckDB maintains visibility information per value:
- Tracks which transaction created each version
- Tracks which transaction deleted each version
- Transaction sees value if:
  - Created before transaction started AND
  - Not deleted (or deleted after transaction started)

## Transactions

### Basic Syntax

```sql
-- Start transaction
BEGIN TRANSACTION;
-- or just
BEGIN;

-- Commit changes
COMMIT;

-- Rollback changes
ROLLBACK;
```

### Autocommit

By default, each statement is its own transaction:

```sql
-- These are separate transactions
INSERT INTO t VALUES (1);  -- auto-commits
INSERT INTO t VALUES (2);  -- auto-commits

-- This is one transaction
BEGIN;
INSERT INTO t VALUES (1);
INSERT INTO t VALUES (2);
COMMIT;
```

### Example

```sql
CREATE TABLE accounts (id INT, balance DECIMAL(10,2));
INSERT INTO accounts VALUES (1, 1000), (2, 500);

-- Transfer $100 from account 1 to account 2
BEGIN TRANSACTION;
UPDATE accounts SET balance = balance - 100 WHERE id = 1;
UPDATE accounts SET balance = balance + 100 WHERE id = 2;
COMMIT;

-- If something goes wrong
BEGIN TRANSACTION;
UPDATE accounts SET balance = balance - 100 WHERE id = 1;
-- Oops, made a mistake
ROLLBACK;  -- Neither update is applied
```

## ACID Properties

### Atomicity

All operations in a transaction succeed or fail together:

```sql
BEGIN;
INSERT INTO orders VALUES (1, 'pending');
INSERT INTO order_items VALUES (1, 'widget', 5);
-- If any INSERT fails, entire transaction rolls back
COMMIT;
```

### Consistency

Constraints are enforced at transaction boundary:

```sql
CREATE TABLE users (
    id INTEGER PRIMARY KEY,
    email VARCHAR UNIQUE
);

BEGIN;
INSERT INTO users VALUES (1, 'alice@example.com');
INSERT INTO users VALUES (2, 'alice@example.com');  -- Constraint violation
COMMIT;  -- Transaction fails, both inserts rolled back
```

### Isolation

Transactions see consistent snapshots (Snapshot Isolation):

```sql
-- Transaction 1                    -- Transaction 2
BEGIN;                              BEGIN;
SELECT SUM(balance)                 UPDATE accounts
FROM accounts;                      SET balance = balance + 100
-- Returns 1500                     WHERE id = 1;
                                    COMMIT;
SELECT SUM(balance)
FROM accounts;
-- Still returns 1500!
-- (doesn't see T2's changes)
COMMIT;
```

### Durability

Committed transactions survive crashes:
- Write-Ahead Log (WAL) records changes before commit
- Checkpoints persist WAL to database file
- Recovery replays uncommitted WAL entries

## Handling Write Conflicts

### Optimistic Concurrency

DuckDB uses optimistic concurrency control:
1. Transactions proceed without acquiring locks
2. At commit, check for conflicts
3. If conflict, transaction aborts

### Conflict Detection

```sql
-- Transaction 1                    -- Transaction 2
BEGIN;                              BEGIN;
UPDATE users SET name = 'Alice'     UPDATE users SET name = 'Alicia'
WHERE id = 1;                       WHERE id = 1;
COMMIT;  -- Succeeds                COMMIT;  -- Fails (conflict)
```

### Retry Pattern

```python
import duckdb

def transfer_funds(from_id, to_id, amount, max_retries=3):
    for attempt in range(max_retries):
        try:
            conn.execute("BEGIN")
            conn.execute(f"UPDATE accounts SET balance = balance - {amount} WHERE id = {from_id}")
            conn.execute(f"UPDATE accounts SET balance = balance + {amount} WHERE id = {to_id}")
            conn.execute("COMMIT")
            return True
        except duckdb.TransactionException:
            conn.execute("ROLLBACK")
            if attempt == max_retries - 1:
                raise
    return False
```

## Index Considerations

### ART Index and MVCC

ART indexes interact specially with MVCC:

```sql
-- Scenario: Unique constraint with concurrent transactions

-- Transaction 1
BEGIN;
SELECT * FROM users WHERE id = 5;  -- Row exists

-- Transaction 2
BEGIN;
DELETE FROM users WHERE id = 5;
COMMIT;

-- Transaction 3
BEGIN;
INSERT INTO users (id, name) VALUES (5, 'New User');
-- May fail! ART index still holds old entry until T1 commits
COMMIT;

-- After T1 commits, index is updated
-- Now INSERT would succeed
```

### Implication

Delete visibility affects index updates:
- Index entries kept while any transaction might need old value
- This can cause temporary constraint violations
- Resolved after older transactions complete

## Best Practices

### For Writers

1. **Keep transactions short**: Long transactions block other writers
2. **Batch operations**: One large INSERT is better than many small ones
3. **Use bulk loading**: COPY command for large data loads

### For Readers

1. **Readers don't block**: No need to worry about lock contention
2. **Snapshot isolation**: Results are consistent within transaction
3. **Long reads OK**: Reading doesn't impact writers

### Architecture Patterns

| Pattern | Recommendation |
|---------|----------------|
| ETL Pipeline | Single writer process, bulk operations |
| Analytics Dashboard | Many readers, infrequent writes |
| Multi-user writes | Queue writes through single process |
| Real-time ingestion | Consider different database or batch updates |

## Configuration

### Connection Settings

```sql
-- Access mode (read-only prevents writes)
ATTACH 'mydb.duckdb' AS db (READ_ONLY);

-- Threads (affects query parallelism, not write concurrency)
SET threads = 8;
```

### File Locking

DuckDB uses file locks to enforce single-writer:
- Write operations acquire exclusive lock
- Read operations acquire shared lock
- Locks prevent multi-process write corruption

## Monitoring

### Active Transactions

```sql
-- Check for long-running transactions
SELECT * FROM duckdb_temporary_files();
```

### Checkpoint Status

```sql
-- Force checkpoint to persist changes
CHECKPOINT;

-- Check WAL size
SELECT * FROM pragma_database_size();
```
