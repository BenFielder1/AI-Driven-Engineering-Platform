# Build progress (v4)

Spec: `SPEC.md` (v4; v1–v3 in `docs/`) · Stack: `STACK.md` (unchanged from v3) · Branch: `build/aide-v4` · Started: 2026-10-25

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

## Environment
- Packs (STACK.md): `fe-nextjs`, `be-aspnet`, `data-efcore-postgres`, `vcs-github`, `deploy-railway`, `local-docker-desktop-k8s`. All installed.
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben).
- VCS CLI auth: gh yes (BenFielder1). Railway deploys run only through `.github/workflows/deploy.yml`.

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-113 | Stack catalog (versioned data, `GET /api/stack/catalog`) | 3.1 | backend + data | M24 | todo | — |
| FR-114 | Wizard Stack step: five ordered sub-steps | 3.1 | frontend + backend | M25 | todo | — |
| FR-115 | Deployment sub-step: per-component hosts, origin model | 3.1 | frontend + backend | M25 | todo | — |
| FR-116 | Server-side composition validation | 3.1 | backend | M24 | todo | — |
| FR-117 | Composition pinned on release version; stack change; `STACK_LOCKED` | 3.1 | backend + frontend + data | M25 | todo | — |
| FR-118 | Import stack detection | 3.1 | backend + frontend | M25 | todo | — |
| FR-119 | Layer paths | 3.1 | backend | M25 | todo | — |
| FR-120 | Generic specialists catalogue | 3.2 | backend | M26 | todo | — |
| FR-121 | Specialists load pack skills | 3.2 | backend | M26 | todo | — |
| FR-122 | Adapted pack content; strict catalog loading | 3.2 | backend | M24 | todo | — |
| FR-123 | Spec Generator: composed spec sections | 3.3 | backend | M26 | todo | — |
| FR-124 | Plan Generator: composed plan rules | 3.3 | backend | M26 | todo | — |
| FR-125 | Execution: Guardian protected paths from packs | 3.3 | backend | M26 | todo | — |
| FR-126 | Code Review: composed checklist + deterministic checks | 3.3 | backend | M26 | todo | — |
| FR-127 | Generated-files policy | 3.3 | backend | M26 | todo | — |
| FR-128 | Repository Analyst + refinement get the composition | 3.3 | backend | M26 | todo | — |
| FR-129 | Retire v2 presets (migration + removal) | 3.4 | backend + frontend + data | M25 | todo | — |
| FR-130 | GitLab instances (Admin) | 3.5 | backend + frontend + data | M27 | todo | — |
| FR-131 | GitLab connections + sign-in | 3.5 | backend + frontend + data | M27 | todo | — |
| FR-132 | GitLab repo picker + create | 3.5 | backend + frontend | M27 | todo | — |
| FR-133 | Bitbucket OAuth consumer (Admin) | 3.6 | backend + frontend + data | M28 | todo | — |
| FR-134 | Bitbucket connections + sign-in | 3.6 | backend + frontend + data | M28 | todo | — |
| FR-135 | Bitbucket repo picker + create | 3.6 | backend + frontend | M28 | todo | — |
| FR-136 | Git provider set; binding Owner's connection | 3.7 | backend + data | M27/M28 | todo | — |
| FR-137 | MRs/PRs on GitLab and Bitbucket | 3.7 | backend | M27/M28 | todo | — |
| FR-138 | Merge readiness on GitLab and Bitbucket | 3.7 | backend | M27/M28 | todo | — |
| FR-139 | CI status on every host | 3.7 | backend | M27/M28 | todo | — |
| FR-140 | Release on GitLab and Bitbucket | 3.7 | backend | M27/M28 | todo | — |
| FR-141 | Import from GitLab and Bitbucket | 3.7 | backend + frontend | M27/M28 | todo | — |
| FR-142 | Platform connections (generic; Vercel migrated) | 3.8 | backend + frontend + data | M29 | todo | — |
| FR-143 | GitHub Pages via the GitHub App | 3.8 | backend + frontend | M29 | todo | — |
| FR-144 | Settings → Deployment: targets, readiness | 3.9 | backend + frontend + data | M29/M30 | todo | — |
| FR-145 | Environment inventory + sync | 3.9 | backend + frontend + data | M29 | todo | — |
| FR-146 | Origin model | 3.9 | backend + frontend | M29 | todo | — |
| FR-147 | Preview environment + rollout | 3.9 | backend + frontend | M30 | todo | — |
| FR-148 | Preview database reset | 3.9 | backend + frontend | M30 | todo | — |
| FR-149 | Production rollout | 3.9 | backend + frontend | M30 | todo | — |
| FR-150 | Rollback and promote per provider | 3.9 | backend + frontend | M29/M30 | todo | — |
| FR-151 | Deployments page | 3.9 | frontend + backend | M29 | todo | — |
| FR-152 | Database card: provision, link, external | 3.10 | backend + frontend + data | M30 | todo | — |
| FR-153 | Supabase BaaS (keys, auth URLs, migrations, advisors) | 3.10 | backend | M30 | todo | — |
| FR-154 | Host-mode database migrations on the platform | 3.10 | backend + deploy | M30 | todo | — |
| FR-155 | Events and notifications | 3.11 | backend + frontend | M31 | todo | — |
| FR-156 | CLI additions | 3.11 | cli + backend | M31 (LLM flags: M23) | todo | — |
| FR-157 | LLM provider enablement per deployment | 3.12 | backend + frontend + deploy | M23 | todo | — |
| FR-158 | `GET /api/llm/providers` | 3.12 | backend | M23 | todo | — |
| FR-159 | LLM choice at project creation | 3.12 | frontend + backend + cli | M23 | todo | — |
| FR-160 | Per-model max output tokens | 3.12 | backend | M23 | todo | — |
| FR-161 | Streaming LLM calls | 3.12 | backend | M23 | todo | — |
| FR-162 | Up to 5 continuations; `output_truncated` | 3.12 | backend + frontend | M23 | todo | — |

