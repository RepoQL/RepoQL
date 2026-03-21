# Google Cloud Run

Research for deploying containerized .NET services on Cloud Run.

*Research date: March 19, 2026*

## Context

RepoQL deploys 4 services to Cloud Run in `us-central1`: `repoql-cloud` (1 vCPU/1GiB, max 50 instances), `repoql-embedding` (1 vCPU/512MiB, max 1), `repoql-inference` (1 vCPU/512MiB, max 1), `repoql-embedding-writer` (1 vCPU/1GiB, max 1, private). All use `dotnet publish /t:PublishContainer`, scale to zero, 300s timeout. Being consolidated into fewer services.

---

## Container Runtime

### Execution Model

Containers listen on `0.0.0.0:$PORT` (default 8080). Filesystem is writable but **in-memory** — counts against instance RAM.

### CPU Allocation Modes

| Mode | CPU Available | Background Work | Billing |
|------|---------------|-----------------|---------|
| Request-based (default) | During request processing only | Not supported (throttled between requests) | Pay while processing |
| Instance-based | Entire instance lifecycle | Supported | Pay for full lifetime |

### CPU/Memory Limits

| CPU | Max Memory |
|-----|------------|
| 1 vCPU | 4 GiB |
| 2 vCPU | 8 GiB |
| 4 vCPU | 16 GiB |
| 8 vCPU (max) | 32 GiB (max) |

Sub-1 vCPU: max concurrency 1, request-based billing required.

### Startup CPU Boost

Doubles CPU during startup + 10 seconds after. 1 vCPU → 2 vCPU boost. Charged only for boost duration.

### Execution Environments

| Aspect | Gen 1 | Gen 2 |
|--------|-------|-------|
| Technology | gVisor sandbox | Linux microVM |
| Cold starts | Faster | Slower |
| CPU perf | Standard | Faster sustained |
| Min memory | No floor | 512 MiB |

### Shutdown

SIGTERM → 10 seconds → SIGKILL. Handle SIGTERM for graceful shutdown.

### Concurrency

Default: 80x vCPUs. Max configurable: 1,000 concurrent requests per instance.

