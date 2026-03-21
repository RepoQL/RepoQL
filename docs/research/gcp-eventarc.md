# Google Cloud Eventarc

Research for event-driven architecture with GCS triggers.

*Research date: March 19, 2026*

## Context

RepoQL uses an Eventarc Standard trigger (`staging-to-writer-{env}`) that listens for `google.cloud.storage.object.v1.finalized` events on the staging GCS bucket and delivers them to the `repoql-embedding-writer` Cloud Run service at `/merge`. Pub/Sub is the implicit transport.

---

## Concepts

| Concept | Description |
|---------|-------------|
| Trigger | Filters events by type + attributes → invokes destination |
| CloudEvents | CNCF standard format for all event delivery |
| Transport | Pub/Sub (implicit, auto-managed by Eventarc) |
| Delivery | At-least-once — duplicates expected |

### Standard vs Advanced

| Aspect | Standard | Advanced |
|--------|----------|---------|
| Routing | Point-to-point (trigger) | Many-to-many (bus + enrollments) |
| Max event size | 512 KB | 1 MB |
| Transformation | None | CEL expressions |
| Cross-project | No | Yes |

RepoQL uses Standard.

> [Eventarc overview](https://docs.cloud.google.com/eventarc/docs/overview)

---

## Event Sources

| Mechanism | Latency | When to use |
|-----------|---------|-------------|
| Direct events (GCS, Pub/Sub) | Seconds | Preferred when available |
| Cloud Audit Log events | Higher (log processing) | When direct events unavailable |

Google recommends direct events over Audit Log events.

### GCS Event Types

| Event | CloudEvents Type |
|-------|-----------------|
| Object finalized | `google.cloud.storage.object.v1.finalized` |
| Object deleted | `google.cloud.storage.object.v1.deleted` |
| Object archived | `google.cloud.storage.object.v1.archived` |
| Metadata updated | `google.cloud.storage.object.v1.metadataUpdated` |

> [Event providers](https://docs.cloud.google.com/eventarc/standard/docs/event-providers-targets)

---

## Destinations

| Destination | Notes |
|-------------|-------|
| Cloud Run | HTTP POST, custom path via `--destination-run-path` |
| Cloud Functions | All event-driven functions use Eventarc |
| Workflows | Events → workflow execution |
| GKE | Event forwarder pod |
| Internal HTTP | VPC-hosted services |

---

## Delivery and Retry

| Parameter | Default |
|-----------|---------|
| Min backoff | 10 seconds |
| Max backoff | 600 seconds (10 min) |
| Backoff type | Exponential |
| Message retention | 24 hours |
| Acknowledgment | 2xx = success; non-2xx = retry |

Retry settings managed via the auto-created Pub/Sub subscription. Dead-letter topics must be configured on the subscription (not the trigger directly).

If a message can't be delivered within retention (24h), it's **discarded** unless dead-letter topic is configured.

> [Retry events](https://docs.cloud.google.com/eventarc/docs/retry-events)

---

## Filtering

Triggers filter on CloudEvents attributes (AND-combined):

```bash
--event-filters="type=google.cloud.storage.object.v1.finalized"
--event-filters="bucket=my-bucket"
```

Path patterns available for granular filtering:
- `*` matches within a segment
- `**` matches across segments

RepoQL filters by type + bucket only; the handler itself filters by parsing the object path.

> [Path patterns](https://docs.cloud.google.com/eventarc/docs/path-patterns)

---

## Pricing

### Standard (RepoQL uses this)

| Component | Cost |
|-----------|------|
| Events from Google sources | **$0/million** |
| Pub/Sub transport | $0.04/million messages (10 GB/month free) |
| Events >64 KB | Billed in 64 KB chunks |

Effectively **$0** for RepoQL's volume (small GCS metadata events, ~2 KB each).

> [Eventarc pricing](https://cloud.google.com/eventarc/pricing)

---

## Quotas and Limits

| Resource | Limit |
|----------|-------|
| Triggers per location per project | 500 |
| Event size (Standard) | 512 KB |
| **GCS notifications per bucket** | **10** |
| Trigger propagation delay | Up to 2 minutes |
| Trigger read requests | 6,000/project/min |
| Trigger write requests | 600/project/min |

The **10 GCS notification limit per bucket** is the most critical constraint — each Eventarc direct-event trigger consumes one slot.

> [Quotas and limits](https://docs.cloud.google.com/eventarc/docs/quotas)

---

## Authentication

| Service Account | Role | Purpose |
|----------------|------|---------|
| Trigger SA (user-managed) | `roles/eventarc.eventReceiver`, `roles/run.invoker` | Deliver events to Cloud Run |
| GCS service agent | `roles/pubsub.publisher` | Publish GCS notifications |
| Pub/Sub SA | `roles/iam.serviceAccountTokenCreator` | Mint OIDC tokens for authenticated push |

> [Roles and permissions](https://docs.cloud.google.com/eventarc/docs/roles-permissions)

---

## Monitoring

| Surface | What it shows |
|---------|---------------|
| Pub/Sub subscription metrics | Push delivery success/failure (primary signal) |
| `oldest_unacked_message_age` | Stuck processing indicator |
| Cloud Run request logs | HTTP status codes on the handler |
| Cloud Audit Logs | Trigger create/update/delete |

No first-class delivery latency metric — must infer from GCS creation time vs Cloud Run receipt time.

> [Monitoring](https://cloud.google.com/eventarc/standard/docs/monitor)

---

## vs Alternatives

| Approach | Pros | Cons |
|----------|------|------|
| Eventarc Standard | Managed, CloudEvents, integrated IAM | No cross-project, 10 notification limit |
| Pub/Sub notifications direct | More control, cross-project | Manual subscription management |
| Cloud Functions triggers | Simplest for small handlers | ARE Eventarc triggers under the hood |
| Eventarc Advanced | Many-to-many, transformations | Higher cost, more complex |

For GCS → single Cloud Run service, Eventarc Standard is the right choice.

> [Eventarc unified eventing](https://cloud.google.com/blog/topics/developers-practitioners/eventarc-unified-eventing-experience-google-cloud)

---

## Best Practices

| Practice | Detail |
|----------|--------|
| Idempotent handlers | At-least-once means duplicates expected |
| Return 2xx for bad messages | Prevents infinite retry loops |
| Return 5xx/429 for transient errors | Triggers retry with backoff |
| Configure dead-letter topics | Capture messages exhausting retries |
| Mind the 10-notification limit | Track per-bucket notification count |
| Test with local dev path | RepoQL's direct JSON fallback enables testing without Eventarc |

---

## Known Issues

| Issue | Detail |
|-------|--------|
| Trigger propagation delay | Up to 2 min after creation |
| Update propagation lag | Old rules may apply up to 3 days after trigger update |
| Trigger SA not updatable | Must delete and recreate trigger to change SA |
| No cross-project (Standard) | Service, bucket, trigger must be in same project |

> [Known issues](https://docs.cloud.google.com/eventarc/docs/issues)

---

## Gaps

- **Delivery latency numbers**: "Seconds" for direct events, no P50/P99 published
- **Dead-letter on Eventarc-managed subscriptions**: Steps to add DLT to auto-created subscriptions not clearly documented
- **SLA specifics**: Not extracted
- **Pub/Sub throughput limits**: Eventarc defers to Pub/Sub docs

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Pattern | GCS finalized → Pub/Sub → Cloud Run at `/merge` |
| Delivery | At-least-once, idempotent handlers required |
| Pricing | Effectively $0 for Google source events |
| Key constraint | 10 GCS notifications per bucket |
| Retry | Exponential backoff, 24h retention, DLT configurable |
| RepoQL status | Working correctly; handler is idempotent via SHA256 dedup |