v1 (FR-1–FR-52), v2 (FR-53–FR-93) and v3 (FR-94–FR-112) requirements stay in force. Their tests must stay green on CI.

## Phases
- [x] M0 Baseline (base: 83d8fa5): CI green on 83d8fa5 (`main`): https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40129842995. The v4 prep commit f8a6dfc only touches docs (CI skips it).
- [ ] M23 LLM providers and long outputs — FR-157–FR-162 (+ FR-156 LLM flags)
- [ ] M24 Stack catalog + compatibility — FR-113, FR-116, FR-122
- [ ] M25 Composition in projects + preset retirement — FR-114, FR-115, FR-117–FR-119, FR-129
- [ ] M26 Specialists across the lifecycle — FR-120, FR-121, FR-123–FR-128
- [ ] M27 GitLab — FR-130–FR-132, FR-136–FR-141 (GitLab)
- [ ] M28 Bitbucket — FR-133–FR-135, FR-136–FR-141 (Bitbucket)
- [ ] M29 Platforms I — FR-142–FR-146, FR-150/151 (frontend parts)
- [ ] M30 Platforms II — FR-144, FR-147–FR-154
- [ ] M31 Integrate & harden — FR-155, FR-156, E2E, docs, spec-verifier over v1–v4

## Decisions
- 2026-10-25 — The draft moved to `docs/v4/DRAFT_SPEC.md` (Ben's prep commit); SPEC.md's source line points there — SPEC updated: yes

## Known issues
- Railway `preview` deploy of 83d8fa5 (run 40130552891): the deploy succeeded, but the smoke test's sign-in returned 401. The `SMOKE_EMAIL` account is missing on `preview` or its password differs. Operator fix (Ben): create the smoke user on preview, or correct the `SMOKE_PASSWORD` secret.

## Manual checks (Ben)
- Railway preview smoke account (see Known issues).
