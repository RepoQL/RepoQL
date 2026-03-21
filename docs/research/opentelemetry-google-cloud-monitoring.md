# OpenTelemetry with Google Cloud Monitoring

Research for configuring OpenTelemetry export from .NET services to Google Cloud Monitoring and Cloud Trace, particularly on Cloud Run.

*Research date: March 19, 2026*

## Context

RepoQL runs multiple .NET services on Google Cloud Run (embedding, inference, cloud service, embedding writer). The cloud service already exports OTLP telemetry to Google Cloud via `telemetry.googleapis.com` (commit `48c6a0e`). This research captures the full landscape: what exists, what the options are, what the gotchas are, and what the current implementation gets right or wrong.

Scope: .NET applications exporting metrics, traces, and logs via OpenTelemetry to Google Cloud. Out of scope: non-GCP backends, non-.NET languages.

---

## Google Cloud's OTLP Endpoint

Google provides a native OTLP-compatible endpoint at `https://telemetry.googleapis.com`. This is the **recommended path** for all new and existing users — the older vendor-specific exporters (`googlemanagedprometheus`, `google-cloud-trace-exporter`) are no longer recommended.

### Signal Support

| Signal | Status | Backend | Endpoint |
|--------|--------|---------|----------|
| Traces | **GA** (since Sept 2025) | Cloud Trace | `v1.traces` |
| Metrics | **Preview** (Pre-GA) | Cloud Monitoring | `v1.metrics` |
| Logs | **Not supported** | — | No `v1.logs` endpoint exists |

For logs, the options are: Google-Built OpenTelemetry Collector with `googlecloud` exporter, the Ops Agent, or direct Cloud Logging API calls.

### Wire Protocols

Supports gRPC, http/protobuf, and http/json. **Google recommends gRPC only** for direct SDK export because most SDK HTTP exporters lack dynamic OAuth2 token refresh support. gRPC channel credentials handle this natively.

### Authentication

Required OAuth2 scopes (either):
- `https://www.googleapis.com/auth/cloud-platform`
- `https://www.googleapis.com/auth/trace.append` (traces only)

IAM roles needed on the service account:
- `roles/monitoring.metricWriter` (metrics)
- `roles/cloudtrace.agent` (traces)

On Cloud Run, Application Default Credentials (ADC) automatically use the attached service account via the metadata server. No credential files or env vars needed — `GoogleCredential.GetApplicationDefault()` discovers them.

### vs Old Exporters

| Aspect | Old exporters | OTLP via `telemetry.googleapis.com` |
|--------|---------------|--------------------------------------|
| Data model | Transform OTLP → proprietary (possible data loss) | Native OTLP preserved |
| Metric names | Converts `.` and `/` to `_` | Preserves `.` and `/` verbatim |
| Vendor lock-in | Vendor-specific dependency | Standard OTLP exporter |
| Trace limits | Proprietary API limits | More generous limits |
| Recommendation | **No longer recommended** | **Recommended path** |

