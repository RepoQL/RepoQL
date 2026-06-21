<WHY>
It's very ineffective trying to improve something you can't measure.
Watch makes repoql act as an OTEL collector and lets you pull telemetry from any app supporting OTEL into a queryable database. RepoQL also samples the watched process's CPU and memory and emits those samples as OTEL metrics on the same run.
</WHY>

<WHEN_TO_USE>
- When you have an app you want to observe without spending tokens on reading all the output.
- When profiling an app as part of optimization.
- To diagnose issues, exceptions, crashes, hangs, or slow startup.
- When you need evidence you can query repeatedly while the process is still running.
</WHEN_TO_USE>

<ENVIRONMENT>
Pass extra environment variables with `environment`, formatted as `key=value;key2=value2`. The process inherits the host environment first, then your overrides, then RepoQL's OTLP exporter variables (which always win, so capture cannot be broken). `%VAR%` references are expanded; escape a literal `;` or `=` with `\`. Use this to enable an app's own telemetry — e.g. `CLAUDE_CODE_ENABLE_TELEMETRY=1;OTEL_LOG_TOOL_DETAILS=1` for Claude Code.
</ENVIRONMENT>

<LIFECYCLE>
A watch call returns immediately with a `run_id` and a `telemetry_schema` — the process keeps running in the background while you query its telemetry. Substitute `telemetry_schema` for `<schema>` in the SQL below.

The run ends when the underlying process exits; `<schema>.watch_run.completed_at` flips from NULL once it has. There is no MCP-callable stop — if you need to terminate early, kill the process at its source. Restarting the host (`command(command="host restart")`) disposes every active run.
</LIFECYCLE>

<GUIDANCE>
Check whether telemetry is arriving:

```sql
SELECT signal, transport, protocol, COUNT(*) AS payloads, SUM(payload_size_bytes) AS payload_bytes
FROM <schema>.payload
WHERE run_id = '<run_id>'
GROUP BY signal, transport, protocol
ORDER BY signal, transport, protocol;
```

Check whether the process has exited:

```sql
SELECT run_id, started_at, completed_at, exit_code
FROM <schema>.watch_run
WHERE run_id = '<run_id>';
```

Find slow spans:

```sql
SELECT name,
       kind,
       (end_time_unix_nano - start_time_unix_nano) / 1000000.0 AS duration_ms,
       status_code
FROM <schema>.span
WHERE end_time_unix_nano IS NOT NULL
ORDER BY duration_ms DESC
LIMIT 20;
```

Read recent logs:

```sql
SELECT observed_time_unix_nano, severity_text, body_string
FROM <schema>.log_record
ORDER BY observed_time_unix_nano DESC
LIMIT 50;
```

Discover metrics:

```sql
USE <schema>;

SELECT name, type, unit, point_count, services, query_hint
FROM available_metrics()
ORDER BY name;
```

Inspect process resource metrics:

```sql
SELECT metric_name, MAX(numeric_value) AS max_value, metric_unit
FROM <schema>.otel_metric_point
WHERE run_id = '<run_id>'
  AND scope_name = 'RepoQL.Hosting.Otel.WatchProcessResourceTelemetry'
  AND metric_name IN ('process.memory.usage', 'repoql.watch.process.cpu.time', 'repoql.watch.process.tree.memory.usage')
GROUP BY metric_name, metric_unit
ORDER BY metric_name;
```
</GUIDANCE>

Full reference: `help:///tools/watch/watch.md`
Watching Claude Code (env knobs, agent-loop queries): `help:///tools/watch/watching-claude.md`
