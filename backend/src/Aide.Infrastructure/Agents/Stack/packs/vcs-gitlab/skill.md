<!-- Adapted from stack-packs vcs-gitlab@1.0.0 skills/vcs-gitlab/SKILL.md, snapshot 2026-10-25 -->

# GitLab + GitLab CI

AIDE performs every Git and GitLab operation itself (branches, commits, pushes, merge requests, merges, CI/CD variables, deploy keys). You only write repository files. The project may be on gitlab.com or a self-managed instance (GitLab 17.11+).

## Detect
- Evidence in the file list: `.gitlab-ci.yml` and its `include:` entries (organisation templates: **extend them, never duplicate their jobs**), `.gitlab/merge_request_templates/`, `.gitlab/issue_templates/`, `renovate.json`, `CODEOWNERS` (root, `.gitlab/` or `docs/`).
- On an empty repo the scaffolder writes `.gitignore` (dependencies, build output, `.env`, `.env.*` except `.env.example`, IDE folders, test reports, CI cache folders `.npm/`, `.nuget/`, `.m2/`, `.uv-cache/`) and `.gitattributes` (`* text=auto eol=lf`). CI, Renovate and templates are left to the ci-engineer.

## Branches and commits
- AIDE names branches (`aide/**` version branches) and writes commit messages. Don't add conventions to files.
- Pipelines must run for merge request events and the default branch only, so AIDE reads one pipeline per MR head commit (no duplicate branch + MR pipelines).

## PRs/MRs (on request)
AIDE opens MRs as drafts, marks them ready and merges them (Maintainer role, protected default branch). Nothing to do here, except the template under Templates.

## CI (`.gitlab-ci.yml`)
```yaml
stages: [verify, build]
workflow:
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
default:
  interruptible: true
  timeout: 30m
```
One job per layer in the composition, stage `verify`, running that pack's **Gate** commands unchanged after `cd {fe}` / `cd {be}`. Use `rules: changes:` only together with the workflow rules above (path filters on MR pipelines compare against the target branch).

| Layer | Image | Cache |
| --- | --- | --- |
| Node | `node:<lts>` (match `.nvmrc`) | `.npm/` keyed on the lockfile (`npm ci --cache .npm --prefer-offline`) |
| .NET | `mcr.microsoft.com/dotnet/sdk:<ver>` (match `global.json`) | `NUGET_PACKAGES: $CI_PROJECT_DIR/.nuget/packages` |
| Java | `eclipse-temurin:<LTS>-jdk` or `maven:<ver>-eclipse-temurin-<LTS>` | `.m2/repository` (`MAVEN_OPTS: -Dmaven.repo.local=$CI_PROJECT_DIR/.m2/repository`) |
| Python | `ghcr.io/astral-sh/uv:python<ver>-bookworm-slim` | `UV_CACHE_DIR: .uv-cache` |

- **Reports:** `artifacts: { when: always, reports: { junit: <path>, coverage_report: { coverage_format: cobertura, path: <path> } } }`. Make each runner emit JUnit XML (`dotnet test` with the JunitXml logger, `vitest --reporter=junit`, pytest `--junitxml`, Surefire XML) by arguments in the job, without changing the pack's gate meaning.
- **Databases:** Testcontainers needs Docker in the job: a `docker:dind` service plus `DOCKER_HOST: tcp://docker:2375`, `DOCKER_TLS_CERTDIR: ""` and `TESTCONTAINERS_HOST_OVERRIDE: docker` (needs privileged runners; gitlab.com shared runners support it). Otherwise use `services: [postgres:<major>]` with `POSTGRES_*` variables and host `postgres`, and tell the test-engineer which env var the tests read.
- **Freshness checks** for every `generatedFiles` entry with a `ciCheck` (contract export + `git diff --exit-code`, client regeneration + diff, pending-model-change checks), and the data pack's `Migrations` check.
- **Lockfiles** are `ci_generated`: use `npm ci` only when `package-lock.json` exists (else `npm install`), `uv sync --locked` only when `uv.lock` exists, and keep a generated lockfile as an artifact.
- **Image builds** (stage `build`): build each Dockerfile with docker + dind, **no push**. No deploy jobs: platforms build from Git themselves.
- Use current stable image tags you are confident exist; pin runtime versions in their version files.

## Secrets and variables
- The gate needs no CI/CD variables. Ephemeral test DB values go in YAML mapping form (`POSTGRES_PASSWORD: postgres`), never `NAME=value` text.
- A job that needs a secret reads a masked, protected CI/CD variable by name; list the name in your step summary. Never put values in YAML.

## Dependency updates
GitLab has no Dependabot. Write `renovate.json` with `"extends": ["config:recommended"]` and a `packageRules` entry grouping minor and patch updates. Note in the step summary that a Renovate runner or the hosted app must be enabled by the user.

## Templates
`.gitlab/merge_request_templates/Default.md`: Summary, Requirement IDs, How it was tested, Contract changes, Migrations, Config and secrets.

## Safety
- Never write secret values. The review flags these patterns outside `*.example` files as critical: `PASSWORD=`, `SECRET=`, `API_KEY=`, `PRIVATE KEY`, `sk_live`, `service_role`.
- Jobs never print variables (`env`, `printenv`, `set -x` with secrets).
- No push, tag, release or deploy jobs.

## Checklist
- [ ] Organisation `include:` templates reused; workflow rules avoid duplicate pipelines.
- [ ] One job per layer running its pack's Gate, plus freshness checks, the migration check and image builds (no push).
- [ ] JUnit and coverage reports wired up; caches and timeouts set; works before lockfiles exist.
- [ ] MR template and Renovate present; no secret patterns outside `*.example`.
