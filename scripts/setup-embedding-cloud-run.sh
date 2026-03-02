#!/usr/bin/env bash
# One-time setup for deploying the RepoQL embedding service to Cloud Run.
#
# Prerequisites:
#   - gcloud CLI installed and authenticated (gcloud auth login)
#   - A GCP project created
#   - gh CLI installed (for setting GitHub secrets/variables)
#
# Usage:
#   ./scripts/setup-embedding-cloud-run.sh <GCP_PROJECT_ID> [REGION]
#
# Example:
#   ./scripts/setup-embedding-cloud-run.sh my-gcp-project us-central1

set -euo pipefail

PROJECT_ID="${1:?Usage: $0 <GCP_PROJECT_ID> [REGION]}"
REGION="${2:-us-central1}"
GITHUB_REPO="stueeey/RepoQL"

SERVICE_NAME="repoql-embedding"
AR_REPO="repoql"
WIF_POOL="github-pool"
WIF_PROVIDER="github-provider"
SA_NAME="github-actions-sa"
SA_EMAIL="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

echo "=== RepoQL Embedding Service — Cloud Run Setup ==="
echo "  Project:  ${PROJECT_ID}"
echo "  Region:   ${REGION}"
echo "  Repo:     ${GITHUB_REPO}"
echo ""

# ── 1. Enable APIs ──────────────────────────────────────────────────────────
echo "→ Enabling APIs..."
gcloud services enable \
  run.googleapis.com \
  artifactregistry.googleapis.com \
  secretmanager.googleapis.com \
  iam.googleapis.com \
  iamcredentials.googleapis.com \
  --project="${PROJECT_ID}"

# ── 2. Create Artifact Registry repository ──────────────────────────────────
echo "→ Creating Artifact Registry repository '${AR_REPO}'..."
if gcloud artifacts repositories describe "${AR_REPO}" \
     --location="${REGION}" --project="${PROJECT_ID}" &>/dev/null; then
  echo "  Already exists, skipping."
else
  gcloud artifacts repositories create "${AR_REPO}" \
    --repository-format=docker \
    --location="${REGION}" \
    --project="${PROJECT_ID}"
fi

# ── 3. Create secrets ───────────────────────────────────────────────────────
create_secret_if_missing() {
  local name="$1"
  if gcloud secrets describe "${name}" --project="${PROJECT_ID}" &>/dev/null; then
    echo "  Secret '${name}' already exists, skipping."
    return 1
  fi
  return 0
}

echo "→ Creating secrets..."

# Voyage API key
if create_secret_if_missing "repoql-embedding-voyage-api-key"; then
  read -rsp "  Enter your Voyage AI API key: " VOYAGE_KEY
  echo ""
  echo -n "${VOYAGE_KEY}" | gcloud secrets create repoql-embedding-voyage-api-key \
    --data-file=- --project="${PROJECT_ID}"
fi

# Bearer token for client auth — generate token, store hash
if create_secret_if_missing "repoql-embedding-auth-key-hash-0"; then
  BEARER_TOKEN=$(openssl rand -hex 32)
  HASH=$(echo -n "${BEARER_TOKEN}" | sha256sum | cut -d' ' -f1)
  echo -n "${HASH}" | gcloud secrets create repoql-embedding-auth-key-hash-0 \
    --data-file=- --project="${PROJECT_ID}"
  echo ""
  echo "  ┌─────────────────────────────────────────────────────────────┐"
  echo "  │ SAVE THIS — your embedding service bearer token:           │"
  echo "  │                                                             │"
  echo "  │  ${BEARER_TOKEN}  │"
  echo "  │                                                             │"
  echo "  │ Put this in your repoql.json:                               │"
  echo "  │   \"embedding\": { \"remote\": { \"apiKey\": \"<token>\" } }        │"
  echo "  └─────────────────────────────────────────────────────────────┘"
  echo ""
fi

