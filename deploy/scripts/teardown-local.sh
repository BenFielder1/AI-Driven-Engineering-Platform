#!/usr/bin/env bash
# Remove the AIDE app from Docker Desktop Kubernetes (context docker-desktop only).
#   default:      deletes workloads, services, config and secrets; KEEPS the namespace and PVCs (DB + aide-data).
#   --wipe-data:  additionally deletes the 'aide' namespace and all its data, after typing the namespace to confirm.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

WIPE=0
for arg in "$@"; do
  case "$arg" in
    --wipe-data) WIPE=1 ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg" ;;
  esac
done

step "Checking the '$CTX' context"
require_context

if ! $K get namespace "$NS" >/dev/null 2>&1; then
  info "Namespace $NS does not exist; nothing to do."
  exit 0
fi

step "Deleting app resources in namespace $NS (PVCs kept)"
$K -n "$NS" delete deployment,statefulset,service,job,configmap,secret \
  -l app.kubernetes.io/part-of=aide --ignore-not-found --wait=true

if [[ $WIPE -eq 1 ]]; then
  step "--wipe-data: this deletes namespace '$NS' including the database and aide-data volumes"
  $K -n "$NS" get pvc || true
  read -r -p "Type '$NS' to permanently delete all data: " answer
  if [[ "$answer" == "$NS" ]]; then
    $K delete namespace "$NS" --wait=true
    info "Namespace and data deleted."
  else
    info "Confirmation did not match; data kept."
  fi
else
  $K -n "$NS" get pvc || true
  info "Data kept. Re-deploy with ./deploy/scripts/deploy-local.sh"
fi
