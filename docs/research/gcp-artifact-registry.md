# Google Artifact Registry

Research for managing Docker container images for Cloud Run deployments.

*Research date: March 19, 2026*

## Context

RepoQL uses a single `repoql` Docker repository in `us-central1` on Artifact Registry. Four services push images from GitHub Actions via Workload Identity Federation. Images are built with `dotnet publish /t:PublishContainer` and tagged with 12-char git SHAs.

---

## Repository Types and Modes

Artifact Registry supports Docker, Maven, npm, Python, Go, Ruby, Debian, RPM, Helm, and generic artifacts. Each repository holds a single format.

| Mode | Description |
|------|-------------|
| Standard | Direct upload/download of private artifacts, with vulnerability scanning |
| Remote | Read-only caching proxy for upstream sources (Docker Hub, Maven Central, etc.) |
| Virtual | Single access point across multiple upstream repositories; mitigates dependency confusion |

### Regional vs Multi-Region

| Type | Characteristics |
|------|----------------|
| Regional (e.g., `us-central1`) | Data in one region. Lower latency for co-located services. Free egress to same-region GCP services. |
| Multi-region (`us`, `europe`, `asia`) | Data replicated across regions within geography. Higher availability. |

RepoQL uses `us-central1` (regional), matching its Cloud Run services — zero egress cost.

