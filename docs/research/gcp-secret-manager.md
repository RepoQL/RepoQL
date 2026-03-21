# Google Cloud Secret Manager

Research for managing secrets across Cloud Run services.

*Research date: March 19, 2026*

## Context

RepoQL stores API keys (Voyage AI, Grok/xAI), auth token hashes, HMAC key pairs, and Firestore project IDs in Secret Manager. Secrets are mounted into Cloud Run services as environment variables via `--set-secrets` flags. All currently use `:latest` version pinning. Infrastructure is provisioned via Pulumi.

---

## Core Concepts

A **secret** is a global (or regional) resource containing metadata plus one or more **versions**. The secret holds no payload — versions do. Versions are immutable once created.

| Version State | Meaning | Billable |
|---------------|---------|----------|
| Enabled | Active, retrievable | Yes |
| Disabled | Temporarily unavailable, can be re-enabled | Yes |
| Destroyed | Permanently gone, irrecoverable | No |

Delayed destruction can be configured — versions are scheduled for removal rather than immediately destroyed.

### Aliases

Named references to specific versions. Max 50 per secret. The built-in `latest` alias always resolves to the newest enabled version. Custom aliases cannot be named `latest` or `NEW`.

### Replication

Set at creation time and **cannot be changed** after.

| Policy | Behavior | Billing |
|--------|----------|---------|
| Automatic | Google picks regions | Charged for 1 location |
| User-managed | You pick regions | Charged per location |

Regional secrets enforce data residency within a single region.

