# Plan: Cloud Service Merge

Implements: Architectural decision from latency research — Cloud Run → Cloud Run hops cost 20-32ms through public GFE. Merging eliminates them entirely.

## Scope

**Covers:**
- New `RepoQL.Cloud.Service` project: embedding + inference gRPC in one process
- Unified auth interceptor (inference's superset: unary + duplex streaming)
- Unified configuration model and service registration
- Single deployment workflow replacing `deploy-embedding.yml` and `deploy-inference.yml`
- Pulumi infrastructure updates: unified service account, `api.repoql.ai` DNS record
- Migration of vendored xAI protos from inference project into merged project

**Does not cover:**
- Embedding Writer service (stays separate — event-driven, different lifecycle)
- Changes to the Embedding.Proto or Inference.Proto gRPC contracts
- WorkOS auth or rate limiting (future work that benefits from single service)
- Cloudflare Access configuration (known incompatible with gRPC)

## Enables

Once merged:
- **Zero inter-service latency** — embedding and inference share a process
- **Single auth interceptor** — future WorkOS refresh tokens and rate limiting implemented once
- **Single deploy** — one workflow, one container image, one Cloud Run revision for gRPC services
- **Simpler Pulumi** — unified service account, one Cloudflare CNAME
- **Shared Firestore** — ProductAnalyticsStore accessible from any handler (inference telemetry, usage tracking)
- **Shared OTEL** — one meter/trace scope covering all domains

## Prerequisites

- Embedding service with SubmitFeedback RPC and ProductAnalyticsStore (done — this session)
- Firestore infrastructure provisioned (done — Pulumi changes committed but not applied)
- All three services currently building and deploying independently

## North Star

One container image. One Cloud Run service. One domain. Deploy in under 3 minutes. Cold start under 2 seconds. No change to any client (same proto contracts, same auth).

## Done Criteria

### Unified Project

- The `RepoQL.Cloud.Service` project shall reference `RepoQL.Embedding.Proto`, `RepoQL.Inference.Proto`, and `RepoQL.Embedding.Storage`
- The project shall include vendored xAI protos (moved from inference service)
- The project shall produce a single container image named `repoql-cloud`
- When built, the project shall compile with zero errors and zero warnings

### gRPC Services

- The host shall map `EmbeddingServiceImpl` and `InferenceServiceImpl` on the same port
- The gRPC interceptor shall handle unary, server streaming, and duplex streaming calls
  - When no API key hashes are configured, all calls shall pass (development mode)
- All existing embedding RPCs shall behave identically (EmbedChunks, EmbedQuery, GetModelInfo, Rerank, SubmitFeedback)
- All existing inference RPCs shall behave identically (Complete, CompleteWithTools)

### HTTP Endpoints

- The host shall map `/merge` for Eventarc-triggered cache merge operations
- The host shall map `/compact` (or equivalent) for scheduled compaction
- When Eventarc delivers a GCS finalization event, the merge handler shall process it identically to today

### Configuration

- The service shall bind `Embedding`, `Inference`, `Auth`, `CacheLayer`, `Writer`, and `Firestore` configuration sections
- The `Auth` section shall be shared across all gRPC services (one set of API key hashes)
- Where cache layer or Firestore configuration is missing, the service shall degrade gracefully (existing behavior preserved)

### Deployment

- A single GitHub Actions workflow `deploy-cloud.yml` shall replace `deploy-embedding.yml`, `deploy-inference.yml`, and `deploy-embedding-writer.yml`
- The workflow shall deploy to Cloud Run with `--use-http2` and all required secrets/env vars from all three services
- The service account shall have IAM for: Secret Manager, storage buckets, Firestore, Eventarc, Cloud Trace

### Infrastructure

- Pulumi shall define a single `cloud-service-{env}` service account replacing `embedding-service-{env}` and `cache-writer-{env}`
- Pulumi shall define a single Cloudflare CNAME (`api.repoql.ai` or similar) replacing `embedding.repoql.ai` and `inference.repoql.ai`
- The Eventarc trigger shall target the merged service's `/merge` endpoint

### Existing Services

- The old `RepoQL.Embedding.Service` and `RepoQL.Inference.Service` projects shall remain in the repo but stop being deployed
  - They serve as reference and their test projects still compile
- The `RepoQL.Embedding.Writer` project shall remain as a library/reference

## Constraints

- **Proto contracts unchanged** — clients must not need updates. Same proto files, same package names, same service definitions
- **Auth identity unchanged** — same SHA-256 API key hash scheme. The interceptor is the inference service's superset version (handles duplex streaming)
- **xAI protos vendored** — must live in the merged project since they're compiled as gRPC client stubs with `AdditionalImportDirs`
- **Writer endpoints stay HTTP** — Eventarc delivers CloudEvents over HTTP, not gRPC. The merged service must serve both protocols
- **Graceful degradation** — missing Voyage key, Grok key, Firestore project, or cache config must each degrade independently. One misconfigured domain must not break others

## Implementation Steps

### 1. Create the project

- New `src/RepoQL.Cloud.Service/RepoQL.Cloud.Service.csproj` (SDK: `Microsoft.NET.Sdk.Web`)
- Reference: `RepoQL.Embedding.Proto`, `RepoQL.Inference.Proto`, `RepoQL.Embedding.Storage`
- Copy vendored xAI protos from inference service (same `Protobuf` items in csproj)
- NuGet: union of all three services' packages (Grpc.AspNetCore, AWSSDK.S3, DuckDB.NET.Data.Full, Google.Cloud.Firestore, Google.Cloud.Storage.V1, OpenTelemetry.*, Grpc.Net.Client, Google.Protobuf)
- Container config: `repoql-cloud`, port 8080, noble family

### 2. Move implementation files

- Copy all `.cs` files from embedding service (except `Program.cs`, `ApiKeyAuthInterceptor.cs`)
- Copy all `.cs` files from inference service (except `Program.cs`, `ApiKeyAuthInterceptor.cs`, `AuthOptions.cs`)
- Copy all `.cs` files from embedding writer (except `Program.cs`)
- Use inference service's `ApiKeyAuthInterceptor` as the unified interceptor (superset)
- Use inference service's `AuthOptions` as the unified auth config
- Update all namespaces to `RepoQL.Cloud.Service` (or keep domain sub-namespaces: `RepoQL.Cloud.Service.Embedding`, `.Inference`, `.Writer`)

### 3. Unified Program.cs

```
OTEL setup (meters: RepoQL.Embedding.*, RepoQL.Inference.*, sources: same)
gRPC setup (shared interceptor, message sizes, compression)
Configuration binding (Embedding, Inference, Auth, CacheLayer, Writer, Firestore)
Service registration (VoyageAiClient, IXaiChatClient/GrokClient, ProductAnalyticsStore, EmbeddingCacheLayer, CacheMergeHandler, CompactionJob, etc.)
Map gRPC services (EmbeddingServiceImpl, InferenceServiceImpl)
Map HTTP endpoints (/merge, /compact)
```

### 4. Single deploy workflow

- New `.github/workflows/deploy-cloud.yml`
- Publishes `RepoQL.Cloud.Service` as container
- Deploys to `repoql-cloud` Cloud Run service
- Combines all secrets from both existing workflows
- Sets `--use-http2` for gRPC support (HTTP endpoints still work alongside)

### 5. Pulumi updates

- Single `cloud-service-{env}` service account with union of all IAM roles
- Single Cloudflare CNAME record (`api` subdomain)
- Eventarc trigger points at merged service
- Old service accounts and DNS records can be removed after migration verified

### 6. Cleanup

- Mark old deployment workflows as deprecated (add `if: false` or delete)
- Update `appsettings.json` to merge all config sections
- Add `InternalsVisibleTo` for test projects

## References

- [Latency Research Findings](../research/) — Cloud Run GFE hop costs 20-32ms, YARP streams 64KB chunks, Cloudflare Access incompatible with gRPC
- [Embedding Service](../../src/RepoQL.Embedding.Service/) — current embedding implementation
- [Inference Service](../../src/RepoQL.Inference.Service/) — current inference implementation with xAI protos
- [Embedding Writer](../../src/RepoQL.Embedding.Writer/) — cache merge and compaction
- [Pulumi Infrastructure](../../infra/cloud-cache/Program.cs) — current GCP/Cloudflare IaC
- Testing: TUnit (`[Test]`), AwesomeAssertions, see `docs/knowledge/testing-guidelines.md`

## Error Policy

Each domain degrades independently:
- **Missing Voyage API key** — embedding RPCs return `Unavailable`, inference and writer unaffected
- **Missing Grok API key** — inference RPCs return `Unavailable`, embedding and writer unaffected
- **Missing Firestore config** — analytics logged only, all RPCs continue
- **Missing cache layer config** — embeddings relay directly to Voyage (no caching)
- **Missing storage config** — writer endpoints return 503, gRPC services unaffected

A single misconfigured domain must never prevent the others from operating.
