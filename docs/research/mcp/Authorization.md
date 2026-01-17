# MCP Authorization

> OAuth 2.1-based authentication for HTTP transports

## Overview

MCP implements OAuth 2.1 authorization for HTTP-based transports, enabling clients to access protected MCP servers on behalf of users. This is only applicable to HTTP transports; stdio connections use the host's ambient permissions.

## Standards Foundation

| Standard | Purpose |
|----------|---------|
| OAuth 2.1 (IETF Draft) | Core authorization framework |
| RFC 8414 | Authorization Server Metadata |
| RFC 7591 | Dynamic Client Registration |
| RFC 9728 | Protected Resource Metadata |
| RFC 8707 | Resource Indicators |

## Roles

| Role | MCP Mapping |
|------|-------------|
| Resource Server | MCP Server (protected) |
| Client | MCP Client |
| Authorization Server | Handles authentication and tokens |
| Resource Owner | User |

## Authorization Flow

```
┌────────────────────────────────────────────────────────────────────┐
│                         Authorization Flow                          │
└────────────────────────────────────────────────────────────────────┘

MCP Client                 Auth Server                 MCP Server
    │                          │                           │
    │──────── Request ─────────────────────────────────────▶│
    │                                                       │
    │◀─────── 401 Unauthorized ────────────────────────────│
    │         WWW-Authenticate: Bearer                      │
    │         resource_metadata="..."                       │
    │                                                       │
    │──── Discovery ──────────▶│                           │
    │     (metadata endpoints)  │                           │
    │                          │                           │
    │◀─── Server Metadata ─────│                           │
    │                          │                           │
    │──── Authorization ──────▶│                           │
    │     (PKCE + scopes)      │                           │
    │                          │                           │
    │◀─── Auth Code ───────────│                           │
    │                          │                           │
    │──── Token Exchange ─────▶│                           │
    │     (code + PKCE)        │                           │
    │                          │                           │
    │◀─── Access Token ────────│                           │
    │                          │                           │
    │──────── Request + Bearer Token ──────────────────────▶│
    │                                                       │
    │◀─────── Response ────────────────────────────────────│
```

## Discovery

### Protected Resource Metadata

Discovered via `WWW-Authenticate` header or well-known endpoint:

```
GET /.well-known/oauth-protected-resource HTTP/1.1
Host: mcp.example.com
```

```json
{
  "resource": "https://mcp.example.com",
  "authorization_servers": ["https://auth.example.com"],
  "scopes_supported": ["mcp:read", "mcp:write", "mcp:admin"]
}
```

### Authorization Server Metadata

```
GET /.well-known/oauth-authorization-server HTTP/1.1
Host: auth.example.com
```

```json
{
  "issuer": "https://auth.example.com",
  "authorization_endpoint": "https://auth.example.com/authorize",
  "token_endpoint": "https://auth.example.com/token",
  "scopes_supported": ["mcp:read", "mcp:write"],
  "code_challenge_methods_supported": ["S256"]
}
```

## Client Registration

### Priority Order

1. **Client ID Metadata Documents** (recommended - no prior relationship)
2. **Pre-registration** (existing relationship)
3. **Dynamic Client Registration** (RFC 7591 fallback)

### Client Metadata Document

```json
{
  "client_id": "https://app.example.com/oauth/client-metadata.json",
  "client_name": "Example MCP Client",
  "client_uri": "https://app.example.com",
  "redirect_uris": [
    "http://127.0.0.1:3000/callback",
    "http://localhost:3000/callback"
  ],
  "grant_types": ["authorization_code"],
  "response_types": ["code"],
  "token_endpoint_auth_method": "none"
}
```

## Authorization Request

### Required: PKCE

MCP **MUST** use PKCE (Proof Key for Code Exchange) with S256:

```
GET /authorize?
  response_type=code&
  client_id=https://app.example.com/client-metadata.json&
  redirect_uri=http://127.0.0.1:3000/callback&
  scope=mcp:read%20mcp:write&
  state=xyz123&
  code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&
  code_challenge_method=S256&
  resource=https://mcp.example.com
```

### Required: Resource Parameter

Always include the `resource` parameter (RFC 8707):

```
&resource=https://mcp.example.com
```

**Valid URIs:**
- `https://mcp.example.com/mcp`
- `https://mcp.example.com`
- `https://mcp.example.com:8443`

**Invalid URIs:**
- `mcp.example.com` (missing scheme)
- `https://mcp.example.com#fragment` (contains fragment)

## Token Exchange

```http
POST /token HTTP/1.1
Host: auth.example.com
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code&
code=AUTH_CODE&
redirect_uri=http://127.0.0.1:3000/callback&
client_id=https://app.example.com/client-metadata.json&
code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk&
resource=https://mcp.example.com
```

### Token Response

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIs...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "dGhpcyBpcyBhIHJlZnJlc2ggdG9rZW4...",
  "scope": "mcp:read mcp:write"
}
```

## Using Access Tokens

### Bearer Token Format

```http
GET /mcp HTTP/1.1
Host: mcp.example.com
Authorization: Bearer eyJhbGciOiJSUzI1NiIs...
MCP-Protocol-Version: 2025-11-25
```

**Requirements:**
- Use `Authorization` header only
- **Never** include tokens in URI query strings
- Include in every request

## Error Handling

### HTTP 401 - Unauthorized

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer realm="mcp",
                         resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource"
```

### HTTP 403 - Insufficient Scope

```http
HTTP/1.1 403 Forbidden
WWW-Authenticate: Bearer error="insufficient_scope",
                         scope="mcp:write mcp:admin",
                         error_description="Write permission required"
```

### Step-Up Authorization

When receiving 403 with scope challenge:

1. Parse required scopes from response
2. Initiate new authorization with additional scopes
3. Exchange code for new token
4. Retry original request (limit attempts)

## Token Validation

MCP servers **MUST**:

| Requirement | Description |
|-------------|-------------|
| Validate tokens | Before processing any request |
| Verify audience | Token was issued for this server |
| Check expiration | Reject expired tokens |
| Validate issuer | Token from expected auth server |

## Security Requirements

### PKCE

- **MUST** implement PKCE
- **MUST** use S256 challenge method
- Verify server supports via `code_challenge_methods_supported`

### Communication

- All auth endpoints **MUST** use HTTPS
- Redirect URIs **MUST** be localhost or HTTPS
- Verify `state` parameter in responses

### Token Security

- Never accept tokens across service boundaries
- Validate token audience (`aud` claim)
- Don't expose tokens in logs or errors

### Client Metadata Security

- Consider SSRF risks when fetching metadata
- Warn about localhost-only redirect URIs
- Display metadata prominently to prevent phishing

## Scope Strategy

### Selection Priority

1. Use `scope` from `WWW-Authenticate` header (401 response)
2. Fall back to `scopes_supported` from Protected Resource Metadata
3. Request minimum necessary scopes (least privilege)

### Common Scopes

| Scope | Description |
|-------|-------------|
| `mcp:read` | Read-only access |
| `mcp:write` | Read and write access |
| `mcp:admin` | Administrative access |

## Implementation Checklist

### Client Implementation

- [ ] Discover Protected Resource Metadata
- [ ] Discover Authorization Server Metadata
- [ ] Implement PKCE with S256
- [ ] Include `resource` parameter in all requests
- [ ] Handle token refresh
- [ ] Implement step-up authorization

### Server Implementation

- [ ] Serve Protected Resource Metadata
- [ ] Validate all tokens before processing
- [ ] Verify token audience
- [ ] Return proper `WWW-Authenticate` headers
- [ ] Implement scope-based access control
