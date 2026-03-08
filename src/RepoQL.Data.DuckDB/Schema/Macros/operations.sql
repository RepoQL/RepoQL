-- Operations macros for tracking indexing batches.
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
        j.value->>'id' AS id,
        j.value->>'description' AS description,
        j.value->>'state' AS state,
        CAST(j.value->>'total_files' AS INTEGER) AS total_files,
        CAST(j.value->>'indexed_count' AS INTEGER) AS indexed_count,
        CAST(j.value->>'embedded_count' AS INTEGER) AS embedded_count,
        CAST(j.value->>'failed_count' AS INTEGER) AS failed_count,
        CAST(j.value->>'ready_percent' AS INTEGER) AS ready_percent,
        j.value->>'created_at' AS created_at,
        j.value->>'completed_at' AS completed_at
    FROM json_each(_operations_internal('')) AS j
    WHERE j.type = 'OBJECT';

-- Returns a single operation by ID.
-- Columns: same as _operations()
CREATE OR REPLACE MACRO _operation(id) AS TABLE
    SELECT
        j.value->>'id' AS id,
        j.value->>'description' AS description,
        j.value->>'state' AS state,
        CAST(j.value->>'total_files' AS INTEGER) AS total_files,
        CAST(j.value->>'indexed_count' AS INTEGER) AS indexed_count,
        CAST(j.value->>'embedded_count' AS INTEGER) AS embedded_count,
        CAST(j.value->>'failed_count' AS INTEGER) AS failed_count,
        CAST(j.value->>'ready_percent' AS INTEGER) AS ready_percent,
        j.value->>'created_at' AS created_at,
        j.value->>'completed_at' AS completed_at
    FROM json_each(_operation_internal(CAST(id AS VARCHAR))) AS j
    WHERE j.type = 'OBJECT';

-- Returns log entries for an operation.
-- Columns: timestamp, type, message, uri
CREATE OR REPLACE MACRO _operation_log(id) AS TABLE
    SELECT
        j.value->>'timestamp' AS timestamp,
        j.value->>'type' AS type,
        j.value->>'message' AS message,
        j.value->>'uri' AS uri
    FROM json_each(_operation_log_internal(CAST(id AS VARCHAR))) AS j
    WHERE j.type = 'OBJECT';
