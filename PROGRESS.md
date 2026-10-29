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
| FR-113 | Stack catalog (versioned data, `GET /api/stack/catalog`) | 3.1 | backend + data | M24 | done | CI 40579534195 |
| FR-114 | Wizard Stack step: five ordered sub-steps | 3.1 | frontend + backend | M25 | done | CI 40679856465 |
| FR-115 | Deployment sub-step: per-component hosts, origin model | 3.1 | frontend + backend | M25 | done | CI 40679856465 |
| FR-116 | Server-side composition validation | 3.1 | backend | M24 | done | CI 40579534195 |
| FR-117 | Composition pinned on release version; stack change; `STACK_LOCKED` | 3.1 | backend + frontend + data | M25 | done | CI 40679856465 |
| FR-118 | Import stack detection | 3.1 | backend + frontend | M25 | done | CI 40679856465 |
| FR-119 | Layer paths | 3.1 | backend | M25 | done | CI 40679856465 |
| FR-120 | Generic specialists catalogue | 3.2 | backend | M26 | done | CI 40743533120 |
| FR-121 | Specialists load pack skills | 3.2 | backend | M26 | done | CI 40743533120 |
| FR-122 | Adapted pack content; strict catalog loading | 3.2 | backend | M24 | done | CI 40579534195 |
| FR-123 | Spec Generator: composed spec sections | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-124 | Plan Generator: composed plan rules | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-125 | Execution: Guardian protected paths from packs | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-126 | Code Review: composed checklist + deterministic checks | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-127 | Generated-files policy | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-128 | Repository Analyst + refinement get the composition | 3.3 | backend | M26 | done | CI 40743533120 |
| FR-129 | Retire v2 presets (migration + removal) | 3.4 | backend + frontend + data | M26 | done | CI 40743533120 |
| FR-130 | GitLab instances (Admin) | 3.5 | backend + frontend + data | M27 | done | CI 40883397083 |
| FR-131 | GitLab connections + sign-in | 3.5 | backend + frontend + data | M27 | done | CI 40883397083 |
| FR-132 | GitLab repo picker + create | 3.5 | backend + frontend | M27 | done | CI 40883397083 |
| FR-133 | Bitbucket OAuth consumer (Admin) | 3.6 | backend + frontend + data | M28 | todo | — |
| FR-134 | Bitbucket connections + sign-in | 3.6 | backend + frontend + data | M28 | todo | — |
| FR-135 | Bitbucket repo picker + create | 3.6 | backend + frontend | M28 | todo | — |
| FR-136 | Git provider set; binding Owner's connection | 3.7 | backend + data | M27/M28 | partial: GitLab done; Bitbucket in M28 | CI 40883397083 |
| FR-137 | MRs/PRs on GitLab and Bitbucket | 3.7 | backend | M27/M28 | partial: GitLab done; Bitbucket in M28 | CI 40883397083 |
| FR-138 | Merge readiness on GitLab and Bitbucket | 3.7 | backend | M27/M28 | todo | — |
| FR-139 | CI status on every host | 3.7 | backend | M27/M28 | todo | — |
| FR-140 | Release on GitLab and Bitbucket | 3.7 | backend | M27/M28 | todo | — |
| FR-141 | Import from GitLab and Bitbucket | 3.7 | backend + frontend | M27/M28 | partial: GitLab done; Bitbucket in M28 | CI 40883397083 |
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
| FR-156 | CLI additions | 3.11 | cli + backend | M31 (LLM flags: M23) | partial: LLM flags done (CI 40579534195) | — |
| FR-157 | LLM provider enablement per deployment | 3.12 | backend + frontend + deploy | M23 | done | CI 40579534195 |
| FR-158 | `GET /api/llm/providers` | 3.12 | backend | M23 | done | CI 40579534195 |
| FR-159 | LLM choice at project creation | 3.12 | frontend + backend + cli | M23 | done | CI 40579534195 |
| FR-160 | Per-model max output tokens | 3.12 | backend | M23 | done | CI 40579534195 |
| FR-161 | Streaming LLM calls | 3.12 | backend | M23 | done | CI 40579534195 |
| FR-162 | Up to 5 continuations; `output_truncated` | 3.12 | backend + frontend | M23 | done | CI 40579534195 |