> [Secret Manager overview](https://docs.cloud.google.com/secret-manager/docs/overview)

---

## Access Patterns

### Cloud Run Environment Variables (RepoQL's approach)

```bash
--set-secrets=ENV_VAR_NAME=SECRET_NAME:VERSION
```

Resolution timing: env vars resolve at **instance startup time**. If the secret is inaccessible, the instance fails to start.

Google recommends pinning to a **specific version number** rather than `latest` for env var mounts, because env vars only resolve once per instance lifecycle.

### Cloud Run Volume Mounts

```bash
--set-secrets=/path/to/file=SECRET_NAME:VERSION
```

Volume mounts fetch the secret value **on each read** from the filesystem — compatible with `latest` and rotation. If inaccessible at read time, the read fails (no startup check). Cannot mount at `/dev`, `/proc`, `/sys`.

### Direct API Access at Runtime

Use `SecretManagerServiceClient.AccessSecretVersion()` for full control over when and how secrets are fetched, including caching and error handling.

> [Configure secrets for services](https://docs.cloud.google.com/run/docs/configuring/services/secrets)

---

## Rotation

Secret Manager does **not** rotate values itself. It sends a `SECRET_ROTATE` notification to Pub/Sub on schedule. You implement the actual rotation logic.

| Parameter | Constraint |
|-----------|-----------|
| `rotation_period` | Minimum 1 hour (in seconds, e.g., `2592000s` = 30 days) |
| `next_rotation_time` | Must be set if period is set; minimum 5 minutes in future |

Retries failed Pub/Sub sends for up to 7 days. In-flight rotations must complete before the next starts.

Manual rotation: simply `AddSecretVersion` with the new value. Old versions can be disabled/destroyed at discretion.

> [Create rotation schedules](https://docs.cloud.google.com/secret-manager/docs/secret-rotation)

---

## Pricing

| Item | Price |
|------|-------|
| Active secret version | **$0.06** / version / location / month (prorated) |
| Access operations | **$0.03** / 10,000 operations |
| Rotation notifications | **$0.05** / rotation |
| Destroyed versions | Free |
| Management operations | Free |

### Free Tier (per billing account)

| Item | Monthly allowance |
|------|------------------|
| Active secret versions | 6 |
| Access operations | 10,000 |
| Rotation notifications | 3 |

RepoQL has ~7 secrets with automatic replication. At current scale: effectively **$0/month** (within free tier).

> [Secret Manager pricing](https://cloud.google.com/secret-manager/pricing)

---

## Quotas and Limits

| Limit | Value |
|-------|-------|
| Max version payload | **64 KiB** |
| Max aliases per secret | 50 |
| Access requests/min (per project) | 90,000 |
| Read requests/min | 600 |
| Write requests/min | 600 |
| AddSecretVersion/UpdateSecret (global, per secret) | 2 qps, 120/min |
| AddSecretVersion/UpdateSecret (regional, per secret) | 80 qps, 4,800/min |
| Max secrets per project | Not documented (no stated cap) |
| Max versions per secret | Not documented (no stated cap) |

> [Quotas and limits](https://docs.cloud.google.com/secret-manager/quotas)

---

## IAM

### Predefined Roles

| Role | ID | Key Permissions | Use Case |
|------|----|----------------|----------|
| Admin | `roles/secretmanager.admin` | All operations | Full management |
| Secret Accessor | `roles/secretmanager.secretAccessor` | `versions.access` | Read secret values only |
| Version Adder | `roles/secretmanager.secretVersionAdder` | `versions.add` | Add versions without reading existing |
| Version Manager | `roles/secretmanager.secretVersionManager` | add, destroy, disable, enable | Lifecycle management |
| Viewer | `roles/secretmanager.viewer` | View metadata (not values) | Audit, inventory |

`roles/editor` and `roles/viewer` do **not** include `secretmanager.versions.access`. Only `roles/owner` does.

### Grant Scope

Roles can be granted at secret, project, folder, or organization level. IAM Conditions support time-limited and prefix-based restrictions.

> [Access control with IAM](https://docs.cloud.google.com/secret-manager/docs/access-control)

---

## Audit Logging

| Category | Operations | Default |
|----------|-----------|---------|
| Admin Activity | Create, Add, Update, Delete, Destroy, Disable, Enable, SetIamPolicy | **Enabled** |
| Data Access (DATA_READ) | AccessSecretVersion, Get, List, GetIamPolicy | **Disabled** |

`AccessSecretVersion` (who read the secret value) is **not logged by default**. Must explicitly enable DATA_READ audit logs.

> [Secret Manager Audit Logging](https://docs.cloud.google.com/secret-manager/docs/audit-logging)

---

## vs Alternatives

| Dimension | GCP Secret Manager | HashiCorp Vault | Env vars in deploy config |
|-----------|--------------------|-----------------|--------------------------|
| Deployment | Fully managed | Self-hosted or HCP Cloud | N/A |
| Dynamic secrets | No | Yes | No |
| Automatic rotation | Notification only | Built-in for dynamic secrets | Manual |
| Multi-cloud | GCP only | Any cloud, on-prem | Platform-specific |
| Secret types | Static blobs ≤ 64 KiB | Static, dynamic, PKI, SSH | Strings |
| Access control | GCP IAM (per-secret) | Policies, LDAP, OIDC, many auth methods | Platform RBAC |
| Pricing | $0.06/version/mo | Free (OSS) or licensing | Free |
| Complexity | Low | High | Lowest |

For RepoQL: Secret Manager is the right choice — static API keys, GCP-native infrastructure, Vault would add complexity without benefit.

> [GCP Secret Manager vs HashiCorp Vault](https://infisical.com/blog/gcp-secret-manager-vs-hashicorp-vault)

---

## .NET SDK

Package: `Google.Cloud.SecretManager.V1` (v2.7.0). RepoQL does **not** use the SDK directly — all secrets are mounted as env vars by Cloud Run at deploy time.

```csharp
var client = SecretManagerServiceClient.Create();
var name = new SecretVersionName("project-id", "secret-id", "latest");
var response = client.AccessSecretVersion(name);
string payload = response.Payload.Data.ToStringUtf8();
```

> [.NET SDK reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.SecretManager.V1/latest)

---

## RepoQL's Current Secret Inventory

| Secret Name | Env Var | Used By |
|-------------|---------|---------|
| `repoql-embedding-voyage-api-key` | `Embedding__VoyageApiKey` | Embedding, Cloud service |
| `repoql-inference-grok-api-key` | `Inference__GrokApiKey` | Inference, Cloud service |
| `repoql-embedding-auth-key-hash-0` | `Auth__ApiKeyHashes__0` | Embedding, Inference, Cloud |
| `repoql-embedding-auth-key-hash-1` | `Auth__ApiKeyHashes__1` | Embedding, Inference, Cloud |
| `repoql-cloud-firestore-project` | `Firestore__ProjectId` | Cloud service |
| HMAC key ID (via vars indirection) | `REPOQL_CACHE_GCS_HMAC_KEY_ID` | Embedding writer |
| HMAC secret (via vars indirection) | `REPOQL_CACHE_GCS_HMAC_SECRET` | Embedding writer |

All use `:latest` version pinning. All service accounts have project-level `secretAccessor`.

---

## Gaps

- **`:latest` everywhere**: Google recommends version pinning for env var mounts. A bad version push affects all new instances.
- **Project-level `secretAccessor`**: Every service account can read every secret. Per-secret IAM would follow least privilege.
- **No rotation schedule**: Acceptable for now, but API keys should have a rotation plan.
- **DATA_READ audit logs likely not enabled**: No record of which service accounts accessed which secrets.
- **No destroyed version cleanup**: Old versions accumulate (negligible cost at current scale).
- **HMAC secrets via vars indirection**: Cache writer HMAC secrets referenced as `${{ vars.CACHE_WRITER_HMAC_KEY_SECRET }}` — harder to audit.

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Access pattern | Env var mounts at startup, `:latest` pinning |
| Pricing | Effectively free at current scale (7 secrets, within free tier) |
| Security gap | Project-level accessor role, not per-secret |
| Version pinning | Google recommends specific versions for env vars, not `latest` |
| Audit gap | DATA_READ logs likely not enabled |
| Rotation | No schedule configured; notification-only (you implement logic) |
