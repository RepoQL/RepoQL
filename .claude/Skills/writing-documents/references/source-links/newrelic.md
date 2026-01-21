# New Relic Deep Links

## Entity Permalink (Universal)

**Structure**: `https://one.newrelic.com/redirect/entity/{ENTITY_GUID}`

Works for any entity type. The GUID redirects to the appropriate view.

| Region | Base URL |
|--------|----------|
| US | `one.newrelic.com` |
| EU | `one.eu.newrelic.com` |

## Finding Entity GUIDs

- UI: Click entity icon → "See metadata & tags" → `entityGuid`
- NRQL: `SELECT entityGuid FROM ...`
- API: NerdGraph `entitySearch` query returns `guid` and `permalink`

## Common Launchers

| Type | Pattern |
|------|---------|
| APM Summary | `one.newrelic.com/nr1-core/apm/overview/{GUID}` |
| Errors Inbox | `one.newrelic.com/nr1-core/errors-inbox/entity/{GUID}` |
| Logs | `one.newrelic.com/launcher/logger.log-launcher` |
| Dashboard | `one.newrelic.com/dashboards/{DASHBOARD_GUID}` |

## Query Parameters

Logs launcher supports filtering:
```
?platform[accountId]={ACCOUNT_ID}
&platform[filters]=...
&platform[timeRange][duration]={MS}
```

## GUID Structure

GUIDs are Base64-encoded: `{accountId}|{domain}|{type}|{identifier}`

Example: `MXxBUE18QVBQTElDQVRJT058MjM` decodes to `1|APM|APPLICATION|23`

## Tip

Copy URLs from the UI—they include all necessary parameters. The `redirect/entity` pattern is most reliable for sharing.

> [New Relic Entity Docs](https://docs.newrelic.com/docs/new-relic-solutions/new-relic-one/core-concepts/what-entity-new-relic/)
