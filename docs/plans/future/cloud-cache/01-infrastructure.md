---
description: Pulumi infrastructure for the cloud embedding cache — GCS buckets, Cloud Tasks, IAM, Cloud Scheduler.
tags: [plan, cloud-cache, infrastructure, pulumi]
audience: { human: 40, agent: 60 }
categories: ["Plan[95%]", "Design[5%]"]
---

# Plan: Cloud Cache Infrastructure

Implements: [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Infrastructure (Pulumi) section

## Scope

**Covers:**
- Pulumi C# project with three stacks (`dev`, `staging`, `prod`)
- GCS embeddings bucket — Standard storage, uniform bucket-level access
- GCS staging bucket — Standard storage, 24h lifecycle policy
- Cloud Tasks queue for cache merge messages
- Cloud Scheduler job for nightly compaction trigger
- IAM service accounts with scoped permissions per service
- HMAC keys for DuckDB httpfs GCS access
- Secret Manager references for HMAC credentials

**Does not cover:**
- Cloud Run service deployments (Plans: 03-cache-layer, 04-writer, 05-compaction)
- Application code (Plans: 02 through 05)
- CI/CD pipeline for Pulumi deployments (follow-on)
- Monitoring and alerting infrastructure (follow-on)

## Enables

Once infrastructure exists:
- **Plans 03, 04, 05 can proceed** — they deploy to resources this plan creates
- **Dev environment available immediately** — developers can test against real GCS buckets
- **IAM boundaries enforced from day one** — no retroactive permission tightening

This is the foundation layer. All other cloud cache plans depend on it.

## Prerequisites

- GCP project with billing enabled
- Pulumi CLI installed and authenticated to GCP
- `Pulumi.Gcp` NuGet package available
- Decision on GCP project structure (single project vs per-environment)

## North Star

`pulumi up` creates everything needed for the cloud cache in a new environment. No manual GCP console steps, no undocumented IAM grants, no drift between environments.

## Done Criteria

### Pulumi Project

- The Pulumi project shall use C# with the `Pulumi.Gcp` provider
- The Pulumi project shall support three stacks: `dev`, `staging`, `prod`
- The Pulumi project shall parameterize resource names by stack name (e.g., `repoql-embeddings-dev`)

### GCS Buckets

- The embeddings bucket shall use Standard storage class with uniform bucket-level access
- The staging bucket shall use Standard storage class with uniform bucket-level access
- The staging bucket shall have a lifecycle rule deleting objects older than 1 day
- When the staging lifecycle rule fires, orphaned staging files shall be removed automatically

### Cloud Tasks

- The merge queue shall be configured with retry policy: max 5 attempts, 10s min backoff, 600s max backoff
- The merge queue shall be located in the same region as the Cloud Run services

### Cloud Scheduler

- The compaction scheduler shall trigger nightly at a configurable time
- The compaction scheduler shall target the compaction Cloud Run job endpoint

### IAM

- The embedding service account shall have read access to the embeddings bucket
- The embedding service account shall have write access to the staging bucket
- The embedding service account shall have enqueue permission on the Cloud Tasks queue
- The writer service account shall have read and delete access to the staging bucket
- The writer service account shall have read and write access to the embeddings bucket
- The compaction service account shall have read and write access to the embeddings bucket
- When a service account is compromised, it shall not have permissions beyond its role
  - The embedding service shall not write to the embeddings bucket
  - The writer shall not enqueue tasks or access Voyage credentials

### HMAC Keys

- Each service account with DuckDB GCS access shall have HMAC keys created via Pulumi
- HMAC credentials shall be stored in Secret Manager, not in Pulumi state
- The Pulumi stack shall output Secret Manager resource names for Cloud Run environment variable binding

### Outputs

- The Pulumi stack shall export bucket names, queue name, scheduler name, and service account emails
- The Pulumi stack shall export Secret Manager resource names for HMAC credentials

## Constraints

- **Pulumi C#, not Terraform** — design chose same language as codebase for type safety and tooling alignment
- **Standard storage, not Nearline** — design chose Standard for both buckets; read-heavy access pattern doesn't suit Nearline's retrieval fees
- **No Cloud Run deployments** — infrastructure only; application deployments are separate plans with separate lifecycles
- **Uniform bucket-level access** — design chose this over ACLs for simpler IAM reasoning

## References

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — infrastructure section with Pulumi code sketch
- [Pulumi GCP Provider](https://www.pulumi.com/registry/packages/gcp/) — `Pulumi.Gcp` NuGet package
- [GCS HMAC Keys](https://cloud.google.com/storage/docs/authentication/hmackeys) — for DuckDB httpfs authentication
- [Cloud Tasks](https://cloud.google.com/tasks/docs) — queue configuration and retry semantics

## Error Policy

Infrastructure provisioning is all-or-nothing per `pulumi up`. If any resource fails to create, Pulumi rolls back. No partial states to recover from.

For HMAC key rotation: create new key, update Secret Manager, restart services, delete old key. Pulumi tracks the key resource but credentials live in Secret Manager.
