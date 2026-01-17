# Secret Handling Constraints

These constraints protect against credential exposure.

## Rules

- Never log secrets, tokens, or API keys
- Never commit secrets to version control
- Never include secrets in error messages
- Never pass secrets as URL parameters
- Always load secrets from environment or secret store

## Scope

**Applies to**: all services, all environments, all languages

**Does not apply to**: test fixtures using obviously fake values (e.g., `test-api-key-12345`)

## Enforcement

- Pre-commit hook: `scripts/scan-secrets.sh`
- CI pipeline: secret scanning on all PRs
- Runtime: log sanitizer strips known secret patterns

## Exceptions

None. No exception process exists for these rules.

If you believe you need an exception, you're likely solving the wrong problem. Ask in #security.
