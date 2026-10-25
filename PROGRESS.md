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
- 2026-10-25 — M23: `maxOutputTokens` and `continuations` are stored in the run output JSON, not new `agent_runs` columns (no migration needed) — SPEC updated: yes (§5)
- 2026-10-25 — M23: `GET /api/llm/providers` returns `isDefault` so the wizard preselects the server's default provider (CI default `fake` keeps E2E flows working) — SPEC updated: yes (FR-158, §6)
- 2026-10-25 — M23: create binds the caller's newest **valid** connection when the provider needs a key and none is sent; invalid connections don't make a provider usable — SPEC updated: yes (FR-159)
- 2026-10-25 — M23: CLI LLM flags live on `aide project init`, with `create` added as an alias — SPEC updated: no (alias matches the spec's `project create`)
- 2026-10-25 — M23: a provider that answers `application/json` to a streaming request is read as a whole body (keeps compatible servers and the JSON-stubbed tests working) — SPEC updated: no
- 2026-10-25 — M23: the integration test factory pins `Llm:DefaultProvider=fake`, matching CI — SPEC updated: no
- 2026-10-25 — M24: a catalog that fails to load makes readiness (`stack-catalog`) unhealthy and logs Critical instead of stopping startup; the stack endpoints then answer 500. The readiness probe keeps the instance out of service, so no coded error is added — SPEC updated: no
- 2026-10-25 — M24: `/api/stack/options` returns all 9 choices (frontend, backend, data, vcs, frontendHost, backendHost, databaseHost, local, originModel) with `selected`/`default`; violation `layer` is the choice id (e.g. `backendHost`) — SPEC updated: no
- 2026-10-25 — M24: Supabase in the Data layer is always BaaS; host mode is the database host `supabase` with another ORM (`data.mode = "host"` → VALIDATION) — SPEC updated: no
- 2026-10-25 — M24: R10 gaps filled: a static SPA on Railway with the backend elsewhere → proxy or cross-origin; Next.js ui mode on GitHub Pages → cross-origin only. R12: Compose forbidden with no backend and no data — SPEC updated: no
- 2026-10-25 — M24: the Admin → Stack catalog page moves to M25 (with the other Stack UI); M24 ships `GET /api/admin/stack` only — SPEC updated: no
- 2026-10-25 — M24: `legacy/` is created in M25 together with a catalog version bump, so the pinned hash only changes with a version — SPEC updated: no
- 2026-10-25 — M24: pack skills are 70–115% of the source length (not 40–60%): hand-writing generated files and CI checks needs the room — SPEC updated: no
- 2026-10-25 — M24: review category for migrations is `data_model` (v1 enum), not `data` — SPEC updated: yes

## M26 inputs (from M24 catalog content)
The catalog schema can't express these, so the M26 resolver must implement them:
- Expand `{fe}`, `{be}`, `{app}` (the backend, or the Next.js full-stack app) in ownership globs, `gate`, `ciCheck`, plan-rule globs and agent prompts. Define what a missing layer expands to (e.g. no `{fe}` → drop the rule).
- `requiredEnv` public keys (`SUPABASE_URL`, `SUPABASE_PUBLISHABLE_KEY`, API base URL) get the frontend pack's `publicEnvPrefix` (Angular: unprefixed, written into `assets/config.json`). `appliesTo: backend` means `{app}` in Next.js full-stack.
- Mode-dependent sections: Prisma/Drizzle `Setup` for frontend-engineer only in Next.js full-stack; `data-supabase` in host mode → data-engineer and deploy-engineer load only `Production notes`; Supabase-only checks (`rls_on_every_table`, `grants_for_exposed_tables`) only in BaaS mode; `fe-nextjs` full-stack-only ownership and generated client only in ui mode.
- `container_non_root` / `health_endpoints` inputs (health paths per backend pack, Docker deploy) aren't manifest fields: derive from the backend pack id or add fields.
- `AIDE_RUN_MIGRATIONS` ("serverless host in host mode") is computed by the resolver, not declared by packs (no OR in `when`).
- Composition summary template (`core/composition-summary.md`): `{{UPPER_SNAKE}}` placeholders, rendered separately from the base prompt template; strip leading provenance comments from agent prompts and the summary; mirror `SpecialistPrompt.ComposeSystem`.
- Plan rules: scaffolder exemption from the deploy/ci exclusivity entries (v2 decision); `planRules.exclusive` has no `soft` flag.
- SPEC follow-ups: §4.22 should name `API_ORIGIN` for the Railway same-origin SPA proxy; §4.20 should say who sets `VITE_API_BASE_URL` for GitHub Pages (repo variable via the GitHub App).

## Known issues
- Railway `preview` deploy of 83d8fa5 (run 40130552891): the deploy succeeded, but the smoke test's sign-in returned 401. The `SMOKE_EMAIL` account is missing on `preview` or its password differs. Operator fix (Ben): create the smoke user on preview, or correct the `SMOKE_PASSWORD` secret.

## Manual checks (Ben)
- Railway preview smoke account (see Known issues).
- **Before the next Railway deploy after merging M23:** run `railway config plan` then `railway config apply` for `preview` and `production`. M23 adds `Llm__Providers__Ollama__Enabled=false`, `Llm__Providers__Fake__Enabled=false` and `GitLab__AllowPrivateNetworks=false`, and removes `Llm__Providers__Ollama__BaseUrl` from the ignore list (the plan deletes it where set). Until applied, deploy.yml's drift check fails.
- After that apply, Railway projects still set to Fake (or Ollama) fail their next agent run with `LLM_PROVIDER_UNAVAILABLE`; their Owners choose another provider in Settings → LLM.
- OpenAI catalog limits for `gpt-6-astra` / `gpt-6.1-sol` are placeholders (16,000 output, 400,000 context) until checked against OpenAI's docs (SPEC Q5).
