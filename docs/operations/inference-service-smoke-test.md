# Inference Service Smoke Test

1. Deploy `repoql-inference` with `.github/workflows/deploy-inference.yml`.
2. Generate or reuse a client API key whose SHA-256 hash is stored in `repoql-embedding-auth-key-hash-0`.
3. Call `repoql.inference.v1.InferenceService/Complete` with:

```json
{
  "prompt": "Summarize the diff in two sentences.",
  "context": "diff --git a/example.txt b/example.txt\n+added line",
  "effort": "EFFORT_BALANCED",
  "max_tokens": 120
}
```

4. Send `Authorization: Bearer <client key>` as gRPC metadata.
5. Verify the response contains `content`, `usage`, and `model`, and that `model` is `grok-4-1-fast-non-reasoning` for balanced effort.
6. Repeat with `EFFORT_HIGH` and verify the model changes to `grok-4-1-fast-reasoning`.
