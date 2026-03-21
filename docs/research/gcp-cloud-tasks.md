# Google Cloud Tasks

Research for asynchronous task execution via managed queues.

*Research date: March 19, 2026*

## Context

RepoQL optionally uses Cloud Tasks to enqueue compaction jobs from the embedding writer — HTTP POST tasks to the Cloud Run compaction endpoint when staging file count exceeds a threshold. Uses `Google.Cloud.Tasks.V2` NuGet package.

---

## Concepts

Cloud Tasks persists HTTP requests (tasks) in a queue and dispatches them reliably to workers.

| Concept | Detail |
|---------|--------|
| Queue | Named queue with rate/retry config |
| Task | HTTP request to be made later |
| Delivery | At-least-once (>99.999% execute once, but duplicates possible) |
| Ordering | No guarantee — best-effort only |
| Dead letter | Not built-in — must implement manually |

### Target Types

| Type | Endpoints | Auth | Timeout |
|------|-----------|------|---------|
| HTTP | Any HTTP endpoint (Cloud Run, GKE, external) | OIDC or OAuth | 10 min default, 30 min max |
| App Engine | App Engine handlers only | Implicit | Up to 24 hours (manual scaling) |

### Task Deduplication

Named tasks are deduplicated — names are reserved for **24 hours** after deletion. Unnamed tasks auto-generate unique IDs (no dedup).

> [Understand Cloud Tasks](https://docs.cloud.google.com/tasks/docs/dual-overview)

---

## Configuration

### Rate Limiting

| Parameter | Default |
|-----------|---------|
| `maxDispatchesPerSecond` | 500 |
| `maxConcurrentDispatches` | 1000 |
| `maxBurstSize` | 100 |

### Retry Policy

| Parameter | Default |
|-----------|---------|
| `maxAttempts` | 100 (-1 for unlimited) |
| `minBackoff` | 0.1s |
| `maxBackoff` | 3600s |
| `maxDoublings` | 16 |

### Auto-Injected Headers

`X-CloudTasks-QueueName`, `X-CloudTasks-TaskName`, `X-CloudTasks-TaskRetryCount`, `X-CloudTasks-TaskExecutionCount`, `X-CloudTasks-TaskETA`, `X-CloudTasks-TaskPreviousResponse`, `X-CloudTasks-TaskRetryReason`.

> [Configure queues](https://docs.cloud.google.com/tasks/docs/configuring-queues)
> [Retry jobs](https://docs.cloud.google.com/scheduler/docs/configuring/retry-jobs)

---

## Authentication

For Cloud Run targets, use OIDC tokens:

| Role | Granted To | Purpose |
|------|-----------|---------|
| `roles/run.invoker` | SA in OidcToken | Invoke Cloud Run service |
| `roles/iam.serviceAccountUser` | Cloud Tasks service agent | Impersonate the SA |
| `roles/cloudtasks.enqueuer` | Task-creating identity | Add tasks to queue |

```csharp
OidcToken = new OidcToken
{
    ServiceAccountEmail = "sa@project.iam.gserviceaccount.com",
    Audience = "https://service-url.run.app"
}
```

> [HTTP target auth](https://docs.cloud.google.com/tasks/docs/creating-http-target-tasks)

---

## Pricing

| Tier | Per Million Operations |
|------|----------------------|
| First 1 million/month | **Free** |
| Up to 5 billion | $0.40/million |

What counts: every API call, every push delivery attempt, `ListTasks` returning N tasks = N ops. Tasks >32KB chunked. RepoQL's compaction volume is well within free tier.

> [Cloud Tasks pricing](https://cloud.google.com/tasks/pricing)

---

## Quotas and Limits

| Resource | Limit |
|----------|-------|
| Max task size | 1 MiB (100KB body via API) |
| Queue dispatch rate | 500 tasks/sec/queue |
| Max task retention | 31 days |
| Max schedule time | 30 days ahead |
| Queue re-creation wait | 7 days after deletion |
| Max queues per region | 1,000 |

> [Quotas and limits](https://docs.cloud.google.com/tasks/docs/quotas)

---

## Monitoring

Four metrics under `cloudtasks.googleapis.com/`:

| Metric | Type | Description |
|--------|------|-------------|
| `api/request_count` | DELTA | API calls by method and response code |
| `queue/depth` | GAUGE | Tasks currently in queue |
| `queue/task_attempt_count` | DELTA | Attempts by response code |
| `queue/task_attempt_delays` | DISTRIBUTION | Delay between scheduled and actual time |

> [Observability in Cloud Tasks](https://docs.cloud.google.com/tasks/docs/monitor)

---

## vs Alternatives

| Dimension | Cloud Tasks | Pub/Sub | Cloud Scheduler |
|-----------|------------|---------|-----------------|
| Model | Explicit invocation, publisher controls target | Implicit, publisher decoupled | Cron triggers |
| Rate control | Yes (per queue) | No | N/A |
| Deduplication | Yes (task naming) | No | N/A |
| Scheduling | Up to 30 days | No | Cron |
| Multiple subscribers | No | Yes (fan-out) | No |
| Message size | 1 MiB | 10 MiB | 1 MiB |
| Ordering | Best-effort | Yes (ordering keys) | N/A |
| Dead letter | No (manual) | Yes | No |
| Pricing | $0.40/million (1M free) | $40/TiB | $0.10/job/month |

Cloud Tasks when publisher controls target and timing. Pub/Sub for fan-out and decoupling.

> [Choose Cloud Tasks or Pub/Sub](https://docs.cloud.google.com/tasks/docs/comp-pub-sub)

---

## .NET SDK (`Google.Cloud.Tasks.V2`)

```csharp
var client = CloudTasksClient.Create();
var task = new Google.Cloud.Tasks.V2.Task
{
    HttpRequest = new Google.Cloud.Tasks.V2.HttpRequest
    {
        HttpMethod = HttpMethod.Post,
        Url = compactionUrl,
        Body = ByteString.CopyFromUtf8(payload),
        Headers = { { "Content-Type", "application/json" } },
        OidcToken = new OidcToken
        {
            ServiceAccountEmail = saEmail,
            Audience = compactionUrl
        }
    },
    ScheduleTime = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(delay))
};
var response = client.CreateTask(new CreateTaskRequest { Parent = queuePath, Task = task });
```

> [Google.Cloud.Tasks.V2 reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Tasks.V2/latest)

---

## Best Practices

| Practice | Detail |
|----------|--------|
| Idempotent handlers | At-least-once means duplicates possible |
| Deterministic task names | Use for deduplication (e.g., `compact-batch-{id}`) |
| Return 4xx for permanent errors | Prevents futile retries |
| Return 2xx for unparseable messages | Prevents infinite retry loops |
| Monitor queue depth | Alert on growing backlogs |
| Don't delete+recreate queues | 7-day wait; pause/purge instead |

---

## Gaps

- **No native dead letter queue**: Must implement manually via Pub/Sub or handler logic
- **No content-based deduplication**: Only name-based
- **No execution ordering**: Design handlers to be order-independent
- **100KB task body limit**: Pass references, not full content
- **24-hour name reservation**: Can't reuse task names immediately
- **Console shows max 5,000 tasks**: Use CLI for larger queues

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Purpose | Async HTTP dispatch with rate control and retry |
| Pricing | $0.40/million ops, 1M free — negligible for RepoQL |
| Auth | OIDC tokens for Cloud Run targets |
| Dedup | Via task naming (24-hour reservation window) |
| No DLQ | Must implement dead letter handling manually |
| vs Pub/Sub | Tasks for explicit dispatch, Pub/Sub for fan-out |
