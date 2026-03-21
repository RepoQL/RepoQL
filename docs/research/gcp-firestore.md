# Google Cloud Firestore

Research for NoSQL document storage (Native mode).

*Research date: March 19, 2026*

## Context

RepoQL uses Firestore for product analytics — a `product-analytics` collection storing feedback events with type, sessionId, feedback text, diagnostics, version, platform, and server timestamp. Write-only pattern via `Google.Cloud.Firestore` .NET SDK. Gracefully degrades to log-only if not configured.

---

## Data Model

Documents (key-value maps) in collections. Subcollections for hierarchical data. Collections created implicitly on first write, deleted when empty.

### Data Types

String, Integer (64-bit), Float (64-bit), Boolean, Map, Array, Null, Timestamp (microsecond), GeoPoint, Reference, Bytes (≤1 MiB).

### Limits

| Limit | Value |
|-------|-------|
| Max document size | 1 MiB |
| Max field nesting | 20 levels |
| Max subcollection depth | 100 levels |
| Max index entries/document | 40,000 |

> [Data model](https://docs.cloud.google.com/firestore/native/docs/data-model)

---

## Querying

### Operators

`==`, `!=`, `<`, `<=`, `>`, `>=`, `array-contains`, `array-contains-any`, `in` (up to 30), `not-in` (up to 10).

### Query Types

| Type | Description |
|------|-------------|
| Simple | Single `where` clause |
| Composite | Multiple fields (requires composite index) |
| Collection group | Across all collections with same ID |
| OR | Via `or()`, `in`, or `array-contains-any` (max 30 disjunctions) |

Range/inequality filters on multiple fields supported (up to 10) but require composite indexes.

> [Queries](https://firebase.google.com/docs/firestore/query-data/queries)

---

## Indexes

| Type | Created | Scope |
|------|---------|-------|
| Single-field | Automatically (ascending + descending per field) | Collection |
| Composite | Manually | Collection or Collection Group |

Limits: 200 composite indexes (1,000 with billing), 100 fields per index, 40,000 entries per document.

Index exemptions available for fields that don't need querying (large strings, high-write fields).

> [Index types](https://firebase.google.com/docs/firestore/query-data/index-overview)

---

## Consistency

| Scenario | Consistency |
|----------|-------------|
| Single-document read | Strongly consistent |
| Multi-document query | **Strongly consistent** (all queries in Native mode) |
| Transactions | Serializable isolation, ACID |

This is a major advantage over legacy Datastore (which had eventual consistency for non-ancestor queries).

> [Native vs Datastore mode](https://docs.cloud.google.com/datastore/docs/firestore-or-datastore)

---

## Pricing (nam5 multi-region)

| Operation | Per 100,000 |
|-----------|-------------|
| Document reads | $0.06 |
| Document writes | $0.18 |
| Document deletes | $0.02 |
| Storage | $0.18/GiB/month |

### Free Tier (per project, default database only)

| Resource | Monthly |
|----------|---------|
| Reads | 50,000 |
| Writes | 20,000 |
| Deletes | 20,000 |
| Storage | 1 GiB |

Named (non-default) databases do NOT qualify for free tier. Multi-region costs ~2-3x more than single-region.

> [Firestore pricing](https://cloud.google.com/firestore/pricing)

---

## Quotas

| Resource | Limit |
|----------|-------|
| Sustained writes per document | 1/second (soft) |
| Writes per batch/transaction | 500 |
| Transaction timeout | 270 seconds |
| Max databases per project | 100 |
| Max API request size | 10 MiB |
| Max composite indexes (billing) | 1,000 |

**500/50/5 ramp-up rule**: Start at 500 ops/sec on new collection, increase max 50% every 5 minutes.

> [Quotas and limits](https://docs.cloud.google.com/firestore/quotas)

---

## TTL (Time-to-Live)

Designate a Timestamp field as TTL field on a collection group. Documents deleted typically within **24 hours** after expiration (not instantaneous). Expired-but-not-deleted documents still appear in queries.

TTL deletes count toward delete costs ($0.02/100K). Triggers Cloud Functions triggers.

Relevant for RepoQL: could auto-expire old feedback events via the `timestamp` field.

> [TTL policies](https://firebase.google.com/docs/firestore/ttl)

---

## .NET SDK (`Google.Cloud.Firestore` 3.10.0)

### Initialization

```csharp
FirestoreDb db = FirestoreDb.Create(projectId);
```

### Serialization

| Attribute | Purpose |
|-----------|---------|
| `[FirestoreData]` | Marks class for serialization |
| `[FirestoreProperty]` | Marks property for storage |
| `[ServerTimestamp]` | Server-side commit timestamp |

### Key Patterns

```csharp
// Write (dictionary — RepoQL's current pattern)
var doc = new Dictionary<string, object?>
{
    ["type"] = "feedback",
    ["timestamp"] = FieldValue.ServerTimestamp
};
await db.Collection("product-analytics").AddAsync(doc, ct);

// Batch writes (up to 500)
WriteBatch batch = db.StartBatch();
batch.Set(ref1, data1);
await batch.CommitAsync();

// Transactions (auto-retry, 5 attempts)
await db.RunTransactionAsync(async tx => { ... });
```

### Sentinel Values

`FieldValue.ServerTimestamp`, `FieldValue.Delete`, `FieldValue.ArrayUnion`, `FieldValue.ArrayRemove`, `FieldValue.Increment`.

> [.NET SDK reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Firestore/latest)

---

## Security: Rules vs IAM

| Mechanism | Applies To |
|-----------|------------|
| Security Rules | Firebase client SDKs (mobile/web) |
| IAM | Server client libraries (.NET, Java, etc.) |

Server SDKs **bypass Security Rules entirely**. For RepoQL's server-side use: IAM is the relevant mechanism. Roles: `roles/datastore.user` (read/write), `roles/datastore.viewer` (read-only).

---

## Native Mode vs Datastore Mode

| Feature | Native | Datastore |
|---------|--------|-----------|
| Real-time listeners | Yes | No |
| Consistency | Strongly consistent (all queries) | Strongly consistent |
| Client libraries | 11 (including mobile/web) | 8 (server only) |
| Recommended | All new applications | Legacy migrations |

Mode switch only possible with empty database.

---

## Best Practices

| Practice | Detail |
|----------|--------|
| Use auto-generated IDs | Prevents sequential hotspotting |
| Keep documents small | <1 KB for best perf |
| Subcollections for one-to-many | Not arrays in parent document |
| Index exemptions | For fields that don't need querying |
| `FieldValue.Increment` | Atomic counters without read-then-write |
| TTL for temporary data | Auto-expire with timestamp field |

---

## Gaps

- **Per-region pricing**: Only nam5 confirmed; single-region may be up to 50% cheaper
- **Listen() API in .NET**: Supported but examples not extracted
- **Firestore Enterprise**: MongoDB wire protocol tier not covered

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Usage | Write-only product analytics, auto-generated IDs |
| Consistency | Strongly consistent for all queries (Native mode) |
| Pricing | $0.18/100K writes; within free tier at current volume |
| .NET SDK | Dictionary + AddAsync + ServerTimestamp pattern |
| TTL opportunity | Could auto-expire old feedback events |
| Security | IAM-based (server SDK bypasses Security Rules) |
