# Google Cloud IAM & Workload Identity Federation

Research for identity, access management, and keyless CI/CD authentication.

*Research date: March 19, 2026*

## Context

RepoQL uses 4 service accounts per environment (embedding-service, cache-writer, compaction, cloud-service) with granular per-resource IAM bindings. GitHub Actions authenticates via Workload Identity Federation (OIDC) without service account keys. Infrastructure managed via Pulumi. WIF SA lives in `repoql-production` and operates cross-project.

---

## IAM Core Concepts

### Policy Types (evaluation order)

| Policy | Order | Purpose |
|--------|-------|---------|
| Principal Access Boundary (PAB) | 1st | Limits which resources a principal can access |
| Deny policies | 2nd | Blocks specific permissions regardless of allow |
| Allow policies | 3rd | Grants permissions via role bindings |

### Role Types

| Type | Description | Maintenance |
|------|-------------|-------------|
| Basic (Owner/Editor/Viewer) | Broad, project-wide, legacy | Google-managed |
| Predefined | Granular, per-service | Google-managed, auto-updated |
| Custom | User-defined permission sets | Self-maintained |

### IAM Conditions

CEL-based attribute conditions on role bindings. Support time-limited access, resource-type restrictions, tag-based access. Cannot apply to basic roles or `allUsers`.