v1 (FR-1–FR-52), v2 (FR-53–FR-93) and v3 (FR-94–FR-112) requirements stay in force. Their tests must stay green on CI.

## Phases
- [x] M0 Baseline (base: 83d8fa5): CI green on 83d8fa5 (`main`): https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40129842995. The v4 prep commit f8a6dfc only touches docs (CI skips it).
- [x] M23 LLM providers and long outputs — FR-157–FR-162 (+ FR-156 LLM flags). Commits df8f119 + fixes; CI green on 7fcb9b9: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40579534195
- [x] M24 Stack catalog + compatibility — FR-113, FR-116, FR-122. Commit 24eaf5b + fixes; CI green on 7fcb9b9 (same run)
- [x] M25 Composition in projects — FR-114, FR-115, FR-117–FR-119, Admin → Stack catalog page. Commit b49f789; CI green: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40679856465
- [x] M26 Specialists across the lifecycle + preset retirement — FR-120, FR-121, FR-123–FR-128, FR-129. Commits 245b3d6, 0b88937, c5629e2 + fixes; CI green on b36e643: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40743533120
- [ ] M27 GitLab — FR-130–FR-132, FR-136–FR-141 (GitLab). Commits e6400fb (instances, connections, sign-in, picker/create), 5ed0698 (binding, transport, draft MRs, import, rebind), 70c1a08 (fix); CI green on 70c1a08: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40883397083. Remaining: readiness, CI status, merge, release (FR-138–FR-140) + their frontend
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
- 2026-10-28 — M25: `POST /api/stack/detect` takes an optional `token` for `github_pat`; `STACK_DETECTED` isn't emitted (detect runs before the project exists) — SPEC updated: no
- 2026-10-28 — M25: layer paths: `.` only when the other layer is None; one layer path inside the other is refused — SPEC updated: no
- 2026-10-28 — M25: until M26, a version has a preset OR a composition: PUT stack clears a legacy preset, PATCH preset clears the stack; PUT can't go back to "No stack" — SPEC updated: no
- 2026-10-28 — M25: stack-change baseline is the base version's composition; for a first version on a repo with code, the `from` of its first STACK_CHANGED — SPEC updated: no
- 2026-10-28 — M25: the wizard keeps the v2 preset cards under "Legacy presets (retiring in v4)" until M26 removes presets (keeps the v2 preset E2E flow working) — SPEC updated: no
- 2026-10-28 — M25: the wizard keeps its v3 step order (Repository and Version before Stack) instead of SPEC §7's Source → Idea → Stack → Repository; the repo then fixes VCS before the Stack step. Revisit in M27/M28 when the Repository step lists repos of the chosen VCS — SPEC updated: no
- 2026-10-28 — M25: the wizard maps GitHub repo modes to `vcs-github` client-side (R5) because no project exists yet; the server re-applies R5 on create (`VCS_MISMATCH`). Move to a server hint with GitLab/Bitbucket (M27/M28) — SPEC updated: no
- 2026-10-28 — M26: the RetirePresets migration writes 4 composition documents (preset × Vercel or not) with catalog 1.1.0 and a null catalog hash, re-pinned on the version's next stack write; `StackComposition.CatalogHash` is nullable — SPEC updated: no
- 2026-10-28 — M26: a run's `legacyAgentKey` is derived from `execution_steps.legacy_agent_key` (no new agent_runs column); a v2 key whose mapped specialist isn't in the stack falls back to the other mapping, then `generalist`, with run warning `legacy_agent_mapped` — SPEC updated: no
- 2026-10-28 — M26: a stack catalog that failed to load makes agent jobs fail transiently (retry, then dead-letter) instead of VALIDATION — SPEC updated: no
- 2026-10-28 — M26: FR-89's MISSING_VERCEL_CONFIG plan warning is kept, driven by the `deploy-vercel` host; preset types renamed (`VersionStackLoader`, `VersionStackContext`); prompts bumped (spec/plan v5, review v7) — SPEC updated: no
- 2026-10-28 — M26: seeds: released demo fe-react-vite + be-aspnet + data-efcore-postgres; preset demo kept as fe-nextjs at "." with its legacy preset id; new "Demo: Vue Notes" (fe-vue + data-supabase BaaS) in specification, no fake database — SPEC updated: no
- 2026-10-28 — M27: gitlab.com is a real instance row created on first use (can be disabled, not deleted); configured instances (GitLab__Instances__*) get rows whose secrets stay in config; deleting an instance removes its connections and external logins first — SPEC updated: no
- 2026-10-28 — M27: sign-in email linking goes through `/login?link=gitlab` and `/api/auth/gitlab/link` (held 10 minutes); OAuth state and pending links are in memory (one API replica, like the v2 manifest state) — SPEC updated: yes (§6 rows)
- 2026-10-28 — M27: unique indexes on git_host_instances and user_external_logins use NULLS NOT DISTINCT (PostgreSQL 15+) — SPEC updated: no
- 2026-10-28 — M27: merge readiness reason codes keep the v2 wire names (`merge_conflict`, `no_permission`) — SPEC updated: yes (FR-138)
- 2026-10-29 — M27: a GitLab connection that becomes `insufficient_scope` is treated as invalidated for FR-136: the binding is cleared and Owners get `git_binding_cleared` — SPEC updated: no
- 2026-10-29 — M27: the §4.19 common `IGitProvider` methods (`ListReposAsync` … `PublishReleaseAsync`) are implemented for GitLab (and Bitbucket in M28); GitHub and Local keep their v2 services and inherit NotSupported defaults, since nothing calls them through the interface — SPEC updated: no
- 2026-10-29 — M27: PATCH settings/git can't move a project onto gitlab/bitbucket (400 VALIDATION on `provider`); a GitLab binding is set on create/import and changed only by rebind (`connectionId`) — SPEC updated: no
- 2026-10-29 — M27: git CLI calls to a GitLab instance pin the SSRF-vetted addresses (`http.curloptResolve`) and don't follow redirects (`http.followRedirects=false`), matching the API path (§8) — SPEC updated: no
- 2026-10-28 — FR-129 (preset retirement) moves from M25 to the end of M26: migrated in-flight plans must run as the new specialists, which M26 builds — SPEC updated: no
- 2026-10-25 — M24: `legacy/` is created in M26 together with a catalog version bump, so the pinned hash only changes with a version — SPEC updated: no
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