> [Repository overview](https://docs.cloud.google.com/artifact-registry/docs/repositories) — modes and formats
> [Artifact Registry locations](https://docs.cloud.google.com/artifact-registry/docs/repositories/repo-locations) — regional vs multi-region

---

## Authentication

| Method | Lifetime | Best For |
|--------|----------|----------|
| gcloud CLI credential helper | Short-lived (auto-refreshed) | Interactive development |
| Standalone credential helper | Short-lived (ADC) | Local dev, build servers |
| OAuth2 access token | **60 minutes** | CI/CD pipelines |
| Service account key | Long-lived (until revoked) | **Not recommended** |

RepoQL uses WIF → OAuth2 access token via `google-github-actions/auth@v2` + `docker/login-action@v3`. This is the recommended keyless CI/CD pattern.

> [Configure authentication for Docker](https://docs.cloud.google.com/artifact-registry/docs/docker/authentication) — all methods
> [google-github-actions/auth](https://github.com/google-github-actions/auth) — WIF action

---

## Image Management

### Tagging

Tags are mutable by default — pushing the same tag overwrites the digest reference. **Immutable tags** can be enabled per-repository:

```bash
gcloud artifacts repositories update REPO --location=LOC --immutable-tags
```

Digests (`sha256:...`) are always content-addressable and immutable.

### Vulnerability Scanning

- **Automatic**: Triggered on every push when Container Scanning API is enabled
- **On-demand**: `gcloud artifacts docker images scan` — useful in CI before deploy
- Scans OS packages and language packages
- Integrates with Security Command Center

### Image Signing & Provenance

- Binary Authorization integration for deploy-time attestation verification
- Cloud Build supports SLSA Level 3 provenance automatically
- `dotnet publish /t:PublishContainer` from GitHub Actions does **not** generate SLSA provenance — would need Cloud Build trigger or separate attestation

> [Container concepts](https://docs.cloud.google.com/artifact-registry/docs/container-concepts) — tagging, immutability
> [Artifact analysis](https://docs.google.com/artifact-registry/docs/analysis) — vulnerability scanning

---

## Cleanup Policies

### Policy Types

| Type | What it does |
|------|-------------|
| Conditional delete | Removes artifacts matching conditions |
| Conditional keep | Retains artifacts matching conditions |
| Keep most recent versions | Preserves N recent versions |

### Available Criteria

`tagState` (tagged/untagged/any), `olderThan`, `newerThan`, `tagPrefixes`, `versionNamePrefixes`, `packageNamePrefixes`, `keepCount`.

### Key Rules

- Max **10 policies** per repository
- Keep policies override delete policies when both match
- **Dry run mode** available
- Changes take effect within ~**1 day** (background job)
- Max **300,000 deletions** per repository per day
- Immutable-tagged artifacts exempt from deletion

RepoQL currently has no cleanup policies. With 4 services pushing SHA-tagged images, untagged images accumulate when the same service is redeployed.

> [Cleanup policy overview](https://docs.cloud.google.com/artifact-registry/docs/repositories/cleanup-policy-overview)
> [Configure cleanup policies](https://docs.cloud.google.com/artifact-registry/docs/repositories/cleanup-policy)

---

## Pricing

### Storage

| Tier | Cost |
|------|------|
| First 0.5 GB | **Free** |
| Above 0.5 GB | **$0.10/GB/month** |

### Data Transfer

| Scenario | Cost |
|----------|------|
| Same region | **Free** |
| Region to multi-region, same continent | **Free** |
| US/Canada cross-region | $0.01/GB |
| Inter-continent | $0.08/GB |
| Ingress | **Free** |

### Vulnerability Scanning

$0.26 per image scan (automatic or on-demand).

RepoQL's cost profile: services in `us-central1` deploying to Cloud Run in `us-central1` — egress is free. Main cost is storage ($0.10/GB/month beyond 0.5 GB free).

> [Artifact Registry pricing](https://cloud.google.com/artifact-registry/pricing)

---

## Quotas and Limits

### Per-Project Request Quotas (Adjustable)

| Quota | Default |
|-------|---------|
| Total requests/min (per region) | 60,000 |
| Write requests/min (per region) | 18,000 |
| Delete requests/min (per region) | 18,000 |

### Fixed Limits

| Limit | Value |
|-------|-------|
| Cleanup policies per repo | 10 |
| Cleanup deletions per repo per day | 300,000 |
| Remote upstream data per request | 9.9 GB |
| Virtual upstream policies | 30 |
| Repo create/delete per region per min | 30 |

Maximum individual image size is not documented.

> [Quotas and limits](https://cloud.google.com/artifact-registry/quotas)

---

## CI/CD Integration

### GitHub Actions Pattern (what RepoQL uses)

```yaml
- uses: google-github-actions/auth@v2
  id: auth
  with:
    workload_identity_provider: ${{ secrets.GCP_WIF_PROVIDER }}
    service_account: ${{ secrets.GCP_SERVICE_ACCOUNT }}
    token_format: 'access_token'

- uses: docker/login-action@v3
  with:
    registry: us-central1-docker.pkg.dev
    username: oauth2accesstoken
    password: ${{ steps.auth.outputs.access_token }}
```

.NET SDK container publishing authenticates via the Docker credential store populated by the login step.

> [docker/login-action](https://github.com/docker/login-action)
> [.NET SDK container publishing](https://learn.microsoft.com/en-us/dotnet/core/containers/sdk-publish)

---

## Security

| Feature | Description |
|---------|-------------|
| VPC Service Controls | Place AR inside a service perimeter to prevent unauthorized exfiltration |
| Binary Authorization | Attestation-based deploy policy for GKE and Cloud Run |
| CMEK | Customer-managed encryption keys |
| IAM | Repository-level access control (not project-level like old Container Registry) |
| Audit logging | Full repository activity logging |
| Download restrictions | Preview — allow/deny rules for artifact access |

> [Protect repositories in a service perimeter](https://docs.cloud.google.com/artifact-registry/docs/securing-with-vpc-sc)

---

## vs Alternatives

| Feature | Artifact Registry | Container Registry | Docker Hub | GitHub Container Registry |
|---------|------------------|-------------------|------------|--------------------------|
| Status | Active, GA | **Shut down March 2025** | Active | Active |
| Formats | Docker + 10 others | Docker only | Docker only | Docker (OCI) |
| Access control | Repository-level IAM | Project-level | Org/user level | Org/user/repo level |
| Locations | 40+ regional, 3 multi-region | 4 multi-region only | US/EU | GitHub infra |
| Remote repos | Yes | No | N/A | No |
| Vulnerability scanning | OS + language | OS only | Basic (paid) | GitHub Advanced Security |
| Pull rate limits | 60k/min per-project | N/A | 100/6hr anon | 1000/hr anon |
| Immutable tags | Yes | No | No | No |

Container Registry was fully shut down by mid-2025.

> [Transition from Container Registry](https://docs.cloud.google.com/artifact-registry/docs/transition/transition-from-gcr)

---

## Gaps

- **Maximum individual image size**: Not documented by Google
- **Concurrent push limits**: Not documented separately from 18k writes/min quota
- **SLSA provenance with `dotnet publish`**: Not supported — Cloud Build or separate attestation step needed
- **Vulnerability scanning pricing tiers**: Dynamic pricing page couldn't be fully verified; $0.26/image from secondary sources
- **Image streaming**: Available for GKE/Spark but not researched for Cloud Run (not applicable)
- **Cleanup policy behavior with SHA tags**: All RepoQL images are tagged (SHA). Untagged images only appear when a tag is moved — with immutable tags, this wouldn't happen. Interaction between immutable tags and cleanup policies for old tagged images needs testing.

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Repository | Single regional Docker repo, co-located with Cloud Run — optimal |
| Auth | WIF + OAuth2 access token — recommended keyless pattern |
| Tagging | SHA tags provide traceability; no immutable tags or cleanup policies configured |
| Pricing | $0.10/GB/month beyond 0.5 GB free; same-region egress free |
| Cleanup gap | No policies — old images accumulate indefinitely |
| Security | Repository-level IAM, VPC-SC support, Binary Authorization available |
| Scanning | $0.26/image; not currently enabled |
