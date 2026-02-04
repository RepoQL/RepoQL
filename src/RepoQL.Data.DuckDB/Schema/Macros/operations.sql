-- Operations macros for tracking indexing batches.
-- See docs/designs/future/operations.md for design details.
--
-- Examples:
--   SELECT * FROM _operations();                                        -- All operations
--   SELECT * FROM _operations() WHERE state = 'Running';                -- Active only
--   SELECT * FROM _operation('abc123');                                 -- Single operation
--   SELECT * FROM _operation_log('abc123');                             -- Operation log
--   SELECT * FROM _operation_log('abc123') WHERE type = 'file_failed';  -- Failures only

-- Returns all operations (active and completed).
-- Columns: id, description, state, total_files, indexed_count, embedded_count, failed_count, ready_percent, created_at, completed_at
CREATE OR REPLACE MACRO _operations() AS TABLE
    SELECT
        j.value->>'Id' AS id,
        j.value->>'Description' AS description,
        j.value->>'State' AS state,
        CAST(j.value->>'TotalFiles' AS INTEGER) AS total_files,
        CAST(j.value->>'IndexedCount' AS INTEGER) AS indexed_count,
        CAST(j.value->>'EmbeddedCount' AS INTEGER) AS embedded_count,
        CAST(j.value->>'FailedCount' AS INTEGER) AS failed_count,
        CAST(j.value->>'ReadyPercent' AS INTEGER) AS ready_percent,
        j.value->>'CreatedAt' AS created_at,
        j.value->>'CompletedAt' AS completed_at
    FROM json_each(_operations_internal()) AS j
    WHERE j.type = 'OBJECT';

-- Returns a single operation by ID.
-- Columns: same as _operations()
CREATE OR REPLACE MACRO _operation(id) AS TABLE
    SELECT
        j.value->>'Id' AS id,
        j.value->>'Description' AS description,
        j.value->>'State' AS state,
        CAST(j.value->>'TotalFiles' AS INTEGER) AS total_files,
        CAST(j.value->>'IndexedCount' AS INTEGER) AS indexed_count,
        CAST(j.value->>'EmbeddedCount' AS INTEGER) AS embedded_count,
        CAST(j.value->>'FailedCount' AS INTEGER) AS failed_count,
        CAST(j.value->>'ReadyPercent' AS INTEGER) AS ready_percent,
        j.value->>'CreatedAt' AS created_at,
        j.value->>'CompletedAt' AS completed_at
    FROM json_each(_operation_internal(CAST(id AS VARCHAR))) AS j
    WHERE j.type = 'OBJECT';

-- Returns log entries for an operation.
-- Columns: timestamp, type, message, uri
CREATE OR REPLACE MACRO _operation_log(id) AS TABLE
    SELECT
        j.value->>'Timestamp' AS timestamp,
        j.value->>'Type' AS type,
        j.value->>'Message' AS message,
        j.value->>'Uri' AS uri
    FROM json_each(_operation_log_internal(CAST(id AS VARCHAR))) AS j
    WHERE j.type = 'OBJECT';