## M26 preset migration SQL
Migration `20261028141435_RetirePresets` (FR-129, §4.23): data only, no DDL; row updates in one transaction (no table rewrites, no long locks). Reviewed SQL from `dotnet ef migrations script`:
- Release versions pinned to `nextjs-supabase` / `nextjs-frontend` without a composition get one of four documents (preset x Vercel deployment settings or not). VCS is `vcs-github` for every Git provider (github_app, github_pat, local or none), so it isn't branched on. Each document is exactly what `StackCompositionJson.Serialize` writes for the equivalent choices validated against the shipped catalog (`PresetMigrationCompositionTests`), pinned to catalog `1.1.0` with `catalogHash` NULL (column and document). The app re-pins the hash on the version's next stack write. `preset_id`, `preset_version` and `preset_hash` are kept as history.
- `projects.stack_packs` is set (`StackPinning.PackIds`) for projects whose current version (`current_release_version_id`, else the latest) was converted.
- Steps of open (in development, awaiting merge) converted versions: the v2 key moves to `legacy_agent_key` and `agent_key` gets the §4.23 specialist. `test-engineer` is the same key in v4 and is left alone. `devops-engineer` becomes `ci-engineer` when every declared file (normalised like `PathRules.Normalize`) is under `.github/`, else `deploy-engineer`. The mixed-files warning is a run-time concern. Released versions' steps keep their v2 keys for history.
- Converted rows are recognised by preset id + catalog `1.1.0` + NULL hash, because the app never pins a NULL hash. Down undoes only those rows: it restores the v2 agent keys, empties the packs and clears the compositions.

