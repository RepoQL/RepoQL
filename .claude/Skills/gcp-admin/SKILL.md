---
name: gcp-admin
description: Administer RepoQL GCP infrastructure — Cloud Run services, Firestore feedback, secrets, Cloudflare DNS, and deployments.
triggers:
  - deploy cloud
  - check feedback
  - cloud run
  - gcp
  - infrastructure
  - production status
  - delete service
  - cloudflare
  - check logs
---

# GCP Administration

Administer RepoQL's cloud infrastructure across two GCP projects and Cloudflare.

## Architecture

```
Users → api.repoql.ai (Cloudflare CNAME → ghs.googlehosted.com)
         → repoql-cloud (Cloud Run) — embeddings, inference, reranking, feedback, cache
         → repoql-embedding-writer (Cloud Run) — cache write pipeline (Eventarc triggered)
```

## Projects

| Project | ID | Purpose |
|---------|-----|---------|
| Production | `repoql-production` | Live service |
| Dev | `repoql-dev` | Testing |

## Services

| Service | What it does | Key config |
|---------|-------------|------------|
| `repoql-cloud` | Unified API: Voyage embeddings (v4-lite), Grok inference, Voyage reranking (rerank-2.5), product feedback (Firestore) | `Embedding__VoyageApiKey`, `Inference__GrokApiKey`, `Firestore__ProjectId` |
| `repoql-embedding-writer` | Writes embedding cache to GCS. Triggered by Eventarc | HMAC keys for GCS |

## Common Operations

### Check service status
```bash
gcloud run services list --project=repoql-production --region=us-central1 --format="table(metadata.name,status.url)"
```

### Check service config (env vars, scaling, resources)
```bash
gcloud run services describe repoql-cloud --project=repoql-production --region=us-central1 \
  --format="yaml(spec.template.spec.containers[0].env,spec.template.spec.containers[0].resources)"
```

### View recent logs
```bash
# Last N requests to a service
gcloud logging read "resource.type=cloud_run_revision AND resource.labels.service_name=repoql-cloud" \
  --project=repoql-production --limit=20 \
  --format="table(timestamp,httpRequest.requestUrl,httpRequest.status,httpRequest.latency)" \
  --freshness=1d

# Errors only
gcloud logging read "resource.type=cloud_run_revision AND resource.labels.service_name=repoql-cloud AND severity>=ERROR" \
  --project=repoql-production --limit=10 --freshness=7d
```

### Check Firestore feedback
```bash
TOKEN=$(gcloud auth print-access-token)
curl -s -H "Authorization: Bearer $TOKEN" \
  "https://firestore.googleapis.com/v1/projects/repoql-production/databases/(default)/documents/product-analytics?pageSize=50"
```
Collection: `product-analytics`. Fields: `type`, `sessionId`, `feedback`, `diagnostics`, `version`, `platform`, `timestamp`.

### Deploy (via GitHub Actions)
Deployments are triggered through GitHub Actions, not directly.
```bash
# Cloud service (auto-deploys on main when cloud-relevant paths change)
gh run list --workflow=deploy-cloud.yml --limit=5

# Trigger manual deploy
gh workflow run deploy-cloud.yml --field environment=prod

# Embedding writer
gh workflow run deploy-embedding-writer.yml --field environment=prod
```

### Check deployed version
```bash
gcloud run services describe repoql-cloud --project=repoql-production --region=us-central1 \
  --format="value(spec.template.spec.containers[0].image)"
```

### Manage secrets
```bash
# List all secrets
gcloud secrets list --project=repoql-production --format="table(name,createTime)"

# View a secret value
gcloud secrets versions access latest --secret=repoql-embedding-voyage-api-key --project=repoql-production

# Update a secret
echo -n "new-value" | gcloud secrets versions add repoql-embedding-voyage-api-key --data-file=- --project=repoql-production
```

### GCS buckets
```bash
# Check bucket sizes
gcloud storage du --summarize gs://repoql-embeddings-prod
gcloud storage du --summarize gs://repoql-staging-prod
```

### Delete a service (destructive)
Always confirm with the user before deleting.
```bash
gcloud run services delete SERVICE_NAME --project=repoql-production --region=us-central1 --quiet
```

## Cloudflare (api.repoql.ai)

Zone ID: `08ff3d6c5676339be3c69687c116f7a6`

Use the `mcp__cloudflare-api__execute` tool. Key DNS records:

| Record | Type | Target | Proxied |
|--------|------|--------|---------|
| `api.repoql.ai` | CNAME | `ghs.googlehosted.com` | Yes |
| `downloads.repoql.ai` | CNAME | `public.r2.dev` | Yes |
| `admin.repoql.ai` | CNAME | `cname.workos-dns.com` | No |
| `auth.repoql.ai` | CNAME | `cname.workos-dns.com` | No |
| `login.repoql.ai` | CNAME | `cname.workos-dns.com` | No |

```javascript
// List DNS records
async () => {
  const zoneId = "08ff3d6c5676339be3c69687c116f7a6";
  const dns = await cloudflare.request({ method: "GET", path: `/zones/${zoneId}/dns_records` });
  return dns.result.map(r => ({ name: r.name, type: r.type, content: r.content, proxied: r.proxied }));
}
```

## Embedding Model Config

Currently: `voyage-4-lite` (standard endpoint, $0.02/M tokens, 31K tok/s)
Reranking: `rerank-2.5` ($0.05/M tokens)

To switch models, update `Embedding__Model` env var on Cloud Run or change `appsettings.json` and redeploy. The VoyageAiClient auto-routes v4 models to `/v1/embeddings` and context-3 to `/v1/contextualizedembeddings`.

## Key Constraints

- Never delete `repoql-cloud` or `repoql-embedding-writer` without explicit permission
- Secrets are shared between services via Secret Manager — changing a secret affects all services that mount it
- `api.repoql.ai` domain mapping lives on `repoql-cloud` — deleting the service breaks the custom domain
- Always confirm destructive operations with the user
