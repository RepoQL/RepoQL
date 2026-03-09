#!/usr/bin/env bash
set -euo pipefail

# Bootstrap script for out-of-band permissions required before Pulumi and
# deploy workflows can run. These are chicken-and-egg: the CI/CD pipeline
# needs them to function, so they can't be managed by the pipeline itself.
#
# Run once per GCP project, with your own credentials (not the WIF SA).
#
# Usage: ./bootstrap.sh <environment> <gcp-project-id>
# Example: ./bootstrap.sh dev repoql-dev

ENV="${1:?Usage: bootstrap.sh <environment> <gcp-project-id>}"
PROJECT="${2:?Usage: bootstrap.sh <environment> <gcp-project-id>}"

# The WIF service account used by GitHub Actions.
# Lives in the production project, operates cross-project.
WIF_SA="github-actions-sa@repoql-production.iam.gserviceaccount.com"

echo "Bootstrapping project '${PROJECT}' for environment '${ENV}'"
echo "WIF service account: ${WIF_SA}"
echo ""

# --- Required GCP APIs ---

echo "Enabling required APIs..."
gcloud services enable \
  artifactregistry.googleapis.com \
  cloudresourcemanager.googleapis.com \
  cloudscheduler.googleapis.com \
  cloudtrace.googleapis.com \
  compute.googleapis.com \
  eventarc.googleapis.com \
  monitoring.googleapis.com \
  pubsub.googleapis.com \
  run.googleapis.com \
  secretmanager.googleapis.com \
  storage.googleapis.com \
  --project="${PROJECT}"

# --- WIF SA project-level roles ---
# These grant the GitHub Actions SA the permissions it needs to:
# - Run Pulumi (IAM management, monitoring dashboards)
# - Deploy Cloud Run services
# - Push container images

echo ""
echo "Granting WIF SA project-level roles..."

ROLES=(
  "roles/artifactregistry.admin"      # Create repos + push images
  "roles/iam.serviceAccountUser"      # Act as Cloud Run service accounts
  "roles/monitoring.admin"            # Create/update dashboards
  "roles/resourcemanager.projectIamAdmin"  # Manage IAM bindings via Pulumi
  "roles/run.admin"                   # Deploy Cloud Run services
)

for ROLE in "${ROLES[@]}"; do
  echo "  ${ROLE}"
  gcloud projects add-iam-policy-binding "${PROJECT}" \
    --member="serviceAccount:${WIF_SA}" \
    --role="${ROLE}" \
    --quiet > /dev/null
done

# --- Activate GCS service agent ---
# Required for Eventarc (GCS → Pub/Sub notifications).

echo ""
echo "Activating GCS service agent..."
gcloud storage service-agent --project="${PROJECT}"

echo ""
echo "Bootstrap complete. You can now run:"
echo "  1. deploy-cloud-cache-infra workflow (Pulumi)"
echo "  2. deploy-embedding-service workflow"
echo "  3. deploy-embedding-writer workflow"
