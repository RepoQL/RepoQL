# Date/Time Formatting Reference

Patterns for formatting and parsing dates across languages and APIs.

## Quick Reference

| Need | Format | Example |
|------|--------|---------|
| API exchange | ISO 8601 | `2024-03-15T14:30:00Z` |
| Human display | Locale-aware | `March 15, 2024` |
| Filenames | Sortable, no special chars | `2024-03-15_143000` |
| Logs | ISO 8601 with millis | `2024-03-15T14:30:00.123Z` |
| Database storage | UTC timestamp | `2024-03-15 14:30:00+00` |

---

## ISO 8601 Patterns

| Pattern | Output | Use |
|---------|--------|-----|
| `yyyy-MM-dd` | `2024-03-15` | Date only |
| `HH:mm:ss` | `14:30:00` | Time only (24h) |
| `yyyy-MM-ddTHH:mm:ssZ` | `2024-03-15T14:30:00Z` | UTC timestamp |
| `yyyy-MM-ddTHH:mm:ss±HH:mm` | `2024-03-15T14:30:00-07:00` | With timezone offset |
| `yyyy-MM-ddTHH:mm:ss.SSSZ` | `2024-03-15T14:30:00.123Z` | With milliseconds |

## Format Tokens

| Token | Meaning | Example |
|-------|---------|---------|
| `yyyy` | 4-digit year | `2024` |
| `MM` | 2-digit month | `03` |
| `dd` | 2-digit day | `15` |
| `HH` | 24-hour hour | `14` |
| `hh` | 12-hour hour | `02` |
| `mm` | Minutes | `30` |
| `ss` | Seconds | `00` |
| `SSS` | Milliseconds | `123` |
| `Z` | UTC indicator | `Z` |
| `±HH:mm` | Timezone offset | `-07:00` |

---

## Examples

```csharp
// C# - ISO 8601 for APIs
DateTime.UtcNow.ToString("o")                    // 2024-03-15T14:30:00.0000000Z
DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") // 2024-03-15T14:30:00Z

// C# - Parsing
DateTime.Parse("2024-03-15T14:30:00Z", null, DateTimeStyles.RoundtripKind)
```

```javascript
// JavaScript - ISO 8601 for APIs
new Date().toISOString()                         // 2024-03-15T14:30:00.123Z

// JavaScript - Parsing
new Date("2024-03-15T14:30:00Z")
```

```python
# Python - ISO 8601 for APIs
from datetime import datetime, timezone
datetime.now(timezone.utc).isoformat()           # 2024-03-15T14:30:00+00:00

# Python - Parsing
datetime.fromisoformat("2024-03-15T14:30:00+00:00")
```

```sql
-- PostgreSQL - Storage and formatting
SELECT NOW() AT TIME ZONE 'UTC'                  -- 2024-03-15 14:30:00
SELECT to_char(NOW(), 'YYYY-MM-DD"T"HH24:MI:SS"Z"')
```

---

## Gotchas

| Wrong | Right | Why |
|-------|-------|-----|
| `MM/dd/yyyy` for APIs | `yyyy-MM-dd` | Ambiguous internationally (03/15 vs 15/03) |
| Local time in APIs | UTC with `Z` suffix | Recipients don't know your timezone |
| `new Date("2024-03-15")` | `new Date("2024-03-15T00:00:00Z")` | Without time, JS assumes local midnight |
| Storing with timezone name | Store UTC, convert on display | Timezone rules change; UTC doesn't |
| `DateTime.Now` in C# | `DateTime.UtcNow` | `.Now` includes server's timezone |

---

## Defaults

- Always store and transmit in UTC
- Convert to local time only for display
- Include timezone offset when local time is intentional
- Use ISO 8601 for machine exchange; locale formatting for humans
