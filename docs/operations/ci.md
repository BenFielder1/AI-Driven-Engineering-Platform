---
title: CI and the commit gate
parent: Operations
nav_order: 4
---

# CI and the commit gate

GitHub Actions (`.github/workflows/ci.yml`) runs on pull requests, pushes to `main` and `build/**`, and manual dispatch. Changes only to `docs/**`, `.claude/**` or `*.md` are skipped. The docs site has its own workflow, `.github/workflows/docs.yml`, which builds it and publishes it to GitHub Pages.

| Job | Checks |
| --- | --- |
| backend | Release build with warnings as errors, `openapi/api.json` up to date, no pending EF model changes, CLI packs, tests (Testcontainers), `dotnet format` |
| backend-integration | Integration tests (Testcontainers), sharded |
| frontend | Client regenerated and unchanged, typecheck, lint, tests, build |
| manifests | `validate-manifests.sh`: Kustomize render plus kubeconform for both overlays (`local`, `local-e2e`) |
| scripts | `bash -n` on the shell scripts, shellcheck at error severity |
| railway-workflows | Typecheck of `.railway`, actionlint on the workflows |
| images | `api` and `web` images build (no push) |
| e2e | Three shards on `chromium` against the API (E2E mode: Fake LLM, Fake deployments, Local Git with a seeded import repo) and the Next.js dev server: `v1` specs, `v2` specs (versions, import, preset), and `a11y` |

CI uses no secrets and no real LLM, Git host, deployment platform or database access. The Firefox, WebKit and Edge projects and the perf scripts run locally or against Kubernetes. The E2E shards.

## The commit gate

The gate is what CI runs:

```bash
(cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes) && (cd frontend && npm run typecheck && npm run lint && npm test && npm run build) && ./deploy/scripts/validate-manifests.sh
```

Agents don't run it locally. Docker, Testcontainers and E2E need more memory than a development machine may have. Instead, for every phase or fix:

1. Review the diff statically (no tests, Docker or deploys).
2. Run only the commands that produce committed files, or a single quick check on the touched layer.
3. Commit and push to the build branch. Never push to `main`, never force-push.
4. Wait for CI on that commit with `gh run watch`, and read failures with `gh run view <id> --log-failed`.
5. Fix and repeat until the head commit's run is green. CI skips documentation-only commits, so the latest code commit's run counts.
6. List anything that needs a manual Docker Desktop check for the owner to run.

On a machine with enough memory you can still run the gate yourself.
