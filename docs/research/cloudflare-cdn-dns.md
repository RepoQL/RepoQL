---
description: "Cloudflare CDN proxy and DNS management for repoql.ai → Cloud Run gRPC services via Pulumi C#"
tags: [cloudflare, dns, cdn, grpc, cloud-run, pulumi, infrastructure]
audience: { human: 40, agent: 60 }
purpose: { research: 90, design: 10 }
---

# Cloudflare CDN + DNS for repoql.ai

Research for: how to add Cloudflare DNS management and CDN proxy to the existing Pulumi infrastructure, fronting Cloud Run gRPC services with `repoql.ai`.

*Research date: 2026-03-10*

## Context

RepoQL runs a gRPC embedding service on Google Cloud Run (`repoql-embedding-s3lststjqa-uc.a.run.app`). Infrastructure is managed via Pulumi C# in `infra/cloud-cache/`. The domain `repoql.ai` is owned and registered at Cloudflare. Goals: DNS-as-code via Pulumi, CDN proxy for custom domain, DDoS protection, and analytics.

**In scope:** DNS records, zone settings, CDN proxy, gRPC proxying, origin protection.
**Out of scope:** Edge caching of gRPC responses (requires Workers — separate effort), Cloudflare Access (doesn't support gRPC).

---

## Cloudflare Pulumi Provider

NuGet package `Pulumi.Cloudflare` v6.13.0 (January 2026). Targets .NET 6+. Apache-2.0.

Key resources:

| Resource | Purpose |
|----------|---------|
| `Cloudflare.Zone` | Zone management (requires `Account.Id`) |
| `Cloudflare.DnsRecord` | DNS records with proxy toggle (`Proxied = true` = orange cloud) |
| `Cloudflare.ZoneSetting` | Zone-level settings (string-based `SettingId`) |

Auth: `CLOUDFLARE_API_TOKEN` env var (recommended) or `ProviderArgs.ApiToken`. API tokens are scoped — need `Zone:DNS:Edit`, `Zone:Zone Settings:Edit`, `Zone:Zone:Read` on `repoql.ai`.

`ZoneSetting` uses stringly-typed setting IDs (`"grpc"`, `"ssl"`, `"http2"`, `"min_tls_version"`, `"always_use_https"`). No compile-time validation — IDs must come from docs.

```csharp
var zone = new Cloudflare.Zone("repoql-zone", new Cloudflare.ZoneArgs
{
    Account = new Cloudflare.Inputs.ZoneAccountArgs { Id = accountId },
    Name = "repoql.ai",
    Type = "full",
});

var embedding = new Cloudflare.DnsRecord("embedding", new Cloudflare.DnsRecordArgs
{
    ZoneId = zone.Id,
    Name = "embedding",
    Type = "CNAME",
    Content = "repoql-embedding-s3lststjqa-uc.a.run.app",
    Ttl = 1,       // automatic
    Proxied = true, // orange cloud
});
```

> Sources: [NuGet Gallery](https://www.nuget.org/packages/Pulumi.Cloudflare), [Pulumi Registry — Zone](https://www.pulumi.com/registry/packages/cloudflare/api-docs/zone/), [Pulumi Registry — DnsRecord](https://www.pulumi.com/registry/packages/cloudflare/api-docs/record/), [Pulumi Registry — ZoneSetting](https://www.pulumi.com/registry/packages/cloudflare/api-docs/zonesetting/)

---

## gRPC Through Cloudflare

Available on **all plans** (including Free) at no additional cost.

**Required settings (all must be true):**

| Setting | Requirement |
|---------|-------------|
| gRPC toggle | Enabled (`SettingId = "grpc"`, `Value = "on"`) |
| SSL/TLS mode | Minimum Full (`SettingId = "ssl"`, `Value = "full"` or `"strict"`) |
| DNS proxy | Proxied (orange cloud on the DNS record) |
| Origin port | 443 only |
| Origin protocol | TLS + HTTP/2 (ALPN advertised) |
| Content-Type | `application/grpc` or `application/grpc+proto` |

**How it works internally:** Cloudflare terminates HTTP/2 at the edge, converts gRPC to gRPC-web (HTTP/1.1) for internal pipeline processing (WAF, etc.), then reconverts to HTTP/2 gRPC before connecting to origin. This is terminate-and-re-establish, not pass-through.

**Limitations:**

| Concern | Status |
|---------|--------|
| Unary RPCs | Reliable |
| Server-side streaming | Works, subject to 120s idle timeout |
| Bidirectional streaming | **Unreliable** — recurring breakage reported through April 2025 |
| WAF inspection | Headers only during connection phase; stream content NOT inspected |
| Cloudflare Access | Does NOT support gRPC at all |
| Message size | Community reports degradation above 32-64KB per message |
| Idle timeout | 120s (Enterprise can configure); mitigate with gRPC keepalives |
| Edge caching | Not possible for gRPC POST — requires Workers for content-addressed caching |

The embedding service uses **unary RPCs only** — the reliable path.

> Sources: [Cloudflare gRPC docs](https://developers.cloudflare.com/network/grpc-connections/), [Road to gRPC blog](https://blog.cloudflare.com/road-to-grpc/), [Connection limits](https://developers.cloudflare.com/fundamentals/reference/connection-limits/), Community threads on [bidi-streaming](https://community.cloudflare.com/t/grpc-bidistreaming-broken-via-proxied-domain/788101), [server streaming](https://community.cloudflare.com/t/facing-rst-stream-with-error-code-internal-error-on-grpc-server-side-streaming/639306)

---

## Cloud Run Custom Domain: Two Paths

### Path A: Cloud Run Domain Mapping (Direct)

CNAME to `ghs.googlehosted.com` → Cloud Run handles TLS with Let's Encrypt.

**Problem with Cloudflare:** Google's Let's Encrypt cert provisioning uses HTTP-01 validation. When Cloudflare proxy is enabled, it intercepts validation requests, preventing cert issuance/renewal. Known bug: [Google Issue #157498377](https://issuetracker.google.com/issues/157498377). Workaround: grey-cloud during provisioning (~every 3 months). Domain mapping is still Preview, limited to 4 regions, no path routing.

**Pulumi:** Single resource (`Gcp.CloudRun.DomainMapping`), but the cert renewal conflict makes it fragile with Cloudflare proxy.

### Path B: Global External Application Load Balancer + Serverless NEG

Static IP → GLB → Serverless NEG → Cloud Run. You control certs, routing, and ingress.

**Cloudflare compatibility is clean:** A record to the GLB's static IP, proxied. No cert renewal conflict — use Cloudflare Origin CA (free, 15-year validity, trusted only by Cloudflare) or Google-managed cert on the LB.

**Origin protection:** Set Cloud Run ingress to `internal-and-cloud-load-balancing` + disable default `*.run.app` URL. Direct access becomes impossible — all traffic must flow through the LB.

**Pulumi resources needed (~7-8):**

| Resource | Purpose |
|----------|---------|
| `Gcp.Compute.GlobalAddress` | Static IP |
| `Gcp.Compute.RegionNetworkEndpointGroup` | Serverless NEG → Cloud Run |
| `Gcp.Compute.BackendService` | Backend referencing NEG |
| `Gcp.Compute.URLMap` | URL routing |
| `Gcp.Compute.TargetHttpsProxy` | HTTPS proxy with cert |
| `Gcp.Compute.GlobalForwardingRule` | Binds IP to proxy |
| `Gcp.Compute.SslCertificate` or `ManagedSslCertificate` | Origin TLS cert |

**Cost:** GLB base ~$18/month + per-GB processing. Modest for embedding service traffic volumes.

> Sources: [Cloud Run domain mapping](https://docs.cloud.google.com/run/docs/mapping-custom-domains), [GLB + serverless NEG setup](https://cloud.google.com/load-balancing/docs/https/setup-global-ext-https-serverless), [Google Issue #157498377](https://issuetracker.google.com/issues/157498377), [Cloud Run ingress](https://docs.cloud.google.com/run/docs/securing/ingress)

---

## SSL Certificate Chain

Two-hop TLS when Cloudflare proxies:

```
Client ──[TLS 1]──▶ Cloudflare Edge ──[TLS 2]──▶ Origin (GLB / Cloud Run)
```

**TLS 1 (client → Cloudflare):** Universal SSL cert, auto-managed by Cloudflare. No action needed.

**TLS 2 (Cloudflare → origin):** Depends on architecture:

| Approach | Origin cert | Renewal | Cloudflare SSL mode |
|----------|-------------|---------|---------------------|
| Domain mapping | Google-managed (Let's Encrypt) | Auto but conflicts with proxy | Full |
| GLB + Origin CA | Cloudflare Origin CA (15yr) | Effectively never | Full (Strict) |
| GLB + Google-managed | Google-managed on LB | Auto, no conflict | Full (Strict) |

Cloudflare Origin CA is the cleanest option with the GLB path — no renewal cycles, trusted by Cloudflare's Full (Strict) mode.

> Sources: [Cloudflare Origin CA](https://developers.cloudflare.com/ssl/origin-configuration/origin-ca/), [SSL modes](https://developers.cloudflare.com/ssl/origin-configuration/ssl-modes/)

---

## Authentication Implications

Cloudflare cannot inject Google IAM identity tokens. Cloud Run services behind Cloudflare **must** use `--allow-unauthenticated` (or rely on application-level auth like the existing `ApiKeyAuthInterceptor`).

Security layering:
1. Cloud Run ingress = `internal-and-cloud-load-balancing` (blocks direct `*.run.app` access)
2. `--allow-unauthenticated` on Cloud Run (since Cloudflare can't provide IAM tokens)
3. Application-level API key validation (`ApiKeyAuthInterceptor` — already in place)
4. Optional: Cloud Armor IP allowlisting to Cloudflare ranges on the GLB

The existing `ApiKeyAuthInterceptor` + SHA-256 hash comparison is sufficient — it's already the auth boundary. The GLB + ingress restriction closes the `*.run.app` bypass.

> Sources: [Cloud Run public access](https://docs.cloud.google.com/run/docs/authenticating/public), [Cloud Run ingress](https://docs.cloud.google.com/run/docs/securing/ingress)

---

## Alternative: GCP-Only (No Cloudflare Proxy)

Keep `repoql.ai` DNS at Cloudflare but grey-cloud (DNS-only). Use GCP-native networking:

| Concern | Cloudflare Proxy + GLB | GCP-Only (GLB + Cloud Armor) |
|---------|------------------------|------------------------------|
| DDoS | Cloudflare (excellent, free) | Cloud Armor ($0.75/M requests) |
| gRPC reliability | Terminate + reconvert (unary OK) | Native HTTP/2 end-to-end |
| Custom domain | Cloudflare edge cert + Origin CA | Google-managed cert on GLB |
| Origin protection | CF IP allowlisting + ingress lock | Ingress lock (network-level) |
| Operational surface | Two dashboards, two Pulumi providers | One dashboard, one provider |
| Cost | Cloudflare free + GLB ~$18/mo | GLB ~$18/mo + Cloud Armor ~$5/mo |
| Latency | Extra CF edge hop | Direct to GLB |

The main Cloudflare value-add for gRPC services: DDoS protection (free), analytics (free), and the `repoql.ai` domain is already there. The main cost: operational complexity of two providers and the gRPC terminate-reconvert hop.

> Sources: [Cloud Armor pricing](https://cloud.google.com/armor/pricing), [Cloudflare plans](https://www.cloudflare.com/plans/)

---

## Comparison

| Dimension | Path A (Domain Mapping) | Path B (GLB + CF Proxy) | GCP-Only (GLB, no CF proxy) |
|-----------|------------------------|-------------------------|------------------------------|
| Pulumi complexity | 1 CF resource + 1 GCP resource | ~8 CF resources + ~8 GCP resources | ~8 GCP resources |
| Cert management | Fragile (renewal conflict) | Clean (Origin CA or managed) | Clean (managed) |
| Origin protection | Weak (no ingress lock) | Strong (ingress + IP allowlist) | Strong (ingress lock) |
| gRPC path | CF terminate/reconvert | CF terminate/reconvert | Native HTTP/2 |
| DDoS protection | Cloudflare free | Cloudflare free | Cloud Armor ~$5/mo |
| Monthly cost | ~$0 | ~$18 (GLB) | ~$23 (GLB + Armor) |
| Operational dashboards | 2 | 2 | 1 |

---

## Gaps

- **Cloudflare Origin CA + GCP GLB integration:** Community reports suggest some friction with cert format compatibility when uploading Cloudflare Origin CA certs to GCP. Needs hands-on validation.
- **Cloudflare account ID for Pulumi:** Need the actual account ID from `repoql.ai`'s Cloudflare dashboard to configure the zone resource.
- **Existing DNS records:** Unknown what DNS records already exist at `repoql.ai` in Cloudflare — importing existing state into Pulumi needs investigation.
- **Cloudflare outage failover:** If Cloudflare proxy is down, services are unreachable unless grey-cloud fallback is automated. November 2025 Cloudflare outage is precedent.
- **Bidi-streaming future risk:** If RepoQL ever needs bidirectional gRPC streaming through Cloudflare, that path is unreliable as of early 2025.

---

## Summary

| Question | Finding |
|----------|---------|
| Can Cloudflare proxy gRPC? | Yes, free plan, unary RPCs are reliable |
| Which architecture? | Path B (GLB + Serverless NEG) avoids cert renewal conflicts |
| What certs? | Cloudflare Origin CA on GLB (15yr, no renewal) is cleanest |
| Auth impact? | None — existing `ApiKeyAuthInterceptor` stays, add ingress lock |
| Pulumi provider? | `Pulumi.Cloudflare` v6.13.0, good C# support |
| Is GCP-only simpler? | Yes, but loses free DDoS and domain is already at Cloudflare |