Up:
```sql
START TRANSACTION;
UPDATE release_versions AS v
SET stack_composition = (CASE
        WHEN v.preset_id = 'nextjs-supabase' AND x.vercel THEN '{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":"deploy-vercel","backendHost":null,"databaseHost":"supabase","local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","data-engineer","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Model \u0026 RLS Policies","Auth Flows","Environments \u0026 Configuration"],"envNames":["NEXT_PUBLIC_SUPABASE_URL","NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY","SUPABASE_URL","SUPABASE_PUBLISHABLE_KEY","SUPABASE_SECRET_KEY"],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}'
        WHEN v.preset_id = 'nextjs-supabase' THEN '{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":null,"backendHost":null,"databaseHost":"supabase","local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","data-engineer","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Model \u0026 RLS Policies","Auth Flows"],"envNames":["NEXT_PUBLIC_SUPABASE_URL","NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY","SUPABASE_URL","SUPABASE_PUBLISHABLE_KEY","SUPABASE_SECRET_KEY"],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[{"layer":"databaseHost","option":"supabase","rule":"R11","text":"Database only used by the local dev loop"}]}'
        WHEN x.vercel THEN '{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":null,"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":"deploy-vercel","backendHost":null,"databaseHost":null,"local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Sources \u0026 Consumed APIs","Environments \u0026 Configuration"],"envNames":[],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}'
        ELSE '{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":null,"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":null,"backendHost":null,"databaseHost":null,"local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","frontend-engineer","test-engineer","ci-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Sources \u0026 Consumed APIs"],"envNames":[],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}'
    END)::jsonb,
    catalog_version = '1.1.0',
    catalog_hash = NULL
FROM (
    SELECT p.id AS project_id,
           EXISTS (SELECT 1 FROM project_deployment_settings AS d
                   WHERE d.project_id = p.id AND d.provider = 'vercel') AS vercel
    FROM projects AS p) AS x
WHERE x.project_id = v.project_id
  AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend')
  AND v.stack_composition IS NULL;

UPDATE projects AS p
SET stack_packs = (CASE
        WHEN v.preset_id = 'nextjs-supabase' AND v.stack_composition -> 'deploy' ->> 'frontendHost' = 'deploy-vercel' THEN '{fe-nextjs,data-supabase,vcs-github,deploy-vercel}'
        WHEN v.preset_id = 'nextjs-supabase' THEN '{fe-nextjs,data-supabase,vcs-github}'
        WHEN v.stack_composition -> 'deploy' ->> 'frontendHost' = 'deploy-vercel' THEN '{fe-nextjs,vcs-github,deploy-vercel}'
        ELSE '{fe-nextjs,vcs-github}'
    END)::text[]
FROM release_versions AS v
WHERE v.id = COALESCE(p.current_release_version_id, (SELECT l.id FROM release_versions AS l WHERE l.project_id = p.id ORDER BY l.created_at DESC, l.id DESC LIMIT 1))
  AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL AND v.catalog_version = '1.1.0' AND v.catalog_hash IS NULL;

UPDATE execution_steps AS s
SET legacy_agent_key = s.agent_key,
    agent_key = CASE s.agent_key
        WHEN 'project-scaffolder' THEN 'scaffolder'
        WHEN 'db-engineer' THEN 'data-engineer'
        WHEN 'domain-logic-engineer' THEN 'frontend-engineer'
        WHEN 'api-engineer' THEN 'frontend-engineer'
        WHEN 'ui-engineer' THEN 'frontend-engineer'
        WHEN 'devops-engineer' THEN CASE
            WHEN jsonb_typeof(s.declared_files) = 'array' AND jsonb_array_length(s.declared_files) > 0
                 AND NOT EXISTS (
                     SELECT 1 FROM jsonb_array_elements(s.declared_files) AS f(file)
                     WHERE NOT starts_with(
                         rtrim(regexp_replace(regexp_replace(replace(btrim(COALESCE(f.file ->> 'path', '')), '\', '/'),
                             '/{2,}', '/', 'g'), '^(\./)+', ''), '/'),
                         '.github/'))
            THEN 'ci-engineer'
            ELSE 'deploy-engineer'
        END
    END
FROM release_versions AS v
WHERE v.id = s.release_version_id
  AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL AND v.catalog_version = '1.1.0' AND v.catalog_hash IS NULL
  AND v.status IN ('in_development', 'awaiting_merge')
  AND s.legacy_agent_key IS NULL
  AND s.agent_key IN ('project-scaffolder', 'db-engineer', 'domain-logic-engineer', 'api-engineer', 'ui-engineer', 'devops-engineer');

INSERT INTO __ef_migrations_history (migration_id, product_version)
VALUES ('20261028141435_RetirePresets', '10.0.13');

COMMIT;
```