# ── 4. Grant Cloud Run default SA access to secrets ─────────────────────────
echo "→ Granting secret access to Cloud Run default service account..."
PROJECT_NUMBER=$(gcloud projects describe "${PROJECT_ID}" --format='value(projectNumber)')
COMPUTE_SA="${PROJECT_NUMBER}-compute@developer.gserviceaccount.com"

for SECRET in repoql-embedding-voyage-api-key repoql-embedding-auth-key-hash-0; do
  gcloud secrets add-iam-policy-binding "${SECRET}" \
    --member="serviceAccount:${COMPUTE_SA}" \
    --role="roles/secretmanager.secretAccessor" \
    --project="${PROJECT_ID}" \
    --quiet
done

# ── 5. Create GitHub Actions service account ────────────────────────────────
echo "→ Creating service account '${SA_NAME}'..."
if gcloud iam service-accounts describe "${SA_EMAIL}" --project="${PROJECT_ID}" &>/dev/null; then
  echo "  Already exists, skipping."
else
  gcloud iam service-accounts create "${SA_NAME}" \
    --display-name="GitHub Actions (RepoQL)" \
    --project="${PROJECT_ID}"
  echo "  Waiting for service account to propagate..."
  sleep 10
fi

echo "→ Granting roles to ${SA_EMAIL}..."
for ROLE in roles/artifactregistry.writer roles/run.admin roles/iam.serviceAccountUser; do
  gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="${ROLE}" \
    --quiet
done

# ── 6. Set up Workload Identity Federation ──────────────────────────────────
echo "→ Creating Workload Identity Pool '${WIF_POOL}'..."
if gcloud iam workload-identity-pools describe "${WIF_POOL}" \
     --location=global --project="${PROJECT_ID}" &>/dev/null; then
  echo "  Already exists, skipping."
else
  gcloud iam workload-identity-pools create "${WIF_POOL}" \
    --location=global \
    --display-name="GitHub Actions" \
    --project="${PROJECT_ID}"
fi

echo "→ Creating OIDC provider '${WIF_PROVIDER}'..."
if gcloud iam workload-identity-pools providers describe "${WIF_PROVIDER}" \
     --location=global --workload-identity-pool="${WIF_POOL}" \
     --project="${PROJECT_ID}" &>/dev/null; then
  echo "  Already exists, skipping."
else
  gcloud iam workload-identity-pools providers create-oidc "${WIF_PROVIDER}" \
    --location=global \
    --workload-identity-pool="${WIF_POOL}" \
    --issuer-uri="https://token.actions.githubusercontent.com" \
    --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository" \
    --attribute-condition="assertion.repository_owner == 'stueeey'" \
    --project="${PROJECT_ID}"
fi

echo "→ Allowing GitHub to impersonate ${SA_EMAIL}..."
gcloud iam service-accounts add-iam-policy-binding "${SA_EMAIL}" \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${WIF_POOL}/attribute.repository/${GITHUB_REPO}" \
  --quiet

# ── 7. Get the full WIF provider path for GitHub secrets ────────────────────
WIF_PROVIDER_FULL="projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${WIF_POOL}/providers/${WIF_PROVIDER}"

# ── 8. Set GitHub repository secrets and variables ──────────────────────────
echo "→ Setting GitHub repository secrets and variables..."
gh variable set GCP_PROJECT_ID --body "${PROJECT_ID}" --repo "${GITHUB_REPO}"
gh secret set GCP_WIF_PROVIDER --body "${WIF_PROVIDER_FULL}" --repo "${GITHUB_REPO}"
gh secret set GCP_SERVICE_ACCOUNT --body "${SA_EMAIL}" --repo "${GITHUB_REPO}"

echo ""
echo "=== Setup complete ==="
echo ""
echo "  Artifact Registry:  ${REGION}-docker.pkg.dev/${PROJECT_ID}/${AR_REPO}"
echo "  Cloud Run service:  ${SERVICE_NAME} (will be created on first deploy)"
echo "  WIF provider:       ${WIF_PROVIDER_FULL}"
echo "  Service account:    ${SA_EMAIL}"
echo ""
echo "  Next: trigger the 'deploy-embedding-service' workflow from GitHub Actions."
