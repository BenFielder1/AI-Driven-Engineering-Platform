<!-- Adapted from stack-packs vcs-github@1.0.0 skills/vcs-github/SKILL.md, snapshot 2026-10-25 -->

# GitHub + GitHub Actions

AIDE performs every Git and GitHub operation itself (branches, commits, pushes, PRs, merges, secrets, Pages settings). You only write repository files. Never describe or script remote actions.

## Detect
- Evidence in the file list: `.github/workflows/*.yml`, `.github/dependabot.yml`, `.github/pull_request_template.md`, `.github/ISSUE_TEMPLATE/`, `CODEOWNERS` (root, `.github/` or `docs/`).
- Existing workflows are extended, never replaced wholesale. Keep job names that branch protection may require.
- On an empty repo the scaffolder writes `.gitignore` (dependencies, build output, `.env`, `.env.*` except `.env.example`, IDE folders, test reports) and `.gitattributes` (`* text=auto eol=lf`). CI, Dependabot and templates are left to the ci-engineer.

## Branches and commits
- AIDE names branches (`aide/**` version branches) and writes commit messages. Don't add branch or commit conventions to files.
- CI must run on `pull_request` (any branch into the default branch) and on `push` to the default branch, so AIDE can read the PR head's check runs before merging. Don't trigger on every branch push as well (duplicate runs).

## PRs (on request)
AIDE opens and updates PRs and writes their bodies. Nothing to do here, except the template under Templates.

## CI (`.github/workflows/ci.yml`)
One workflow, one job per layer in the composition. Each job runs that layer pack's **Gate** commands, unchanged, in its directory (`defaults.run.working-directory: {fe}` / `{be}`). Use `dorny/paths-filter` or `on.pull_request.paths` only if every job still reports a status that branch protection can rely on.

| Layer runtime | Setup step | Cache |
| --- | --- | --- |
| Node (fe-*, be-express, be-nestjs) | `actions/setup-node` with `node-version-file` (`.nvmrc`) | `cache: npm` + `cache-dependency-path` |
| .NET (be-aspnet) | `actions/setup-dotnet` with `global-json-file`, then `dotnet tool restore` | NuGet via `actions/cache` keyed on `Directory.Packages.props` / `packages.lock.json` |
| Java (be-spring-boot) | `actions/setup-java` (`distribution: temurin`, version from the pack) | `cache: maven` or `gradle` |
| Python (be-fastapi) | `astral-sh/setup-uv` (+ `python-version-file`) | the action's built-in uv cache |

Also required:
- **Freshness checks** for every `generatedFiles` entry with a `ciCheck` in the composition's packs. Typical: export the contract and `git diff --exit-code {be}/openapi/api.json`; regenerate the client and `git diff --exit-code` its path; `dotnet ef migrations has-pending-model-changes`; `prisma migrate diff --exit-code`.
- **Migrations:** the data pack's `Migrations` check (apply to a fresh database in the integration tests, or its diff command).
- **Lockfiles** are `ci_generated`: they may not exist yet. Use `npm ci` when `package-lock.json` exists, else `npm install`; `uv sync --locked` only when `uv.lock` exists. Upload a lockfile the run generated as an artifact.
- **Containers:** `docker build` each Dockerfile in the repo, no push. Validate other deploy files offline only (JSON/TOML/YAML parse); no platform CLI calls.
- **Databases in tests:** Testcontainers works on `ubuntu-latest` (Docker is available). Alternatively use a `services: postgres:` container with the data pack's image and major version.
- **Reports:** upload test results and coverage with `actions/upload-artifact` (`if: always()`).
- `concurrency: { group: ci-${{ github.ref }}, cancel-in-progress: true }`, top-level `permissions: contents: read`, and `timeout-minutes` on each job.
- Use current stable action majors you are confident exist; pin Node/.NET/Java/Python versions in their version files, not in the workflow.
- **No deploy jobs.** Platforms build from Git themselves. The only deploy workflow is `.github/workflows/pages.yml`, owned by the deploy-engineer.

## Secrets and variables
- CI needs no repository secrets for the gate. Ephemeral test databases use fixed values in YAML mapping form (`POSTGRES_PASSWORD: postgres`), never `NAME=value` text.
- If a job really needs a secret, reference `${{ secrets.NAME }}` and list the name in your step summary. AIDE or the user sets it.

## Dependency updates
`.github/dependabot.yml` (version 2): one entry per ecosystem and directory (`npm` for each Node folder, `nuget`, `maven`/`gradle`, `uv` or `pip`, `docker` for each Dockerfile folder, `github-actions` at `/`), `schedule.interval: weekly`, and `groups` bundling minor and patch updates.

## Templates
- `.github/pull_request_template.md`: Summary, Requirement IDs, How it was tested, Contract changes, Migrations, Config and secrets.
- Issue templates only if the spec asks. `CODEOWNERS` only if the spec names owners.

## Safety
- Never write secret values. The review flags these patterns outside `*.example` files as critical: `PASSWORD=`, `SECRET=`, `API_KEY=`, `PRIVATE KEY`, `sk_live`, `service_role`.
- Workflows never echo secrets or `env`. Use `pull_request`, never `pull_request_target`, for repo code.
- No `git push`, release, tag or deploy steps in CI.

## Checklist
- [ ] One job per layer running its pack's Gate, plus freshness checks, the migration check and image builds.
- [ ] Least-privilege `permissions`, concurrency, job timeouts, and reports uploaded.
- [ ] Works before lockfiles exist.
- [ ] Dependabot and the PR template are present; no secret patterns outside `*.example`.
- [ ] No deploy, push or release steps.
