# Database Migration

Run migrations against production. Order matters—schema changes must precede code deployment.

## Prerequisites

- Production database credentials in environment
- Backup completed within last hour
- No active deployments in progress

## Steps

### 1. Enable maintenance mode

→ Verify: Public endpoints return 503
→ If failed: Check load balancer configuration

### 2. Create migration snapshot

→ Verify: Snapshot appears in backup list
→ If failed: DO NOT PROCEED. Investigate storage.

### 3. Run migrations

```
./scripts/migrate.sh production
```

→ Verify: Output shows "All migrations complete"
→ If failed: Check output for specific migration. See Recovery.

### 4. Run smoke tests

```
./scripts/smoke-test.sh production
```

→ Verify: All tests pass
→ If failed: Check which tests failed. If data integrity, see Recovery.

### 5. Disable maintenance mode

→ Verify: Public endpoints return 200
→ If failed: Check load balancer configuration

## Completion

- All migrations applied
- Smoke tests passing
- Production serving traffic normally

## Recovery

If migration fails partway:

1. DO NOT run migrations again
2. Identify the failed migration from output
3. Restore from snapshot created in step 2
4. Investigate the failure before retrying

If smoke tests fail with data integrity issues:

1. Enable maintenance mode immediately
2. Restore from snapshot
3. Escalate to database team
