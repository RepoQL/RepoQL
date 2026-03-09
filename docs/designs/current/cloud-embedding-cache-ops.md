# Cloud Embedding Cache Operations Guide

How to deploy, configure, troubleshoot, and operate the cloud embedding cache infrastructure.

## Environments

| Environment | GCP Project | Pulumi Stack | Cloud Run Region |
|-------------|-------------|--------------|------------------|
| dev | `repoql-dev` | `stueeey/dev` | `us-central1` |
| prod | `repoql-production` | `stueeey/prod` | `us-central1` |

Both share one WIF service account: `github-actions-sa@repoql-production.iam.gserviceaccount.com` (lives in prod, operates cross-project).

---

## Services

| Service | Cloud Run Name | Protocol | Auth | Scales to Zero |
|---------|---------------|----------|------|----------------|
| Embedding service | `repoql-embedding` | HTTP/2 gRPC | Bearer token (API key → SHA-256 hash) | Yes |
| Writer | `repoql-embedding-writer` | HTTP/1.1 REST | Eventarc (no-allow-unauthenticated) | Yes |
| Compaction | (Cloud Run job, not yet deployed) | HTTP/1.1 REST | Cloud Scheduler | N/A |

### Service Accounts

| Account | Purpose |
|---------|---------|
| `embedding-service-{env}` | Reads embeddings bucket, writes staging bucket, Eventarc trigger identity |
| `cache-writer-{env}` | Reads staging, writes embeddings, deletes staging |
| `compaction-{env}` | Reads/writes embeddings |

Each has its own HMAC keys for DuckDB GCS access, stored in Secret Manager.

---

## Deploy Workflows

All workflows are manual dispatch (`workflow_dispatch`). Run from GitHub Actions or CLI.

### 1. Infrastructure (Pulumi)

```bash
gh workflow run deploy-infra.yml --field environment=dev
gh workflow run deploy-infra.yml --field environment=prod
```

Creates: GCS buckets, service accounts, HMAC keys, Secret Manager secrets, IAM bindings, Cloud Scheduler job, monitoring dashboard, Artifact Registry repo.

**Prerequisites:** Run `bootstrap.sh` once per project first (see Bootstrap section).

### 2. Embedding Service

```bash
gh workflow run deploy-embedding.yml --field environment=dev --field enable_cache=true
gh workflow run deploy-embedding.yml --field environment=prod --field enable_cache=true
```

The `enable_cache` flag adds a second `gcloud run services update` step that sets cache-specific env vars and secrets. Without it, the service deploys as a direct Voyage relay.

### 3. Writer + Eventarc Trigger

```bash
gh workflow run deploy-embedding-writer.yml --field environment=dev
gh workflow run deploy-embedding-writer.yml --field environment=prod
```

Creates the Eventarc trigger (`staging-to-writer-{env}`) on first deploy. Subsequent deploys detect the existing trigger and skip creation.

**Important:** The Eventarc trigger location must match the bucket location. Staging buckets are `US` multi-region → trigger location is `us` (set via `CACHE_BUCKET_LOCATION` GitHub environment variable, defaults to `us`).

---

## Bootstrap

Run once per GCP project before any Pulumi or deploy workflow can succeed. Requires your personal credentials (not the WIF SA).

```bash
cd infra/cloud-cache
./bootstrap.sh dev repoql-dev
./bootstrap.sh prod repoql-production
```

What it does:
1. Enables required GCP APIs (Artifact Registry, Cloud Run, Eventarc, Secret Manager, etc.)
2. Grants the WIF SA project-level roles needed for Pulumi and deploys
3. Activates the GCS service agent (for Eventarc Pub/Sub notifications)
4. Activates the Eventarc service agent (created lazily by GCP — must be force-provisioned)

**PowerShell gotcha:** The script has Unix line endings. If running from PowerShell, either use `bash bootstrap.sh dev repoql-dev` or convert line endings first. PowerShell's `echo -n` is `Write-Output` (not bash echo) — it adds `\r\r\n` trailing bytes. Use `printf` for clean secret values.

### WIF SA Roles

The full set of roles the WIF SA needs (granted by bootstrap):

| Role | Why |
|------|-----|
| `roles/artifactregistry.admin` | Create repos + push container images |
| `roles/cloudscheduler.admin` | Create Cloud Scheduler jobs via Pulumi |
| `roles/eventarc.admin` | Create Eventarc triggers |
| `roles/iam.serviceAccountAdmin` | Create service accounts via Pulumi |
| `roles/iam.serviceAccountUser` | Act as Cloud Run service accounts |
| `roles/monitoring.admin` | Create/update dashboards |
| `roles/resourcemanager.projectIamAdmin` | Manage IAM bindings via Pulumi |
| `roles/run.admin` | Deploy Cloud Run services |
| `roles/secretmanager.admin` | Create secrets via Pulumi |
| `roles/storage.admin` | Manage buckets + bucket-level IAM via Pulumi |
| `roles/storage.hmacKeyAdmin` | Create HMAC keys for service accounts |

