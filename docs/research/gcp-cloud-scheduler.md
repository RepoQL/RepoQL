# Google Cloud Scheduler

Research for managed cron job scheduling on Google Cloud.

*Research date: March 19, 2026*

## Context

RepoQL uses a scheduler job (`embedding-compaction-{env}`) that triggers nightly compaction at midnight UTC, sending an HTTP POST with JSON payload to the Cloud Run compaction endpoint. Provisioned via Pulumi.

---

## Concepts

Cloud Scheduler is a fully managed cron job scheduler. It creates **jobs** that fire on a recurring schedule, sending a request to a **target**.

| Concept | Detail |
|---------|--------|
| Job | Named unit of scheduled work — schedule + target + retry/auth config |
| Schedule | Unix cron format. Minimum granularity: **1 minute** |
| Time zone | Per-job (e.g., `Etc/UTC`) |
| Pause/Resume | Jobs can be paused and resumed without deletion |
| Delivery | **At least once** — duplicates possible, targets must be idempotent |
| Scope | Regional resources |

> [Cloud Scheduler overview](https://docs.cloud.google.com/scheduler/docs/overview)

---

## Targets

| Target Type | Configuration | Use Case |
|-------------|---------------|----------|
| HTTP/S | URI, method, headers, body, OIDC or OAuth auth | Cloud Run, Cloud Functions, any HTTP endpoint |
| Pub/Sub | Topic name, message body, attributes | Fan-out, decoupled architectures |
| App Engine | Service, version, handler path | Legacy App Engine handlers |

HTTP body is sent for POST, PUT, PATCH only. In REST API and Pulumi/Terraform, body must be **base64-encoded**. gcloud CLI accepts plaintext.

`attempt_deadline` sets the timeout for each execution attempt — if the handler doesn't respond in time, the request is cancelled and marked failed.

> [Creating jobs](https://docs.cloud.google.com/scheduler/docs/creating)

---

## Retry Policies

Retries use exponential backoff. All parameters configurable per job.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `retryCount` | **0** | Max retry attempts (max 5). 0 = no retries |
| `maxRetryDuration` | 0s (unlimited) | Total wall-clock time for all retries |
| `minBackoffDuration` | 5s | Initial wait before first retry |
| `maxBackoffDuration` | 3600s | Maximum wait between retries |
| `maxDoublings` | 5 | Times backoff doubles before switching to linear |

Default `retryCount` of 0 means **no retries** — any transient failure results in a missed execution until the next scheduled run.

> [Retry jobs](https://docs.cloud.google.com/scheduler/docs/configuring/retry-jobs)

---

## Authentication

| Token Type | When to Use |
|------------|-------------|
| OIDC | Cloud Run, Cloud Functions, custom services that validate ID tokens |
| OAuth | Google APIs on `*.googleapis.com` |

### OIDC for Cloud Run

1. Create a dedicated service account
2. Grant `roles/run.invoker` on the Cloud Run service to that SA
3. Configure `--oidc-service-account-email` and `--oidc-token-audience` (service URL without query params)
4. Caller needs `iam.serviceAccounts.actAs` on the SA

Never use the Cloud Scheduler service agent as the OIDC service account.

> [HTTP target auth](https://docs.cloud.google.com/scheduler/docs/http-target-auth)
> [Running services on a schedule](https://docs.cloud.google.com/run/docs/triggering/using-scheduler)

---

## Pricing

| Item | Cost |
|------|------|
| Free tier | **3 jobs/month** per billing account |
| Per job | **$0.10/month** (prorated daily) |
| Per execution | **No charge** |
| Paused jobs | Still billed |

Executions are included — a job running 1000 times/day costs the same $0.10/month. RepoQL's single job per environment fits within the free tier.

> [Cloud Scheduler pricing](https://cloud.google.com/scheduler/pricing)

---

## Quotas and Limits

| Resource | Default | Adjustable |
|----------|---------|------------|
| Jobs per region per project | 1,000 (max 5,000) | Yes |
| Read API requests/min | 1,250 | Yes |
| Write API requests/min | 500 | Yes |
| Max job payload size | **1 MB** | Yes |
| Min schedule interval | **1 minute** | No |
| Max execution duration (HTTP) | **30 minutes** | No |
| Max retry count | **5** | No |

The 30-minute HTTP timeout is a hard system limit. For longer work, the endpoint should enqueue work (e.g., to Cloud Tasks) and return immediately.

> [Quotas and limits](https://docs.cloud.google.com/scheduler/quotas)

---

## Monitoring

| Capability | Detail |
|------------|--------|
| Execution logs | Log entry at start and end of each execution in Cloud Logging |
| Console UI | Job list with last run status; "View logs" opens filtered Logs Explorer |
| Audit logs | Admin Activity under `cloudscheduler.googleapis.com` |
| Cloud Monitoring | Metrics under `cloudscheduler.googleapis.com/` for dashboards and alerting |

No built-in execution history table — must use Cloud Logging for historical results.

> [Viewing logs](https://docs.cloud.google.com/scheduler/docs/viewing-logs)

---

## vs Alternatives

| Dimension | Cloud Scheduler | Cron on VM | Cloud Tasks | Workflows |
|-----------|----------------|-----------|-------------|-----------|
| What | Managed cron | Self-managed crontab | Managed task queue | Orchestrated multi-step |
| Schedule | Recurring (cron) | Recurring (cron) | One-time future (up to 30 days) | Recurring via Scheduler |
| Min interval | 1 minute | No limit | N/A | 1 minute |
| Managed | Fully | No | Fully | Fully |
| Retry | Exponential backoff (max 5) | DIY | Configurable rate/retry | Workflow error handling |
| Pricing | $0.10/job/month | VM cost | $0.40/million tasks | $0.01/1K steps |
| Best for | Periodic triggers | Complex scripting | Async work dispatch | Multi-step orchestration |

Cloud Scheduler triggers work at a time. Cloud Tasks dispatches work items to a queue. They're complementary — Scheduler often triggers Tasks for fan-out.

> [Cloud Tasks vs Cloud Scheduler](https://docs.cloud.google.com/tasks/docs/comp-tasks-sched)

---

## Endpoint Design Best Practices

| Practice | Detail |
|----------|--------|
| Idempotent handlers | At-least-once delivery means duplicates possible |
| Set retryCount > 0 | Default 0 means transient failures = missed execution |
| Match attempt_deadline to target timeout | Mismatch causes false failure reports |
| Use OIDC for Cloud Run | Prevent unauthorized invocation |
| Acknowledge bad messages with 2xx | Non-2xx for unparseable payload causes infinite retries |
| 30-minute limit | For longer work, enqueue and return immediately |
| Monitor missed executions | If AttemptFinished log is missing, subsequent executions block |

> [Troubleshooting](https://docs.cloud.google.com/scheduler/docs/troubleshooting)

---

## Gaps

- **No OIDC auth configured**: RepoQL's Pulumi code creates the HTTP target without an `OidcToken`. The compaction endpoint doesn't appear to enforce auth either.
- **No retry policy configured**: Uses default (0 retries). Any transient failure = missed nightly compaction.
- **Execution blocking semantics**: If an execution doesn't finish before the next scheduled run, pending executions are blocked. Exact queuing/dropping behavior not fully documented.
- **No multi-region failover**: Scheduler jobs are regional. No cross-region redundancy documented.
- **Exact Cloud Monitoring metric names**: Not extracted; prefix is `cloudscheduler.googleapis.com/`.

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Schedule | Cron syntax, min 1-minute granularity, at-least-once delivery |
| Pricing | $0.10/job/month, 3 free — effectively free for RepoQL |
| Auth gap | No OIDC configured on compaction job |
| Retry gap | Default 0 retries — transient failures = missed execution |
| Hard limit | 30-minute HTTP timeout |
| Idempotency | Required — RepoQL's compaction uses GCS locks correctly |
