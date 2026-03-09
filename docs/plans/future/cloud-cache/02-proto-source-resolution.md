---
description: Add source field to EmbedChunks proto and implement URL normalization on the host side.
tags: [plan, cloud-cache, proto, grpc, source-resolution]
audience: { human: 35, agent: 65 }
categories: ["Plan[95%]", "Design[5%]"]
---

# Plan: Proto & Source Resolution

Implements: [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Proto Changes, Source Identity sections

## Scope

**Covers:**
- Add `source` field to `EmbedChunksRequest` in `embedding.proto`
- `SourceNormalizer` utility — normalize git remote URLs to canonical form
- Host-side resolution — resolve the current repo's canonical origin before gRPC calls
- Populate `request.Source` in the host's embedding client
- Tests for normalization across all URL formats

**Does not cover:**
- Cache layer in the embedding service (Plan: 03-cache-layer)
- Infrastructure (Plan: 01-infrastructure)
- Any service-side changes — the service receives `source` but this plan doesn't use it yet

## Enables

Once proto and source resolution exist:
- **Plan: 03-cache-layer** can proceed — the `source` field is available in `EmbedChunksRequest`
- **Backward-compatible deployment** — service can be deployed before or after the host; empty `source` skips cache
- **Source identity stable across all customers** — same repo always produces the same `source` string

## Prerequisites

- `embedding.proto` accessible in `RepoQL.Embedding.Proto`
- Host-side embedding client that constructs `EmbedChunksRequest`
- Git remote URL available from the host's repository context

## North Star

Any git remote URL — HTTPS, SSH, `github://` scheme, any host — normalizes to one canonical string. Same repo = same string = same cache shard. Always.

## Done Criteria

### Proto

- The `EmbedChunksRequest` shall include a `string source = 2` field
- When `source` is empty, the embedding service shall skip the cloud cache
- The proto change shall be additive — existing clients that don't set `source` get empty string, no breaking change

### SourceNormalizer

- The normalizer shall strip URL scheme (`https://`, `git@`, `github://`, `ssh://`)
- The normalizer shall strip `.git` suffix
- The normalizer shall strip authentication credentials from URLs
- The normalizer shall convert SSH colon syntax (`host:path`) to slash (`host/path`)
- The normalizer shall lowercase the result
- The normalizer shall preserve the full path after the host (not assume `owner/repo` structure)
- When given a `file://` URL without a git remote, return empty string
- When given a `github://org/repo` URL, return `github.com/org/repo`

**Normalization truth table:**

| Input | Output |
|-------|--------|
| `https://github.com/org/repo.git` | `github.com/org/repo` |
| `git@github.com:org/repo.git` | `github.com/org/repo` |
| `github://org/repo` | `github.com/org/repo` |
| `ssh://git@github.com/org/repo` | `github.com/org/repo` |
| `https://user:token@github.com/org/repo.git` | `github.com/org/repo` |
| `https://gitlab.com/org/repo` | `gitlab.com/org/repo` |
| `https://bitbucket.org/org/repo` | `bitbucket.org/org/repo` |
| `https://dev.azure.com/org/proj/_git/repo` | `dev.azure.com/org/proj/_git/repo` |
| `https://gitea.company.com/team/repo` | `gitea.company.com/team/repo` |
| `file:///local/path` (no remote) | `` (empty) |

### Host Integration

- The host shall resolve the canonical source from the current repo's git remote
- The host shall populate `request.Source` on every `EmbedChunks` call
- When no git remote is configured, the host shall send empty `source`
- The source resolution shall happen once at startup (or repo open), not per-request

## Constraints

- **One proto field only** — design explicitly chose minimal host change
- **Host resolves, service hashes** — the host sends the human-readable normalized URL; the service SHA256-hashes it for the GCS path. This keeps debugging possible (logs show `github.com/org/repo`, not a hash)
- **No scheme registry assumptions** — must work with any git host, not just GitHub/GitLab/Bitbucket
- **`github://` scheme maps to `github.com`** — this is a RepoQL convention for imported repos

## References

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Proto Changes and Source Identity sections
- [`embedding.proto`](../../../src/RepoQL.Embedding.Proto/Protos/embedding.proto) — existing proto definition
- [`EmbeddingServiceImpl.cs`](../../../src/RepoQL.Embedding.Service/EmbeddingServiceImpl.cs) — service that receives the request

## Error Policy

Source resolution failures are non-fatal. If the git remote can't be read or parsed, send empty `source` — the service skips the cloud cache and calls Voyage directly. Log a warning so the issue is visible but never block embedding.