---

## Secrets

### Per-Environment Secrets (GitHub → Settings → Secrets)

| Secret | Description |
|--------|-------------|
| `GCP_WIF_PROVIDER` | Workload Identity Federation provider resource name |
| `GCP_SERVICE_ACCOUNT` | WIF service account email |
| `PULUMI_ACCESS_TOKEN` | Pulumi Cloud access token |

### Per-Environment Variables (GitHub → Settings → Variables)

| Variable | Example (dev) | Example (prod) |
|----------|---------------|----------------|
| `GCP_PROJECT_ID` | `repoql-dev` | `repoql-production` |
| `CACHE_EMBEDDINGS_BUCKET` | `repoql-embeddings-dev` | `repoql-embeddings-prod` |
| `CACHE_STAGING_BUCKET` | `repoql-staging-dev` | `repoql-staging-prod` |
| `CACHE_BUCKET_LOCATION` | `us` | `us` |
| `CACHE_EMBEDDING_HMAC_KEY_SECRET` | `repoql-embedding-service-hmac-access-key-id-dev` | `...-prod` |
| `CACHE_EMBEDDING_HMAC_SECRET_SECRET` | `repoql-embedding-service-hmac-secret-dev` | `...-prod` |
| `CACHE_WRITER_HMAC_KEY_SECRET` | `repoql-cache-writer-hmac-access-key-id-dev` | `...-prod` |
| `CACHE_WRITER_HMAC_SECRET_SECRET` | `repoql-cache-writer-hmac-secret-dev` | `...-prod` |

### GCP Secret Manager Secrets (created manually)

| Secret | How to create |
|--------|---------------|
| `repoql-embedding-voyage-api-key` | `printf 'pa-...' \| gcloud secrets create repoql-embedding-voyage-api-key --data-file=- --project=PROJECT` |
| `repoql-embedding-auth-key-hash-0` | Generate a random API key, SHA-256 hash it, store the hash: `printf 'HASH' \| gcloud secrets create repoql-embedding-auth-key-hash-0 --data-file=- --project=PROJECT` |

**Critical:** Always use `printf` (not `echo -n`) when creating secrets from the command line. PowerShell's `echo` adds trailing `\r\r\n` bytes that corrupt the secret value. If a secret is corrupted, add a new version with `gcloud secrets versions add`.

### API Key Authentication

The embedding service authenticates requests via Bearer token:

1. Client sends `Authorization: Bearer {api_key}`
2. Service computes `SHA256(api_key)` (case-insensitive hex comparison)
3. Compares against hashes stored in `Auth__ApiKeyHashes__0`, `Auth__ApiKeyHashes__1`, etc.

To generate a new key pair:

```bash
# Generate a random 32-byte key
api_key=$(openssl rand -hex 32)
echo "API key: ${api_key}"

# Compute the hash to store in Secret Manager
hash=$(printf '%s' "${api_key}" | sha256sum | cut -d' ' -f1)
echo "Hash: ${hash}"

# Store the hash
printf '%s' "${hash}" | gcloud secrets versions add repoql-embedding-auth-key-hash-0 \
  --data-file=- --project=PROJECT
```

---

## Smoke Testing

### Prerequisites

Install grpcurl (stored in `.tools/grpcurl.exe` for Windows).

### Test Commands

```bash
# Cache miss (first call for new content)
.tools/grpcurl.exe \
  -import-path src/RepoQL.Embedding.Proto/Protos \
  -proto embedding.proto \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{
    "source": "github.com/stueeey/RepoQL",
    "groups": [{
      "document_uri": "file:///smoke.cs",
      "context": "Smoke test",
      "chunks": ["public class SmokeTest { }"]
    }]
  }' \
  CLOUD_RUN_URL:443 \
  repoql.embedding.v1.EmbeddingService/EmbedChunks
```

**Expected results:**
- First call: `totalTokens` > 0 (cache miss, called Voyage)
- Second call (after ~10-15s for Eventarc pipeline): `totalTokens` absent or 0 (cache hit)

### Verify end-to-end pipeline

1. Make a request with a unique chunk → observe `totalTokens` > 0
2. Wait 10-15 seconds (staging write → Eventarc → writer merge)
3. Repeat the same request → observe `totalTokens` absent (proto3 default for 0)
4. The vectors should be identical

### Check service health

```bash
# Service URL
gcloud run services describe repoql-embedding --region=us-central1 --project=PROJECT --format='value(status.url)'

# Recent logs
gcloud run services logs read repoql-embedding --region=us-central1 --project=PROJECT --limit=50
gcloud run services logs read repoql-embedding-writer --region=us-central1 --project=PROJECT --limit=50

# Eventarc trigger status
gcloud eventarc triggers describe staging-to-writer-ENV --location=us --project=PROJECT
```

