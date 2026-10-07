## Summary

<!-- What changed and why. -->

## Requirement IDs

<!-- SPEC.md IDs covered, e.g. FR-3, NFR-2. -->

## How it was tested

- [ ] Gate passes (backend build/test/format, frontend typecheck/lint/test/build, `kubectl kustomize deploy/k8s/overlays/local`)
- [ ] New behaviour has tests named after the requirement IDs
- [ ] Redeployed to Docker Desktop (`./deploy/scripts/deploy-local.sh`) and the smoke test passes (if Kubernetes is available)
- [ ] E2E (`npm run test:e2e`), if UI flows changed

## Contract

- [ ] No API change
- [ ] API changed: `backend/openapi/api.json` and `frontend/src/api/generated/` regenerated and committed
- Breaking-change check (`oasdiff breaking`) result: <!-- none / details + SPEC.md decision -->

## Migrations

- [ ] No schema change
- [ ] New EF Core migration added and reviewed (no committed migration edited)

## Decisions / differences from the old stack

<!-- Anything logged under Decisions in PROGRESS.md; any old-stack feature dropped is an open question, not a decision. -->

## Checklist

- [ ] No secrets committed (only `*.example` files)
- [ ] SPEC.md updated if behaviour differs from it