> [Telemetry (OTLP) API overview](https://docs.cloud.google.com/stackdriver/docs/reference/telemetry/overview) — endpoint reference
> [OTLP metric ingestion overview](https://docs.cloud.google.com/stackdriver/docs/otlp-metrics/overview) — metrics preview details
> [Migrate to OTLP endpoints (traces)](https://docs.cloud.google.com/stackdriver/docs/instrumentation/migrate-to-otlp-endpoints) — migration guide
> [Migrate to OTLP exporter (metrics)](https://docs.cloud.google.com/stackdriver/docs/otlp-metrics/migrate-to-otlphttp) — migration guide
> [OpenTelemetry now in Google Cloud Observability](https://cloud.google.com/blog/products/management-tools/opentelemetry-now-in-google-cloud-observability) — Sept 2025 GA announcement

---

## .NET Configuration

### NuGet Packages

No Google-specific .NET exporter exists. Unlike Go, Python, Java, and Node.js (which have `GoogleCloudPlatform/opentelemetry-operations-{lang}` repos), .NET has no equivalent. Google's SDK documentation only covers Go, Java, Python, and Node.js — .NET is absent.

The approach: standard OTLP exporter + custom auth handler using `Google.Apis.Auth`.

Core packages needed:
- `OpenTelemetry` + `OpenTelemetry.Api`
- `OpenTelemetry.Exporter.OpenTelemetryProtocol`
- `OpenTelemetry.Extensions.Hosting`
- `OpenTelemetry.Instrumentation.AspNetCore` (ASP.NET Core auto-instrumentation)
- `OpenTelemetry.Instrumentation.Runtime` (runtime metrics — GC, threadpool)
- `Google.Apis.Auth` (for `GoogleCredential`, typically already a transitive dep)

Optional:
- `OpenTelemetry.Resources.Gcp` — pre-release package providing `AddGcpDetector()` for automatic Cloud Run resource detection. In `opentelemetry-dotnet-contrib` repo. Not yet stable on NuGet.

> [googleapis/google-cloud-dotnet#9656](https://github.com/googleapis/google-cloud-dotnet/issues/9656) — no .NET-specific Google exporter
> [opentelemetry-dotnet-contrib GCP README](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Resources.Gcp/README.md) — resource detector

### Code Pattern

The pattern for direct OTLP export to Google Cloud from .NET:

```csharp
// 1. Get ADC credential
var credential = GoogleCredential.GetApplicationDefault()
    .CreateScoped("https://www.googleapis.com/auth/cloud-platform")
    .CreateWithEnvironmentQuotaProject();

// 2. Configure OTLP exporter
options.Endpoint = new Uri("https://telemetry.googleapis.com");
options.Protocol = OtlpExportProtocol.Grpc;
options.HttpClientFactory = () =>
{
    var handler = new GoogleCloudTelemetryAuthHandler(credential);
    return new HttpClient(handler)
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
};

// 3. Apply to all signals
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddOtlpExporter(configure))
    .WithTracing(t => t.AddOtlpExporter(configure));
builder.Logging.AddOpenTelemetry(l => l.AddOtlpExporter(configure));
```

Where `GoogleCloudTelemetryAuthHandler` is a `DelegatingHandler` that calls `GetAccessTokenForRequestAsync` on each request to inject a fresh Bearer token.

### .NET-Specific Gotchas

| Gotcha | Detail |
|--------|--------|
| gRPC requires explicit config | .NET 8+ defaults to `http/protobuf`. Must explicitly set `OtlpExportProtocol.Grpc` and ensure `Grpc.Net.Client` is available |
| HTTP/2 required for gRPC | Must set `DefaultRequestVersion = HttpVersion.Version20` and `RequestVersionExact` on custom HttpClient |
| `UseOtlpExporter` vs `AddOtlpExporter` | Mutually exclusive — cannot call both on the same `IServiceCollection` |
| Token refresh | Static `Headers` auth fails when tokens expire (~1 hour). Must use `HttpClientFactory` with `DelegatingHandler` |
| `cloud-platform` scope | Required for `telemetry.googleapis.com` |
| Default export interval | 60 seconds (safe — minimum is 10 seconds per time series) |
| Default aggregation temporality | Cumulative for OTLP exporter — maps Counter to CUMULATIVE, which is what Cloud Monitoring expects |

> [opentelemetry-dotnet#2504](https://github.com/open-telemetry/opentelemetry-dotnet/issues/2504) — token refresh discussion
> [opentelemetry-dotnet#5538](https://github.com/open-telemetry/opentelemetry-dotnet/issues/5538) — UseOtlpExporter vs AddOtlpExporter

---

## Cloud Run Specifics

### Environment Variables

Auto-injected by Cloud Run (reserved, cannot override):

| Variable | Content |
|----------|---------|
| `K_SERVICE` | Service name |
| `K_REVISION` | Revision name |
| `K_CONFIGURATION` | Configuration name |
| `PORT` | Port to listen on (ingress container only) |

Notably, `GOOGLE_CLOUD_PROJECT` is **not** automatically set. Libraries discover the project ID via the metadata server, not an env var.

### Resource Attributes

OTel resource attributes determine how telemetry appears in Google Cloud Console. For proper mapping to the `cloud_run_revision` monitored resource type, these attributes should be present:

| Attribute | Source | Purpose |
|-----------|--------|---------|
| `service.name` | `OTEL_SERVICE_NAME` env var | Service identification |
| `service.version` | `OTEL_RESOURCE_ATTRIBUTES` env var | Version tracking |
| `cloud.provider` | Hardcoded `gcp` | Cloud provider |
| `cloud.platform` | Hardcoded `gcp_cloud_run` | Platform type |
| `cloud.region` | Metadata server | Region |
| `cloud.account.id` | Metadata server | GCP project ID |
| `faas.name` | `K_SERVICE` env var | Cloud Run service name |
| `faas.version` | `K_REVISION` env var | Cloud Run revision |
| `service.instance.id` | Metadata server | Per-instance ID |

Without these, telemetry may land under `generic_task` or `generic_node` instead of `cloud_run_revision`, making it harder to correlate with Cloud Run's built-in metrics.

The `OpenTelemetry.Resources.Gcp` pre-release package (`AddGcpDetector()`) populates these automatically. Alternatively, set via `OTEL_RESOURCE_ATTRIBUTES` or `ResourceBuilder` in code.

### Sidecar vs Direct Export

| Factor | Direct Export | Sidecar Collector |
|--------|---------------|-------------------|
| Complexity | Minimal — SDK config only | Second container + YAML config in Secret Manager |
| Cold start | Lighter | Heavier — collector must start first |
| Batching/retry | SDK-level only | Production-grade batching, retry, backoff |
| Resource detection | Manual | Automatic via `resourcedetection` processor |
| Multi-backend | One destination | Fan-out trivially |
| Cloud Run fit | No sidecar needed | Multi-container deployment required |

Google recommends direct OTLP for high-volume scenarios (avoids collector bottleneck). Collector makes sense when you need fan-out, processing, or tail-based sampling.

> [Container runtime contract](https://docs.cloud.google.com/run/docs/container-contract) — env vars and metadata
> [OTel Collector on Cloud Run](https://docs.cloud.google.com/stackdriver/docs/instrumentation/opentelemetry-collector-cloud-run) — sidecar pattern
> [OpenTelemetry semantic conventions for cloud](https://opentelemetry.io/docs/specs/semconv/resource/cloud/) — resource attributes

---

## Metric Model Translation

### OTel → GCP Mapping

| OTel Instrument | GCP Metric Kind | GCP Value Type | Suffix |
|-----------------|-----------------|----------------|--------|
| Gauge | GAUGE | DOUBLE | `/gauge` |
| Counter (cumulative) | CUMULATIVE | DOUBLE | `/counter` |
| Counter (delta) | DELTA | DOUBLE | `/delta` |
| UpDownCounter | GAUGE | DOUBLE | `/gauge` |
| Histogram (cumulative) | CUMULATIVE | DISTRIBUTION | `/histogram` |
| Histogram (delta) | DELTA | DISTRIBUTION | `/histogram:delta` |

### Naming

Metrics appear in Cloud Monitoring as `prometheus.googleapis.com/{metric_name}/{suffix}`. Periods and slashes are preserved (unlike old exporters). Label keys must match `[a-zA-Z_][a-zA-Z0-9_.]*` — non-conforming metrics are rejected silently.

### Gotchas

- **INT64 → DOUBLE**: All OTLP INT64 metrics are silently converted to DOUBLE. Cannot rely on integer precision for counter values.
- **UpDownCounter → GAUGE**: Treated as point-in-time value, not cumulative. Won't support `rate()` in PromQL.
- **Monitored resource**: All metrics use `prometheus_target` resource type requiring `location`, `cluster`, `namespace`, `instance` labels. If `instance` is empty, the metric is rejected.
- **Write interval**: Minimum 10 seconds per time series. Points pushed more frequently are rejected.
- **Cardinality**: Hard limit of 10,000 custom metric descriptors per project. High-cardinality attributes (user IDs, parameterized paths) create unique time series — use traces for high-cardinality data.
- **Summary expansion**: OTel Summary instruments expand into 3 separate time series, tripling descriptor usage.

> [v1.metrics overview](https://docs.cloud.google.com/stackdriver/docs/reference/telemetry/v1.metrics) — metric type mapping and naming
> [Cloud Monitoring quotas](https://docs.cloud.google.com/monitoring/quotas) — limits

---

## Cost and Billing

### Metrics (via Telemetry API — billed as "Prometheus Samples Ingested")

| Volume (samples/month) | Rate |
|------------------------|------|
| Up to 50 billion | $0.06/million |
| 50–250 billion | $0.048/million |
| 250–500 billion | $0.036/million |
| Over 500 billion | $0.024/million |

First 150 MiB/month of custom metrics is free.

### Traces

- $0.20 per million spans ingested
- Free tier: 2.5 million spans/month per billing account
- Cloud Run auto-generated request spans are non-chargeable (only custom app spans count)

### Monitoring API Reads

$0.50 per million time series returned, with 1 million free/month.

### Alerting (effective May 1, 2026)

$0.10/month per condition + $0.35 per million time series returned by alert queries.

### Realistic Estimate for RepoQL

A single Cloud Run service with ~10 custom metrics at 60-second intervals generates roughly 0.4–1 MiB/month — well within the free tier. Trace costs depend on request volume; 2.5M free spans covers ~83K requests/day at 1 span/request.

**Bill shock vectors:**
1. Cardinality explosion from high-cardinality labels
2. Export interval below 10 seconds (silent point rejection)
3. Summary instruments tripling descriptor count

> [Google Cloud Observability pricing](https://cloud.google.com/stackdriver/pricing)
> [Pricing examples](https://cloud.google.com/stackdriver/observability-pricing-examples)

---

## Quotas and Limits

### Telemetry API — Metrics

| Limit | Value |
|-------|-------|
| Requests/minute | 60,000 (default, increasable) |
| Max datapoints/request | 200 |
| Effective throughput | ~200,000 samples/second |
| Labels per metric | 200 |
| Descriptor creation rate | 6,000/min/project |
| Write interval per series | Min 10 seconds |

The Telemetry API has its **own** quota, separate from the Cloud Monitoring API.

### Telemetry API — Traces

| Limit | Value |
|-------|-------|
| Write operations | 4,800/minute |
| Daily span ingestion | Unrestricted |
| Span name length | 1,024 bytes (vs 128 for legacy Trace API) |
| Attributes per span | 1,024 (vs 32 for legacy) |
| Events per span | 256 |
| Links per span | 128 |
| Attribute value size | 64 KiB |
| Span retention | 30 days |

> [Cloud Monitoring quotas](https://docs.cloud.google.com/monitoring/quotas)
> [Cloud Trace quotas](https://docs.cloud.google.com/trace/docs/quotas)

---

## Sampling Strategies

| Strategy | When | Trade-off |
|----------|------|-----------|
| AlwaysOn | Dev/staging, or under 2.5M spans/month | Full visibility, highest cost |
| ParentBased(TraceIdRatioBased(0.1)) | Production default | 10% sampling, respects upstream decisions |
| Custom rule-based | Mixed workloads | Always sample critical paths, low-rate sample health checks |
| Tail-based (Collector) | Need all errors but not all successes | Requires Collector with state |

Cloud Run does not apply sampling by default — it generates request-level spans automatically (non-chargeable), but application OTel SDK controls custom span sampling. The default sampler in .NET is `ParentBased(AlwaysOn)` when not explicitly configured.

The `traceparent` header's `sampled` flag propagates decisions across services. `ParentBased` sampler handles this automatically — if the upstream service decided to sample, downstream services respect that.

> [Trace sampling](https://docs.cloud.google.com/trace/docs/trace-sampling)

---

## Viewing Telemetry in Console

### Metrics

**Console > Monitoring > Metrics Explorer**

OTLP metrics appear with prefix `prometheus.googleapis.com/{metric_name}/{type}`. Can query via PromQL or MQL. Dashboard creation and alerting policies both supported. Metric retention: 24 months (full granularity for 6 weeks, then 10-minute intervals).

### Traces

**Console > Trace > Trace explorer**

Traces via OTLP get the expanded limits (1,024 attributes, 1,024-byte names). 30-day retention.

### Log Correlation

To correlate logs with traces in Cloud Logging, structured JSON logs must include:

| Field | Value |
|-------|-------|
| `logging.googleapis.com/trace` | `projects/PROJECT_ID/traces/TRACE_ID` |
| `logging.googleapis.com/spanId` | 16-character hex span ID |
| `logging.googleapis.com/traceSampled` | `true`/`false` |

Write structured JSON to stdout on Cloud Run — Cloud Logging picks it up. OTel's logging bridge auto-populates `TraceId`/`SpanId` from `Activity.Current`, but getting them into Google's field names requires a JSON formatter that maps them. No .NET-specific Google documentation exists for this.

> [Link log entries with traces](https://docs.cloud.google.com/trace/docs/trace-log-integration)
> [PromQL for Cloud Monitoring](https://cloud.google.com/monitoring/promql)

---

## Comparison: Configuration Approaches

| Dimension | Direct OTLP (current) | Collector Sidecar | Google-specific Exporter |
|-----------|----------------------|-------------------|--------------------------|
| .NET support | Full (custom auth handler) | Full (app → localhost) | **Does not exist for .NET** |
| Data fidelity | Native OTLP, no loss | Native OTLP, no loss | Transformation, possible loss |
| Auth complexity | Custom `DelegatingHandler` | Collector handles it | N/A |
| Operational cost | Lowest | Medium (second container) | N/A |
| Resource detection | Manual | Automatic | N/A |
| Multi-backend | No | Yes | No |
| Google recommendation | **Recommended** | Recommended for complex cases | **Deprecated** |

---

## Gaps

- **Logs via OTLP**: Google blogged about "native OTLP ingestion for all telemetry types" (Sept 2025) but `v1.logs` still doesn't exist as of March 2026. Timeline unknown.
- **Metrics Pre-GA**: The metrics path is still Preview with "limited support" disclaimers. Production use carries Pre-GA risk.
- **.NET documentation**: Google provides zero .NET-specific guidance. The `GoogleCloudTelemetryAuthHandler` pattern is community-derived.
- **Metric prefix under gRPC**: Docs describe `prometheus.googleapis.com/` prefix but examples focus on `otlphttp`. Whether gRPC OTLP produces the same prefix needs verification via actual billing/console.
- **Resource → monitored resource mapping**: Which OTel resource attributes are required for Google to map telemetry to `cloud_run_revision` (vs `generic_task`) is underdocumented for direct SDK export.
- **`OpenTelemetry.Resources.Gcp`**: Pre-release, not stable on NuGet. May break.
- **OTel SDK version requirement**: Metrics ingestion requires OTel SDK >= 0.140.0 (per Google docs). RepoQL uses 1.12.0 — version numbering schemes differ between OTel spec and .NET SDK; compatibility should be verified.
- **Silent ADC failure**: RepoQL's `CloudServiceOtlpExportConfiguration.TryCreate()` swallows all exceptions when ADC initialization fails, returning null (no export). A misconfigured service account silently loses all telemetry with no log entry.
- **Export interval default**: .NET SDK defaults to 60s (safe), but accidentally lowering below 10s causes silent point rejection with no error.

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Endpoint | `https://telemetry.googleapis.com` — standard OTLP, no vendor packages |
| Signals | Traces GA, Metrics Preview, Logs not supported |
| .NET approach | Generic OTLP exporter + custom `DelegatingHandler` for ADC auth |
| Protocol | gRPC recommended (token refresh); RepoQL already uses gRPC |
| Cloud Run auth | ADC via metadata server, automatic — needs `monitoring.metricWriter` + `cloudtrace.agent` roles |
| Resource attributes | Need enrichment beyond `service.name`/`service.version` for proper `cloud_run_revision` mapping |
| Cost | Well within free tier for RepoQL's current volume |
| Metric naming | `prometheus.googleapis.com/{name}/{type}` — dots preserved |
| Sampling | Default is `ParentBased(AlwaysOn)` — fine while under 2.5M spans/month |
| Old exporters | Deprecated — OTLP is the path forward |
