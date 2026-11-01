<!-- Adapted from stack-packs vcs-bitbucket@1.0.0 skills/vcs-bitbucket/SKILL.md, snapshot 2026-10-25 -->

# Bitbucket Cloud + Bitbucket Pipelines

AIDE performs every Git and Bitbucket operation itself (branches, commits, pushes, PRs, merges, variables, deploy keys). You only write repository files.

## Detect
- Evidence in the file list: `bitbucket-pipelines.yml`, `renovate.json`, `docs/pr/TEMPLATE.md`, `CODEOWNERS`.
- Extend an existing pipeline (its `definitions` and anchors) rather than replacing it.
- On an empty repo the scaffolder writes `.gitignore` (dependencies, build output, `.env`, `.env.*` except `.env.example`, IDE folders, `test-results/`) and `.gitattributes` (`* text=auto eol=lf`). CI, Renovate and templates are left to the ci-engineer.

## Auth (for git and API)
AIDE holds the Bitbucket credentials (OAuth or scoped API tokens; app passwords no longer work). Generated files never contain tokens, usernames or clone URLs with credentials, and pipelines need no repository token for the gate.

## Branches and commits
- AIDE names branches (`aide/**` version branches) and writes commit messages. Don't add conventions to files.
- Pipelines run for pull requests and the default branch, so AIDE can read the PR head commit's statuses before merging.

## PRs (on request)
AIDE opens PRs as drafts and merges them. Bitbucket doesn't read a PR template from the repo, so keep `docs/pr/TEMPLATE.md` (see Templates).

## CI (`bitbucket-pipelines.yml`)
```yaml
image: atlassian/default-image:4
options:
  max-time: 30
definitions:
  caches:
    uv: ~/.cache/uv
  services:
    postgres:
      image: postgres:<major>
      variables:
        POSTGRES_DB: app_test
        POSTGRES_USER: app
        POSTGRES_PASSWORD: app
  steps:
    - step: &frontend
        name: Frontend gate
        image: node:<lts>
        caches: [node]
        script:
          - cd {fe}
          - <frontend gate commands, one per line>
    - step: &backend
        name: Backend gate
        image: <runtime image>
        caches: [<runtime cache>]
        services: [postgres]
        script:
          - cd {be}
          - <backend gate commands>
pipelines:
  pull-requests:
    '**':
      - parallel:
          - step: *frontend
          - step: *backend
  branches:
    main:
      - parallel:
          - step: *frontend
          - step: *backend
```
- **One step per layer** in the composition, running its pack's **Gate** unchanged. Images: `node:<lts>`, `mcr.microsoft.com/dotnet/sdk:<ver>`, `eclipse-temurin:<LTS>-jdk` or `maven:<…>`, python + uv. Use the default branch's real name under `branches`.
- **Caches:** predefined `node`, `dotnetcore`, `maven`, `gradle`, `pip`, plus custom ones (e.g. uv).
- **Databases:** Pipelines' Docker has no privileged mode, so Testcontainers usually fails. Use the `postgres` service (reached on `localhost:5432`) and pass its URL to the tests through an env var in the step; tell the test-engineer the variable name and that tests use it when `CI=true`.
- **Test reports:** write JUnit XML under `test-results/` so Pipelines picks it up.
- **Freshness checks** for every `generatedFiles` entry with a `ciCheck`, and the data pack's `Migrations` check.
- **Lockfiles** are `ci_generated`: `npm ci` only when `package-lock.json` exists (else `npm install`); `uv sync --locked` only when `uv.lock` exists; keep a generated lockfile as an artifact.
- **Image builds:** a step with `services: [docker]` running `docker build` for each Dockerfile, no push. No `deployment:` steps: platforms build from Git themselves.
- Use current stable images you are confident exist; pin runtime versions in their version files.

## Secrets and variables
- The gate needs no repository variables. The ephemeral `postgres` service uses fixed values in YAML mapping form, never `NAME=value` text.
- A step that needs a secret reads a secured repository variable by name; list the name in your step summary. Never put values in YAML.

## Dependency updates
`renovate.json` with `"extends": ["config:recommended"]` and a `packageRules` entry grouping minor and patch updates. Note in the step summary that the hosted Renovate app (or the organisation's runner) must be enabled by the user.

## Templates
`docs/pr/TEMPLATE.md`: Summary, Requirement IDs, How it was tested, Contract changes, Migrations, Config and secrets. Note in the step summary that the repository's default PR description is a manual setting.

## Safety
- Never write secret values. The review flags these patterns outside `*.example` files as critical: `PASSWORD=`, `SECRET=`, `API_KEY=`, `PRIVATE KEY`, `sk_live`, `service_role`.
- Steps never echo variables or tokens.
- No push, tag, merge or deploy steps.

## Checklist
- [ ] One step per layer running its pack's Gate, with a Pipelines service for the DB and JUnit reports in `test-results/`.
- [ ] Freshness checks, the migration check and image builds (no push) are present; works before lockfiles exist.
- [ ] No credentials in any file; Renovate and `docs/pr/TEMPLATE.md` present; no secret patterns outside `*.example`.
