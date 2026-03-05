# Hosting the Embedding Service

**Purpose:** Inform the decision of where to host the RepoQL embedding proxy — a stateless ASP.NET Core gRPC service that relays contextual embedding requests to Voyage AI.

**Date:** 2026-03-02

---

## The Service

A thin gRPC proxy (~50MB container). Accepts `EmbedChunks`/`EmbedQuery` calls from the RepoQL host, forwards to Voyage AI's REST API, returns vectors. Adds batch splitting, circuit breaking, and retry. Stateless — no database, no disk, no session state.

**Traffic pattern:** Bursty. Idle most of the time, then 5–30 minutes of batched requests during indexing. Single user.

**Hard requirement:** gRPC (HTTP/2 end-to-end). Bearer token auth is handled in-app.

---

## Findings

### Eliminated (no gRPC support)

| Platform | Why |
|----------|-----|
| AWS App Runner | No HTTP/2 inbound. Open since 2021, still "Researching." ([#77](https://github.com/aws/apprunner-roadmap/issues/77)) |
| AWS Lambda | HTTP/1.1 only. Lambda Web Adapter does not support HTTP/2. |
| Azure Functions | No gRPC trigger type. ([#771](https://github.com/Azure/Azure-Functions/issues/771)) |
| Google Cloud Functions | No HTTP/2. Google recommends Cloud Run instead. |
| Cloudflare Workers | No HTTP/2 streaming. Workers runtime limitation. ([workerd #4534](https://github.com/cloudflare/workerd/discussions/4534)) |
| Render | HTTP proxy strips HTTP/2 trailers. Multiple community confirmations. |

### Viable: Scale-to-Zero Containers

#### Google Cloud Run — $0/month

- **gRPC:** Native HTTP/2 end-to-end. First-class, well-documented. ([docs](https://cloud.google.com/run/docs/triggering/grpc))
- **Scale-to-zero:** True zero. No charges when idle.
- **Cold start:** 2–5 seconds for .NET. Startup CPU Boost available. Native AOT could reduce further.
- **Free tier:** 180,000 vCPU-seconds + 360,000 GiB-seconds + 2M requests/month. 30 min/day usage = ~54,000 vCPU-seconds (30% of free grant).
- **Deploy:** `gcloud run deploy` from Dockerfile. Container must listen on `$PORT`.
- **Gotchas:** Requires GCP billing account (credit card) even for free tier. 60-minute max request timeout.

Sources: [Cloud Run Pricing](https://cloud.google.com/run/pricing), [Cloud Run gRPC](https://cloud.google.com/run/docs/triggering/grpc), [Cold start benchmarks](https://markaicode.com/google-cloud-run-cold-start-optimization-2025/)

#### Azure Container Apps (Consumption) — $0/month

- **gRPC:** Native HTTP/2 via `transport: http2` ingress config. Setting is mutually exclusive (HTTP/2 only on that ingress). ([examples](https://github.com/azureossd/grpc-container-app-examples))
- **Scale-to-zero:** True zero. No charges when idle.
- **Cold start:** 15–30 seconds. Documented at 22s for hello-world. ([#997](https://github.com/microsoft/azure-container-apps/issues/997), [Gillius benchmark Oct 2025](https://gillius.org/blog/2025/10/cold-start-azure.html))
- **Free tier:** Same grants as Cloud Run (180,000 vCPU-seconds, etc.). Usage fits entirely within free.
- **Deploy:** Azure CLI or Bicep. Requires Container Registry.
- **Gotchas:** Cold start is 3–6x worse than Cloud Run. Environment load balancer may have hidden idle costs. ([#657](https://github.com/microsoft/azure-container-apps/issues/657))

Sources: [ACA Pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [ACA gRPC config](https://azureossd.github.io/2022/07/07/Running-gRPC-with-Container-Apps/)

#### Fly.io — $2–4/month

- **gRPC:** Supported via TCP+TLS+ALPN config (not default HTTP path). Official guide exists. ([docs](https://fly.io/docs/app-guides/grpc-and-grpc-web-services/))
- **Scale-to-zero:** Auto-stop/suspend via Machines API. Suspend resumes in ~300ms (memory preserved). Stop requires full cold start (~2s).
- **Cold start:** ~300ms from suspend, ~2s from stopped.
- **Free tier:** None (legacy free plans deprecated Oct 2024).
- **Cost floor:** Dedicated IPv4 = $2/month. Compute for 30 min/day = ~$0.04/month. Total: ~$2–4/month.
- **Deploy:** `fly launch` + `fly deploy` with Dockerfile.
- **Gotchas:** gRPC needs TCP service config (not HTTP). Auto-stop + TCP/gRPC interaction not as well-documented. IPv4 cost dominates the bill.

Sources: [Fly.io Pricing](https://fly.io/pricing/), [Fly.io gRPC guide](https://fly.io/docs/app-guides/grpc-and-grpc-web-services/), [Machine suspend](https://fly.io/docs/reference/suspend-resume/)

### Viable: Always-On VPS

#### Oracle Cloud Free Tier — $0/month

- 4 ARM Ampere A1 cores, 24 GB RAM (always-free, never expires).
- Publish as `linux-arm64` self-contained binary. systemd service.
- No cold start (always running).
- **Gotchas:** ARM instance availability fluctuates — may take days of retrying to provision. No SLA on always-free. Oracle can reclaim idle instances. Sign-up credit card validation is finicky.

Source: [Oracle Cloud Free Tier](https://www.oracle.com/cloud/free/)

#### Hetzner CX22 — ~€3.49/month (~$3.80)

- 2 shared vCPU, 4 GB RAM, 40 GB SSD. x86-64.
- **Gotcha:** EU-only datacenters. Adds ~100–150ms latency to Voyage AI (likely US-based).

Source: [Hetzner Cloud Pricing](https://www.hetzner.com/cloud)

#### DigitalOcean — $4–6/month

- $4/month: 1 vCPU, 512 MB RAM (tight for .NET). $6/month: 1 GB RAM (comfortable).
- US datacenters available.

Source: [DigitalOcean Droplet Pricing](https://www.digitalocean.com/pricing/droplets)

### Viable: Managed (Always-On, No Scale-to-Zero)

| Platform | Cost | Notes |
|----------|------|-------|
| Azure App Service B1 | ~$13/month | gRPC works (Linux only). No scale-to-zero. Free tier (F1) unreliable for gRPC. |
| AWS Fargate + ALB | ~$17–27/month | ALB minimum ($16–18/month) dominates cost. gRPC works via ALB. |
| AWS Fargate (no ALB) | ~$3–9/month | Dynamic IPs, manual TLS, high deploy complexity. |

### Alternative: Skip Cloud Hosting Entirely

#### Tailscale Mesh — $0/month

Run the proxy on any machine in your tailnet. Other machines reach it via Tailscale IPs. No cloud, no public internet exposure.
- gRPC works (direct TCP, no proxy).
- Only works across your own devices (not publicly reachable).
- **Caveat:** Tailscale Funnel (public) has known gRPC/HTTP2 issues. ([#7893](https://github.com/tailscale/tailscale/issues/7893))

Source: [Tailscale Funnel docs](https://tailscale.com/kb/1223/funnel)

#### Eliminate the Proxy — $0/month

Move `VoyageAiClient.cs` (~307 lines) into a `DirectVoyageEmbeddingProvider` implementing `IContextualEmbeddingProvider`. Replace hand-rolled circuit breaker with Polly via `Microsoft.Extensions.Http.Resilience`. Each RepoQL host calls Voyage directly.

**What you gain:** Zero infrastructure. One fewer network hop. Lower latency. No server to secure/update. Better resilience library (Polly is battle-tested).

**What you lose:**
- Single point of API key management (each machine needs its own key).
- Shared circuit breaker state across machines.
- Centralized metering/cost observability.
- The clean gRPC client/server boundary (though the interface stays the same).

**Effort:** ~1–2 days. Extract VoyageAiClient, add Polly pipeline, register as `IContextualEmbeddingProvider`, add Voyage API key to host config.

Sources: [Voyage AI API](https://docs.voyageai.com/reference/contextualized-embeddings-api), [Polly](https://github.com/App-vNext/Polly), [.NET HTTP Resilience](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/implement-http-call-retries-exponential-backoff-polly)

---

## Comparison

| Option | Monthly Cost | gRPC | Cold Start | Complexity | Multi-Machine |
|--------|-------------|------|------------|------------|---------------|
| Google Cloud Run | $0 | Native | 2–5s | Low | Yes |
| Azure Container Apps | $0 | Native | 15–30s | Medium | Yes |
| Fly.io | $2–4 | TCP config | 300ms suspend | Low-Med | Yes |
| Oracle Cloud VM | $0 | Yes | None | Medium | Yes |
| Hetzner VPS | ~$4 | Yes | None | Low | Yes |
| DigitalOcean VPS | $4–6 | Yes | None | Low | Yes |
| Tailscale mesh | $0 | Yes | None | Low | Private only |
| **Eliminate proxy** | **$0** | **N/A** | **None** | **Low-Med** | **No** |
| Azure App Service B1 | $13 | Yes | None | Low | Yes |
| AWS Fargate + ALB | $17–27 | Yes | None | High | Yes |

---

## Gaps in This Research

1. **Cloud Run cold start with .NET 10 + Native AOT specifically** — estimated from .NET 8/9 benchmarks. Could be significantly faster with AOT.
2. **Oracle Cloud free-tier ARM availability** — varies by region and time. Some users report weeks of retrying.
3. **Fly.io suspend behavior with TCP/gRPC services** — documented for HTTP services, unclear edge cases for TCP.
4. **Azure Container Apps cold start improvements since Oct 2025 Gillius benchmark** — Microsoft announced investments but no updated numbers found.
5. **Voyage AI ToS on API key sharing across machines** — affects the "eliminate proxy" option at scale.

---

## Source Incentives

| Source | Incentive |
|--------|-----------|
| Cloud provider pricing pages | Attract customers; prices are accurate but emphasize free tiers |
| GitHub issues (App Runner, ACA, etc.) | User frustration; descriptions of limitations are reliable |
| Gillius cold-start benchmark | Independent developer blog; no commercial incentive |
| Platform documentation (Cloud Run, Fly.io) | Accurate for supported features; may understate limitations |
| Community forums (Render, Railway) | Users reporting actual experience; high credibility |

---

*You present. They decide.*
