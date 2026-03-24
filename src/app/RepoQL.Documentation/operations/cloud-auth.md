# Cloud Auth

RepoQL cloud clients now support two authentication paths:

- OAuth session credentials via `cloud.auth_token` and `cloud.refresh_token`
- Legacy static bearer tokens via `cloud.api_key`

When OAuth credentials are present, RepoQL prefers them over `cloud.api_key`.

RepoQL also derives a local cloud auth status from those credentials so any part of the application can answer:

- Am I authenticated?
- Am I a paying cloud customer?
- Should paid cloud features such as contextual embeddings be active?

## Commands

- `repoql login`
  Starts the browser-based PKCE flow and stores the resulting session locally.
- `repoql login --device-code`
  Uses the RFC 8628 device authorization flow for SSH, WSL, or containers.
- `repoql logout`
  Clears locally stored session tokens.
- `repoql whoami`
  Shows the current session identity or API key hash prefix.

## Customer Status

RepoQL determines paid cloud access from locally available credential state:

- `cloud.api_key` counts as authenticated paid access
- OAuth sessions count as authenticated paid access when the session claims include an organization id
- A session without paid-customer claims is still authenticated, but paid cloud features stay disabled

This status is resolved locally from config, the persisted auth session, and JWT claims. RepoQL does not need to make a network round trip just to decide whether paid cloud features should be considered available.

## Embedding Model Selection

Paid cloud access affects embedding compatibility:

- If paid cloud access is available, RepoQL prefers the contextual cloud embedding model
- Existing local ONNX embeddings are preserved
- Documents that only have embeddings from an incompatible model are treated as still pending embedding
- Startup catch-up and later refresh passes will backfill compatible embeddings for the active model

This prevents a repository that was fully embedded with ONNX from being treated as fully embedded for semantic search after cloud access becomes available.

## Settings

- `cloud.client_id`
  Default WorkOS client ID used for refresh-token exchange.
- `cloud.client_secret`
  Required for refresh-token exchange. Sensitive.
- `cloud.auth_token`
  Current short-lived access token. Sensitive.
- `cloud.refresh_token`
  Current long-lived refresh token. Sensitive.
- `cloud.api_key`
  Legacy static bearer token path. Sensitive.

## Storage

- Access tokens are cached in memory and shared across processes via `~/.repoql/auth.json`
- Refresh tokens are stored in the OS credential store when available
- If the OS credential store is unavailable, RepoQL falls back to an encrypted `~/.repoql/.credentials` file and logs a warning
- ID tokens are stored alongside the access token in `~/.repoql/auth.json` for local identity display

## Failure Modes

- No stored credentials: `Not authenticated. Run: repoql login`
- Refresh token expired or revoked: `Session expired. Run: repoql login`
- Authentication service unreachable after one retry: `Cannot reach authentication service. Check your connection.`