---

## Troubleshooting

### "Invalid API key"

1. Check the stored hash: `gcloud secrets versions access latest --secret=repoql-embedding-auth-key-hash-0 --project=PROJECT | xxd`
2. Look for trailing bytes (`0d 0d 0a` = PowerShell corruption). Add a clean version with `printf`.
3. Redeploy the embedding service (Cloud Run mounts secrets at deploy time, not runtime).

### "Path misses a bucket parameter" (GCS upload)

Root cause: .NET 10 trimming strips `[RequestParameter]` attributes from `Google.Apis` reflection-based URL template expansion.

Fix: Ensure `<PublishTrimmed>false</PublishTrimmed>` in the service's `.csproj`.

### "Deserialization of types without a parameterless constructor" (Writer)

Same .NET 10 trimming issue. Fix: `<PublishTrimmed>false</PublishTrimmed>` in the writer's `.csproj`.

### Eventarc trigger creation fails with "Permission denied"

1. **Eventarc service agent doesn't exist**: Run `gcloud beta services identity create --service=eventarc.googleapis.com --project=PROJECT`. Wait 1-2 minutes for propagation.
2. **Missing IAM**: Ensure the Eventarc service agent has `storage.objectViewer` on the staging bucket (managed by Pulumi).
3. **Location mismatch**: Bucket location (e.g., `US` multi-region) must match trigger location (`us`). Set `CACHE_BUCKET_LOCATION` GitHub variable.

### Cache not producing hits

1. Check writer logs for merge errors
2. Verify Eventarc trigger exists: `gcloud eventarc triggers list --location=us --project=PROJECT`
3. Check staging bucket for unprocessed files: `gcloud storage ls gs://repoql-staging-ENV/`
4. If staging files accumulate, the writer isn't receiving events

### Pulumi fails with 409 (resource already exists)

Import the existing resource into Pulumi state:

```bash
cd infra/cloud-cache
pulumi import gcp:artifactregistry/repository:Repository containerRepo \
  projects/PROJECT/locations/us-central1/repositories/repoql --yes --stack ENV
```

---

## Architecture Decisions

### Why .NET container publishing (not Dockerfile)

`dotnet publish /t:PublishContainer` produces container images directly from the SDK — no Dockerfile, no Docker daemon, no multi-stage build. Simpler CI, fewer moving parts.

**Gotcha:** .NET 10 preview enables trimming by default for container publishing. This breaks Google.Apis (reflection-based URL templates) and System.Text.Json (private nested types). Both services set `<PublishTrimmed>false</PublishTrimmed>`.

### Why the writer is a separate service

IAM blast radius. The embedding service handles unauthenticated external traffic — if compromised, it can only read the embeddings bucket and write staging. The writer has write access to the permanent embeddings bucket but only receives authenticated Eventarc events (no external traffic). Separate IAM boundaries.

### Why Eventarc (not direct writes)

The embedding service writes to staging and returns immediately. GCS `OBJECT_FINALIZE` triggers the writer automatically — no dispatch code, no queue management, no polling. Pub/Sub provides at-least-once delivery with retries. The 24h staging lifecycle is the dead letter queue.

### Why DuckDB for cache reads

The embedding service already embeds DuckDB for cache lookups via `httpfs`. Point queries on sorted parquet with row group statistics are O(log n). The object cache eliminates footer round-trips on repeat queries. No external database infrastructure needed.

---

## Cost Model

| Component | Cost driver | Notes |
|-----------|-------------|-------|
| Embedding service | Cloud Run CPU/memory per request | Scales to zero. Minimum 0 instances. |
| Writer | Cloud Run per Eventarc invocation | Scales to zero. ~1 invocation per embedding batch. |
| Staging bucket | GCS Standard, 24h lifecycle | Self-cleaning. Negligible at current scale. |
| Embeddings bucket | GCS Standard, read-heavy | ~1KB per cached embedding. Main cost at scale. |
| Eventarc | Free (GCS notifications via Pub/Sub) | Pub/Sub charges are negligible. |
| Secret Manager | Per-secret per-month + per-access | ~$0.06/secret/month. ~10 secrets per env. |

The primary cost savings come from reduced Voyage API calls. Each cache hit saves one Voyage API call (billed by tokens). The cache pays for itself quickly on active repos.

---

## Related

- [Design: Cloud Embedding Cache](cloud-embedding-cache.md) — architecture and contracts
- [Flows: Cloud Cache](../../flows/current/cloud-cache/) — embedding request, cache merge, compaction
- [North Star: Embedding Cache](../../north-star/embedding-cache.md) — what great looks like
- [Bootstrap script](../../../infra/cloud-cache/bootstrap.sh) — one-time project setup
- [Pulumi infrastructure](../../../infra/cloud-cache/Program.cs) — IaC definition