> [IAM Overview](https://docs.cloud.google.com/iam/docs/overview)
> [Conditions](https://docs.cloud.google.com/iam/docs/conditions-overview)

---

## Service Accounts

### Credential Types (by security)

| Method | Lifetime | Persistence | Security |
|--------|----------|-------------|----------|
| Workload Identity Federation | 1 hour (auto-expiring) | None | Best |
| Impersonation | 1 hour (configurable to 12h) | None | Very good |
| Attached SA (on GCP) | Auto-refreshed | None | Good |
| SA key JSON | Until deleted | On disk | **Worst — avoid** |

### Impersonation

Requires `roles/iam.serviceAccountTokenCreator` on the target SA. Generates OAuth2 access tokens (max 1 hour, extendable to 12h with org policy), OIDC ID tokens, self-signed JWTs, or signed blobs.

> [Service accounts](https://docs.cloud.google.com/iam/docs/service-account-overview)
> [Best practices](https://docs.cloud.google.com/iam/docs/best-practices-service-accounts)

---

## Workload Identity Federation

### How It Works

1. External workload obtains token from its IdP (e.g., GitHub OIDC)
2. Token exchanged at Google STS for federated token
3. Federated token either accesses resources directly or impersonates a SA

### Components

| Component | Description |
|-----------|-------------|
| Pool | Logical grouping of external identities (project-level, global) |
| Provider | Config for one IdP (issuer URI, attribute mappings, conditions) |
| Attribute Mapping | Maps external claims to STS attributes (min: `google.subject=assertion.sub`) |
| Attribute Condition | CEL expression restricting which identities can authenticate |

### Two Approaches

| Approach | Token Lifetime | Limitations |
|----------|---------------|-------------|
| Direct WIF | 10 minutes | Not all services support `principalSet` |
| Via SA impersonation | 1 hour | Requires intermediate SA |

RepoQL uses SA impersonation.

### Security Guidance

- **Always use attribute conditions** — restrict by `repository_owner` minimum
- **Prefer numeric `*_id` fields** — names can be recycled (cybersquatting)
- **One provider per pool** — prevents subject collisions
- **Enable Data Access audit logs** on STS and IAM APIs

> [Workload Identity Federation](https://docs.cloud.google.com/iam/docs/workload-identity-federation)
> [WIF best practices](https://cloud.google.com/iam/docs/best-practices-for-using-workload-identity-federation)

---

## GitHub Actions Integration

### `google-github-actions/auth@v2`

```yaml
permissions:
  id-token: write  # Required for OIDC token generation

- uses: google-github-actions/auth@v2
  with:
    workload_identity_provider: ${{ secrets.GCP_WIF_PROVIDER }}
    service_account: ${{ secrets.GCP_SERVICE_ACCOUNT }}
    token_format: 'access_token'
```

Propagation delay: ~5 minutes after creating pools/providers/permissions before first use.

> [google-github-actions/auth](https://github.com/google-github-actions/auth)

---

## Predefined Roles Used by RepoQL

### Per Service

| Service | Key Roles |
|---------|-----------|
| Cloud Run | `run.admin` (deploy), `run.invoker` (service-to-service) |
| Cloud Storage | `storage.objectViewer`, `storage.objectCreator`, `storage.objectAdmin` |
| Secret Manager | `secretmanager.secretAccessor`, `secretmanager.admin` (Pulumi) |
| Firestore | `datastore.user` (read/write), `datastore.owner` (Pulumi) |
| Cloud Trace | `cloudtrace.agent` (write traces) |
| Monitoring | `monitoring.metricWriter` (write metrics), `monitoring.admin` (dashboards) |
| Pub/Sub | `pubsub.publisher` (GCS → Eventarc) |
| Eventarc | `eventarc.admin` (create triggers), `eventarc.eventReceiver` |
| IAM | `iam.serviceAccountUser`, `iam.serviceAccountTokenCreator`, `iam.workloadIdentityUser` |

### Custom Roles

Not used. At RepoQL's scale with granular predefined roles and bucket-scoped bindings, custom roles would add maintenance burden without meaningful security benefit. Custom roles require monitoring the permissions change log for service evolution.

Limits: 300 per project, 300 per org, 3,000 permissions per role.

> [Choose role type](https://docs.cloud.google.com/iam/docs/choose-role-type)

---

## Pricing

| Component | Cost |
|-----------|------|
| IAM API | **Free** |
| Workload Identity Federation | **Free** |
| Service accounts | **Free** |
| Custom roles | **Free** |
| Admin Activity audit logs | **Free** (always on) |
| Data Access audit logs | First 50 GiB/month free, then $0.50/GiB |

IAM and WIF cost nothing. Only potential cost is Data Access audit logs.

> [IAM pricing](https://cloud.google.com/iam/pricing)

---

## Quotas and Limits

| Resource | Limit |
|----------|-------|
| Service accounts per project | 100 (increasable) |
| SA keys per account | 10 |
| Principals per allow policy | 1,500 |
| Custom roles per project | 300 |
| Permissions per custom role | 3,000 |
| Access token max lifetime | 3,600s (12h with org policy) |
| IAM v1 write requests | 600/project/min |

> [IAM quotas](https://docs.cloud.google.com/iam/quotas)

---

## Cross-Project Access

RepoQL's WIF SA lives in `repoql-production` and accesses resources in both `repoql-dev` and `repoql-production`. The bootstrap script grants explicit roles in each target project.

| Rule | Detail |
|------|--------|
| Default | Cannot attach SA from one project to another's resources |
| Org policy | `iam.disableCrossProjectServiceAccountUsage` must be disabled |
| IAM bindings | SA from Project A can be granted roles on Project B resources |

> [Cross-project SA](https://docs.cloud.google.com/iam/docs/attach-service-accounts)

---

## Audit Logging

| Log Type | Default | Cost |
|----------|---------|------|
| Admin Activity | Always on | Free |
| System Event | Always on | Free |
| Policy Denied | Always on | Free |
| Data Access | **Off** (except BigQuery) | $0.50/GiB after 50 GiB/month |

Admin Activity logs retain 400 days. Data Access logs 30 days (default bucket).

> [Audit Logs](https://docs.cloud.google.com/logging/docs/audit)

---

## RepoQL Service Account Matrix

| SA | Cloud Run Service | Key Permissions |
|----|-------------------|-----------------|
| `embedding-service-{env}` | repoql-embedding | GCS objectViewer (embeddings), objectCreator (staging), secretAccessor, eventarc.eventReceiver, datastore.user, cloudtrace.agent |
| `cache-writer-{env}` | repoql-embedding-writer | GCS objectAdmin (both), secretAccessor, cloudtrace.agent |
| `compaction-{env}` | (scheduler-triggered) | GCS objectAdmin (embeddings) |
| `cloud-service-{env}` | repoql-cloud | GCS objectViewer (embeddings), objectCreator (staging), secretAccessor, datastore.user, cloudtrace.agent |
| `github-actions-sa` | N/A (deploy-time) | 12 project-level admin roles for Pulumi + deploys |

---

## Security Best Practices

| Practice | Detail |
|----------|--------|
| WIF over SA keys | No persistent credentials (RepoQL does this) |
| Resource-scoped bindings | Per-bucket, not project-wide (RepoQL does this) |
| One SA per workload | Limits blast radius (RepoQL has 4 per env) |
| Attribute conditions on WIF | Restrict by repository_owner minimum |
| Separate deploy from runtime SAs | Deploy SA ≠ runtime SAs (RepoQL does this) |
| Rotate HMAC keys | Long-lived credentials needing periodic rotation |

---

## Gaps

- **`monitoring.metricWriter` not granted**: Pulumi grants `cloudtrace.agent` but not `monitoring.metricWriter`. If OTLP metrics are exported (not just traces), this role is needed.
- **Inference service SA not in Pulumi**: Creation/IAM not visible in `Program.cs` — may be managed separately.
- **WIF attribute condition not verifiable**: Stored in GitHub secrets, not in codebase. Should restrict to `stueeey/RepoQL` at minimum.
- **HMAC keys are long-lived**: Persistent credentials stored in Secret Manager — rotation plan needed.
- **WIF SA has broad admin roles**: 12 project-level admin roles. Consider deny policies or conditional bindings to tighten.
- **Compaction SA has no `secretAccessor`**: May need HMAC secrets but doesn't have access — needs investigation.

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Auth model | WIF for CI/CD (keyless), attached SAs for Cloud Run (ADC) |
| IAM approach | Predefined roles, bucket-scoped bindings, no custom roles |
| Pricing | IAM and WIF are completely free |
| Service accounts | 4 per environment + 1 WIF deploy SA |
| Key gap | `monitoring.metricWriter` not granted for OTLP metrics export |
| Security | Good separation of deploy vs runtime SAs; HMAC keys are the main long-lived credential |
