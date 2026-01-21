# Service Catalog Reference — Hybrid Pattern

This exemplar demonstrates the hybrid approach: CSVs for queryable facts and relationships, markdown for guidance.

## File Structure

```
newrelic/
├── services.csv              # Service facts, descriptions, notes
├── service-calls.csv         # Direct call relationships
├── service-events-publish.csv    # Event publishing
├── service-events-consume.csv    # Event consumption
└── service-catalog.md        # Guidance on using the data
```

---

## services.csv

```csv
service,account,environment,entity_guid,repository,entry_point,technology,purpose,database,logs_enabled,deployments_tracked,chronic_errors,notes
Identity,2673924,production-public,MjY3MzkyNHxBUE18QVBQTElDQVRJT058MTA4NTMyNDQ1OA,platform-services/services/Identity,source/Pushpay.Services.Identity.TokenApi,.NET 8,JWT generation and user authentication,DynamoDB (event sourcing - 18 tables),true,true,,"Critical foundation. Identity merging is IRREVERSIBLE."
Identity,2673924,production-internal,MjY3MzkyNHxBUE18QVBQTElDQVRJT058MTA4NTI4MDkzOA,platform-services/services/Identity,source/Pushpay.Services.Identity.GraphQLApi,.NET 8,JWT generation and user authentication,DynamoDB (event sourcing - 18 tables),true,true,,
Token,2673924,production-public,MjY3MzkyNHxBUE18QVBQTElDQVRJT058MTEwMjgwMzg5Nw,platform-services/services/Identity,OpenIddict endpoints,.NET 8,OAuth 2.0 token issuance,DynamoDB (shared with Identity),true,true,,"Separate entity but same codebase as Identity"
Organization,2673924,production,MjY3MzkyNHxBUE18QVBQTElDQVRJT058MTA4NTI4MTI0OA,platform-services/services/Organization,source/Pushpay.Services.Organization.Api,.NET 8,Merchant structure and entitlements,DynamoDB (event sourcing),true,true,,"Identity = who. Organization = what they can do."
Community,2673924,production,MjY3MzkyNHxBUE18QVBQTElDQVRJT058MTA4NTI4MTI0OA,platform-services/services/Community,source/Pushpay.Services.Community.Api,.NET 8,Person data management,DynamoDB,true,true,Timeout on ChMS sync (PLAT-1234),
```

**Queryable:**
- `WHERE chronic_errors != ''` — services with known issues
- `WHERE logs_enabled = 'false'` — observability gaps
- `WHERE notes LIKE '%IRREVERSIBLE%'` — dangerous operations

---

## service-calls.csv

```csv
caller,callee,reason,criticality
Identity,Okta,Group membership lookup,high
Identity,Auth0,User authentication,high
Identity,Organization,Role resolution,high
Token,Identity,Token validation,high
Organization,Identity,JWT validation,medium
Community,Identity,JWT validation,medium
Community,Organization,Entitlement checks,medium
CAP,Identity,JWT validation,high
CAP,Community,Person data,medium
CAP,Organization,Merchant data,medium
```

**Queryable:**
- `WHERE callee = 'Identity'` — what depends on Identity (blast radius)
- `WHERE caller = 'Community'` — what Community depends on
- `WHERE criticality = 'high'` — critical path dependencies

---

## service-events-publish.csv

```csv
publisher,event_type,description
Identity,IdentityCreated,New identity provisioned
Identity,IdentityMerged,Two identities combined (irreversible)
Community,PersonUpdated,Person record changed
Community,HouseholdChanged,Household membership modified
Organization,EntitlementChanged,Permission grant or revoke
```

**Queryable:**
- `WHERE publisher = 'Identity'` — events Identity emits
- `WHERE event_type LIKE '%Merged%'` — merge operations

---

## service-events-consume.csv

```csv
consumer,event_type,action
Community,IdentityCreated,Create person record
Community,IdentityMerged,Merge person records
CAP,PersonUpdated,Invalidate cache
Campaign,HouseholdChanged,Recalculate giving history
```

**Queryable:**
- `WHERE event_type = 'IdentityMerged'` — what reacts to identity merge
- `WHERE consumer = 'Community'` — what events Community listens to

---

## service-catalog.md

```markdown
# Service Catalog

Production services, their relationships, and event flows.

## Data Files

| File | Contains | Use For |
|------|----------|---------|
| `services.csv` | Service facts, GUIDs, notes | Finding a service, checking observability |
| `service-calls.csv` | Direct call relationships | Blast radius, dependency tracing |
| `service-events-publish.csv` | Event publishing | Understanding event sources |
| `service-events-consume.csv` | Event consumption | Understanding event handlers |

## Common Queries

**Find production GUID for a service:**
```sql
SELECT entity_guid FROM services.csv
WHERE service = 'Identity' AND environment = 'production-public'
```

**Blast radius — what breaks if Identity goes down:**
```sql
SELECT caller, reason FROM service-calls.csv
WHERE callee = 'Identity' AND criticality = 'high'
```

**Event flow — what happens when identity is merged:**
```sql
-- Who publishes it
SELECT * FROM service-events-publish.csv WHERE event_type = 'IdentityMerged'

-- Who reacts to it
SELECT consumer, action FROM service-events-consume.csv WHERE event_type = 'IdentityMerged'
```

**Services with known issues:**
```sql
SELECT service, chronic_errors FROM services.csv WHERE chronic_errors != ''
```

## Blast Radius Summary

Derived from `service-calls.csv WHERE criticality = 'high'`:

| Service Down | Direct Impact |
|--------------|---------------|
| Identity | All services (JWT validation) |
| Organization | Identity, all domain services |
| Auth0 | Identity (user auth) |
| Okta | Identity (staff auth) |

## Maintenance

**Adding a service:**
1. Add rows to `services.csv` (one per environment)
2. Add call relationships to `service-calls.csv`
3. Add event relationships if applicable
4. Document any chronic errors in the `chronic_errors` column

**Updating relationships:**
- Direct calls → `service-calls.csv`
- Event publishing → `service-events-publish.csv`
- Event consuming → `service-events-consume.csv`
```

---

## Why This Pattern

**All facts in CSV:**
- Services, GUIDs, descriptions, notes → queryable
- Direct calls → queryable relationships with criticality
- Events → queryable publish/subscribe flows

**Markdown provides:**
- How to query the data
- Pre-computed summaries (blast radius table)
- Maintenance procedures

**The boundary:** Data goes in CSV. Guidance goes in markdown.