Down:
```sql
START TRANSACTION;
UPDATE execution_steps AS s
SET agent_key = s.legacy_agent_key,
    legacy_agent_key = NULL
FROM release_versions AS v
WHERE v.id = s.release_version_id
  AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL AND v.catalog_version = '1.1.0' AND v.catalog_hash IS NULL
  AND s.legacy_agent_key IN ('project-scaffolder', 'db-engineer', 'domain-logic-engineer', 'api-engineer', 'ui-engineer', 'devops-engineer');

UPDATE projects AS p
SET stack_packs = '{}'::text[]
FROM release_versions AS v
WHERE v.id = COALESCE(p.current_release_version_id, (SELECT l.id FROM release_versions AS l WHERE l.project_id = p.id ORDER BY l.created_at DESC, l.id DESC LIMIT 1))
  AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL AND v.catalog_version = '1.1.0' AND v.catalog_hash IS NULL;

UPDATE release_versions AS v
SET stack_composition = NULL,
    catalog_version = NULL
WHERE v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL AND v.catalog_version = '1.1.0' AND v.catalog_hash IS NULL;

DELETE FROM __ef_migrations_history
WHERE migration_id = '20261028141435_RetirePresets';

COMMIT;
```

## Known issues
- §4.22 gap (2026-10-28): a Supabase BaaS stack in Next.js full-stack declares `SUPABASE_SECRET_KEY` but not the server-side unprefixed `SUPABASE_URL` (a pack can't list a key twice; the public `SUPABASE_URL` becomes `NEXT_PUBLIC_SUPABASE_URL`). Fix in M30 with the derived Supabase variables (FR-145, FR-153).
- Integration test memory (found 2026-10-27): every derived test host (`WithWebHostBuilder`, e.g. `LlmStubs.App`, harnesses) stays alive on the shared `AideApiFactory` until the run ends (~37 MB each). With v4's extra tests one process outgrew a 16 GB runner (heap dump: 50 live hosts after 150 s; RSS ~14.7 GB). Fixed for CI by running integration tests in 3 shards (`backend-integration` job, ci.yml); the stack catalog is also loaded once per process now. Follow-up (test-engineer): dispose derived factories per test so a single process stays small.
- Railway `preview` deploy of 83d8fa5 (run 40130552891): the deploy succeeded, but the smoke test's sign-in returned 401. The `SMOKE_EMAIL` account is missing on `preview` or its password differs. Operator fix (Ben): create the smoke user on preview, or correct the `SMOKE_PASSWORD` secret.

## Manual checks (Ben)
- Railway preview smoke account (see Known issues).
- **Before the next Railway deploy after merging M23:** run `railway config plan` then `railway config apply` for `preview` and `production`. M23 adds `Llm__Providers__Ollama__Enabled=false`, `Llm__Providers__Fake__Enabled=false` and `GitLab__AllowPrivateNetworks=false`, and removes `Llm__Providers__Ollama__BaseUrl` from the ignore list (the plan deletes it where set). Until applied, deploy.yml's drift check fails.
- After that apply, Railway projects still set to Fake (or Ollama) fail their next agent run with `LLM_PROVIDER_UNAVAILABLE`; their Owners choose another provider in Settings → LLM.
- OpenAI catalog limits for `gpt-6-astra` / `gpt-6.1-sol` are placeholders (16,000 output, 400,000 context) until checked against OpenAI's docs (SPEC Q5).