> [Container runtime contract](https://docs.cloud.google.com/run/docs/container-contract)
> [CPU limits](https://docs.cloud.google.com/run/docs/configuring/services/cpu)

---

## Scaling

### Autoscaling Signals

- CPU utilization target: 60% over 1-minute window
- Concurrency target: 60% of max over 1-minute window

### Min/Max Instances

| Setting | Default | Range |
|---------|---------|-------|
| Minimum | 0 (scale to zero) | 0 to max |
| Maximum | 100 | 1 to regional quota |

Min instances incur charges even without traffic.

### Cold Start Behavior

- Container must start listening within **4 minutes**
- Idle instances kept ~**15 minutes** before eviction
- Requests pend for up to 3.5x avg startup time or 10 seconds (whichever greater)
- No instance available → 429 (max instances) or 503

### Minimizing Cold Starts for .NET

| Strategy | Effect |
|----------|--------|
| `--min-instances=1` | Eliminates scale-from-zero |
| `--cpu-boost` | Doubles CPU during startup (~50% faster) |
| Native AOT | ~50ms startup vs ~400ms JIT |
| Lazy init | Defer expensive work to first request |
| Chiseled images | Smaller, faster (though CR streams images) |

> [Instance autoscaling](https://docs.cloud.google.com/run/docs/about-instance-autoscaling)

---

## Networking

### Ingress

- **All traffic** (default): Internet + VPC
- **Internal + LB**: VPC or load balancer only
- **Internal only**: VPC only

### gRPC and HTTP/2

- Supports HTTP/1, HTTP/2, WebSockets, all gRPC streaming modes (GA)
- **HTTP/2 must be explicitly enabled** (`--use-http2`)
- Container handles h2c (cleartext) — TLS terminated by load balancer
- Max 100 concurrent HTTP/2 streams per connection
- Each gRPC stream = one concurrent request for autoscaling

### Bandwidth

| Metric | Limit |
|--------|-------|
| Direct VPC egress | 1 Gbps/instance |
| Other egress | 600 Mbps/instance |
| Outbound connections/sec | 700 |
| DNS resolutions/sec | 1,000 |

> [Using gRPC](https://docs.cloud.google.com/run/docs/triggering/grpc)
> [Quotas](https://docs.cloud.google.com/run/quotas)

---

## Multi-Container (Sidecars)

- Up to **10 containers** per instance
- Shared network namespace (localhost communication)
- In-memory shared volumes (emptyDir)
- Container startup ordering via `container-dependencies` annotation
- Sidecar containers require explicit health check probes

> [Multi-container](https://docs.cloud.google.com/run/docs/configuring/services/containers)

---

## Deployment

### Revision Model

Every config/image change creates an immutable **revision**. Max 1,000 per service.

### Traffic Splitting

Percentage-based across revisions. In-flight requests complete during transitions.

### Rollbacks

Route 100% traffic to a previous revision — no rebuild needed.

### Canary

Deploy with `--no-traffic`, test via tagged revision URL, gradually shift traffic.

> [Rollouts and rollbacks](https://docs.cloud.google.com/run/docs/rollouts-rollbacks-traffic-migration)

---

## Pricing (Tier 1, us-central1)

| Resource | Rate |
|----------|------|
| CPU | $0.000024/vCPU-second |
| Memory | $0.0000025/GiB-second |
| Requests | $0.40/million |

### Free Tier (per billing account/month)

| Resource | Request-Based | Instance-Based |
|----------|---------------|----------------|
| CPU | 180,000 vCPU-sec (~50 hrs) | 240,000 vCPU-sec (~67 hrs) |
| Memory | 360,000 GiB-sec (~100 hrs at 1 GiB) | 450,000 GiB-sec |
| Requests | 2 million | 2 million |

### Committed Use Discounts

Instance-based only: 28% (1-year), 46% (3-year).

Per-hour cost for 1 vCPU/1 GiB: ~$0.095/hr (~$69/month always-on).

> [Cloud Run pricing](https://cloud.google.com/run/pricing)

---

## Quotas and Limits

| Limit | Value |
|-------|-------|
| Max instances per revision | 100 default (increasable) |
| Memory per instance | 32 GiB |
| vCPU per instance | 8 |
| Startup timeout | 4 minutes |
| Request timeout | Up to 60 minutes |
| HTTP/1 request/response size | 32 MiB |
| Concurrent requests/instance | 1,000 |
| Services per project/region | 1,000 |
| Revisions per service | 1,000 |
| Env vars per container | 1,000 |

> [Cloud Run Quotas](https://docs.cloud.google.com/run/quotas)

---

## Health Checks

| Probe | Purpose | Default |
|-------|---------|---------|
| Startup | Is container started? | TCP, 240s timeout |
| Liveness | Should container restart? | None (explicit) |
| Readiness (Preview) | Should instance get traffic? | None (explicit) |

HTTP probes use HTTP/1 even if service uses HTTP/2. gRPC probes require Health Checking Protocol implementation.

> [Health checks](https://docs.cloud.google.com/run/docs/configuring/healthchecks)

---

## .NET Specifics

### Container Publishing

`dotnet publish /t:PublishContainer` builds OCI images without Dockerfile. Supports `ContainerRegistry`, `ContainerRepository`, `ContainerImageTag`, `ContainerFamily` MSBuild properties.

### Native AOT

| Metric | JIT | Native AOT |
|--------|-----|------------|
| Startup | ~400ms | ~50ms |
| Image size | Larger | 3-5x smaller |
| Memory | Higher | Lower |

Caveats: not all .NET libraries support trimming. Reflection-heavy code needs source generators. RepoQL already hit trimming issues (`JsonSerializer.Serialize(new { ... })` → `{}` in Release builds).

### No .NET-specific Google guide exists

Google provides Cloud Run optimization guides for Java, Python, Go, Node.js — but not .NET.

> [dotnet publish containers](https://learn.microsoft.com/en-us/dotnet/core/containers/sdk-publish)

---

## Security

### Service-to-Service Auth

IAM + OIDC ID tokens. Grant `roles/run.invoker` to calling SA. Token via metadata server, ~1 hour expiry. Custom domains NOT supported as `aud` claim.

### Binary Authorization

Deploy-time attestation verification. Breakglass for emergencies.

### RepoQL Note

3 of 4 services use `--allow-unauthenticated`, relying on app-level auth (`Auth__ApiKeyHashes__*`). Only the writer uses IAM-based `--no-allow-unauthenticated`.

> [Service-to-service auth](https://docs.cloud.google.com/run/docs/authenticating/service-to-service)

---

## Gaps

- **No .NET optimization guide** from Google
- **Container image size limit**: Not documented (streaming means size doesn't affect cold starts)
- **Readiness probes**: Preview/pre-GA
- **Worker pools**: Preview, no autoscaling
- **Request vs instance billing rate difference**: Some sources disagree on whether per-second rates differ

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Scaling | Autoscales on CPU (60%) and concurrency (60%); scale to zero supported |
| Cold starts | CPU boost, AOT, min-instances=1 are mitigation strategies |
| Pricing | ~$0.095/hr per 1vCPU/1GiB instance; generous free tier |
| gRPC | GA, HTTP/2 must be explicitly enabled, h2c in container |
| Sidecars | Up to 10 containers, shared network, ordered startup |
| Deployment | Revision-based, traffic splitting, instant rollbacks |
| .NET gap | No Google optimization guide; AOT promising but trimming risks |
