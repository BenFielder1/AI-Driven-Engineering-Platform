# AIDE Platform — Specification (v3)

2026-10-18 · Ben Fielder · **v3**, built on the v2 codebase in this repo. v2: [`docs/v2/SPEC.md`](docs/v2/SPEC.md), build log [`docs/v2/PROGRESS.md`](docs/v2/PROGRESS.md) · v1: [`docs/v1/SPEC.md`](docs/v1/SPEC.md), build log [`docs/v1/PROGRESS.md`](docs/v1/PROGRESS.md) · Initially planned for v3 but now acting as the first draft spec for v4.

> **How this spec relates to v1 and v2.** v3 is an increment on the running v2 app, not a rewrite. **Everything in `docs/v1/SPEC.md` and `docs/v2/SPEC.md` stays in force unless this document amends or replaces it.** That covers FR-1 to FR-93, the domain rules in v1 §4.1–4.9 and v2 §4.10–4.15, both data models, APIs, NFRs and deployment sections. Amended items are listed in §3.0 and marked *(amends FR-n)* where they change. **The v2 tech stack presets (v2 FR-74–FR-77, §4.11, §4.12) are replaced** by stack composition (§3.1–§3.4). New requirements continue the numbering from **FR-94**, domain rules from **§4.16**, and decisions use the `D-V3-n` prefix, so every ID stays unique across versions. v1 and v2 tests stay green unless an amendment changes the behaviour they assert; in that case, update the test and name the amendment in the test.
>
> **Paths.** This file is written to live at the repo root while v3 is built (it is drafted in `docs/v3/`). Links and paths are relative to the repo root. `../stack-packs/` is Ben's stack-packs marketplace (read-only, like the old stack).

## 1. Overview

AIDE (AI-Driven Engineering Framework) turns an idea into released software through a governed six-phase lifecycle: **Refinement → Specification → Planning → Execution → Review → Release**. AI agents produce versioned artifacts, humans approve them at gates, and every state change is deterministic, logged and reversible. v2 added release versions, imports, per-user connections, two Next.js presets and a Vercel integration.

**v3 makes AIDE stack-agnostic:**
- **Stack composition replaces presets.** A user picks one option per layer, in this order: **Frontend → Backend → Data → VCS → Deployment**. Options that don't fit the earlier choices are filtered out with the reason shown. The catalog and its rules come from Ben's **stack-packs** marketplace (`../stack-packs/`): 5 frontends, 5 backends, 7 data layers, 3 Git hosts and 6 deployment platforms.
- **Specialist agents per layer.** A fixed team of generic specialists (scaffolder, data, backend, frontend, test, CI, deploy, docs engineers, from stack-packs' `stack-core`) each load the **pack skill** for the framework chosen in their layer. For example, the backend engineer loads `be-fastapi` for a FastAPI project and `be-aspnet` for an ASP.NET one.
- **Every Git host AIDE's users have:** GitLab (gitlab.com and allow-listed self-managed instances) and Bitbucket Cloud join GitHub, with sign-in, repo picker, repo creation, merge requests, merge readiness, CI status, releases and import.
- **Every deployment platform in the catalog:** Netlify, Cloudflare, Render, Railway and GitHub Pages join Vercel, with a split per component (frontend host, backend host, database host), preview environments and production deployments.
- **Managed databases:** Supabase (BaaS and host mode), Render Postgres and Railway Postgres are provisioned or linked by AIDE, wired into the apps' environment variables, and migrated safely.

Success means:
- A user composes **React + Vite / ASP.NET Core / EF Core + PostgreSQL / GitLab / Netlify (frontend) + Render (backend and Postgres)**. Incompatible options (e.g. Vercel for the ASP.NET backend) can't be picked. The scaffolder, data, backend, frontend, test, CI and deploy engineers build version 0.1.0 with the right pack skills. The review sees a preview on Netlify talking to a Render preview service with its own preview database. The release merges the GitLab merge request, creates a GitLab Release and deploys both components to production.
- A second user imports a Bitbucket repo that AIDE detects as **Vue + Supabase (BaaS)**, deploys it to Vercel, and AIDE applies the version's Supabase migrations to the linked Supabase project on release.
- Projects created on the v2 `nextjs-supabase` and `nextjs-frontend` presets keep working as compositions, with no action from their owners.

**In scope (v3):**
- Stack catalog and composition: catalog data adapted from stack-packs, compatibility rules, the wizard Stack step, stack settings, import detection (§3.1)
- Specialist agents and pack skills (§3.2)
- Spec, plan, execution and review driven by the composition, including per-pack deterministic review checks (§3.3)
- Retiring the v2 presets: data migration of existing projects, removal of preset code and endpoints (§3.4)
- GitLab integration (§3.5) and Bitbucket Cloud integration (§3.6), plus provider-neutral Git lifecycle rules (§3.7)
- Deployment platform connections: Netlify, Cloudflare, Render, Railway, Supabase, plus Vercel moved onto the same model; GitHub Pages through the GitHub connection (§3.8)
- Deployment targets per component, environment inventory, origin model, preview environment, production deployments, rollback (§3.9)
- Managed databases and migrations (§3.10)
- Events, notifications and CLI additions (§3.11)

**Out of scope (v3):**
- A build or generator runner. Agents still can't run commands, and AIDE doesn't build, install or execute the generated code (D-V2-9, D-V3-5). CI on the Git host and the platforms' own builds are the verification.
- Deploying by local build or upload (`wrangler deploy`, `railway up`, `netlify deploy`, Docker image builds). Where a platform can't build from a Git host, that combination is incompatible (D-V3-9).
- The stack-packs `local-docker-desktop-k8s` pack as a deployment target. AIDE never runs the generated app on the user's cluster. The Compose dev-loop pack (`local-docker-desktop`) is used only as a skill for the deploy engineer.
- Supabase branching, Netlify Database, Vercel Marketplace databases, Cloudflare D1/Hyperdrive, Cloudflare Containers, managed SQL Server (§11).
- OAuth for Netlify, Cloudflare, Render, Railway and Supabase. Pasted, scoped tokens are used, as for Vercel (D-V3-8).
- GitHub Enterprise Server, Bitbucket Data Center, Azure DevOps.
- New stack packs beyond those in stack-packs today. Adding one is a catalog change (§4.16), not a feature.
- Hosting AIDE itself anywhere other than Docker Desktop Kubernetes.

## 2. Users and permissions

The v1 and v2 roles are unchanged (v1 §2, v2 §2). v3 adds:

| Role | Can (new in v3) | Cannot |
| --- | --- | --- |
| Admin | Add, edit, disable and remove **Git host instances**: gitlab.com and self-managed GitLab (allow-listed base URLs) with their OAuth application credentials, and the Bitbucket Cloud OAuth consumer; view the stack catalog and pack versions | Add a GitLab host that resolves to loopback, link-local or metadata addresses (§8); see any user's credentials |
| User | Connect GitLab and Bitbucket accounts (OAuth or token fallback) and sign in with them; connect Netlify, Cloudflare, Render, Railway and Supabase accounts (pasted tokens); pick or create repos on any connected Git host | Use another user's connection directly |
| Project Owner | Compose or change the stack (until the plan is locked); bind their own Git host connection for GitLab/Bitbucket projects; configure deployment targets and databases with their own platform connections; reset the preview database; promote and roll back production deployments | Bind another user's connection (`CREDENTIAL_NOT_OWNER → 403`); change the stack after the version's implementation plan is locked (`STACK_LOCKED → 409`) |
| Project Contributor | Trigger preview deployments (v2), view the composition, targets, databases and migration runs | Change the stack, targets, databases or env vars; reset the preview database (403) |
| Project Viewer | View the composition, targets, databases, deployments and migration runs | Any mutation (403) |

Auth: v1 cookie auth, API tokens and antiforgery, and v2 GitHub App sign-in, are unchanged. **"Continue with GitLab" and "Continue with Bitbucket"** are added on the sign-in page for each enabled Git host instance (FR-112, FR-115). Accounts are matched by `(provider, instance, external user id)`, and a signed-in user can link further Git identities from Account. Connections stay personal (v2 §2): GitLab and Bitbucket background Git work runs with the **binding Owner's** connection, because neither host has GitHub-style installation tokens (D-V3-6).

## 3. Functional requirements

### 3.0 Amended v1 and v2 requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| FR-2 (v1), FR-80 (v2) | Sign-in also offers GitLab and Bitbucket | FR-112, FR-115 |
| FR-5 (v1), FR-73 (v2) | The wizard's **Stack** step becomes five ordered layer sub-steps; the Repository step lists repos of the chosen VCS; import may come from GitLab or Bitbucket and pre-fills the stack by detection | FR-95, FR-99, FR-122 |
| FR-9 (v1) | Project settings gain **Stack**; Git settings gain `gitlab` and `bitbucket` providers; Deployment settings become per-component targets plus databases | FR-98, FR-117, FR-125 |
| FR-28 (v1), FR-75 (v2) | Spec sections come from the composition's packs | FR-104 |
| FR-29/31 (v1), FR-76 (v2) | Plan steps use generic specialist keys; validation rules come from the composition | FR-105, FR-106 |
| FR-35/36 (v1), FR-82 (v2) | Git providers gain GitLab and Bitbucket; background work uses the binding Owner's connection for those hosts | FR-117, FR-118 |
| FR-42/43 (v1), FR-64/65 (v2) | Release creates a GitLab Release, or an annotated tag with notes on Bitbucket; merge strategies map per host | FR-121 |
| FR-74–FR-77 (v2), §4.11–§4.12 | **Removed** and replaced by stack composition; existing projects are migrated | FR-94–FR-110 |
| FR-78 (v2) | Unchanged; connections page gains Git host, platform and database sections | FR-123 |
| FR-84/85 (v2) | Merge readiness and CI status for GitLab and Bitbucket | FR-119, FR-120 |
| FR-87–FR-91 (v2), §4.15 | Vercel moves onto the generic platform connection and deployment target model; previews become a project **preview environment**; production deploys every target in order | FR-123–FR-132 |
| FR-92/93 (v2) | New events, notifications and CLI commands | FR-136, FR-137 |
| D-V2-10 (v2) | "Supabase stays manual" is replaced: Supabase is a connection and a managed database | FR-133–FR-135, D-V3-12 |

### 3.1 Stack catalog and composition

1. **FR-94** The **stack catalog** is versioned data shipped in the repo (§4.16), adapted from stack-packs. It defines the five layers, their options (one per stack-packs pack, plus "None" where allowed), each option's chips, description and source pack version, the compatibility rules (§4.17) and the recommended combinations.
   - `GET /api/stack/catalog` returns it for the wizard, settings and Admin → Stack catalog.
   - The catalog version and content hash are recorded on every release version and agent run.
   - Given the shipped catalog, when `GET /api/stack/catalog` is called, then it lists 5 frontend options (`fe-react-vite`, `fe-nextjs`, `fe-angular`, `fe-vue`, `fe-vanilla`) plus None, 5 backends plus None, 7 data options plus None, 3 VCS options and 6 deployment platforms, each with its `source` pack and version.
2. **FR-95** The new-project wizard's **Stack** step *(amends FR-5, replaces the v2 preset cards)* has five ordered sub-steps: **Frontend → Backend → Data → VCS → Deployment**.
   - Each sub-step shows option cards (name, chips, one-line description, recommended badge). Options come from `POST /api/stack/options` with the choices so far (§4.17). Each option is `allowed`, `caveat` (allowed, with the ⚠ text shown on the card) or `forbidden` (shown disabled in a collapsed "Not available with your choices (n)" list, each with its reason).
   - A forbidden option can't be selected, and the API rejects it anyway (`STACK_INCOMPATIBLE`).
   - Going back and changing an earlier layer re-evaluates the later layers. A later choice that became forbidden is cleared, and a toast names what was cleared and why.
   - A **summary card** shows the composition, the derived facts (Next.js mode, origin model, which specialists will work on it) and the caveats.
   - "No stack (general)" remains as a choice at the top of the step: v1/v2 behaviour with the `generalist` agent (FR-101).
   - Given Frontend = React + Vite, when the user reaches Backend, then every backend is `allowed`. Given Backend = ASP.NET Core, when the user reaches Data, then only EF Core + PostgreSQL, EF Core + SQL Server (caveat: no managed SQL Server on the catalog's platforms) and None are selectable, and Prisma shows "Prisma works with Express, NestJS or Next.js full-stack".
   - Given the user changes Backend from FastAPI to ASP.NET after choosing SQLAlchemy, then Data is cleared and the toast says "SQLAlchemy was cleared: it needs FastAPI".
3. **FR-96** The **Deployment** sub-step has up to four choices, each filtered by the earlier layers and by each other (§4.17):
   - **Frontend host** (shown when there's a frontend): Vercel, Netlify, Cloudflare, GitHub Pages, Render, Railway, or None.
   - **Backend host** (shown when there's a backend): Vercel, Render, Railway, or None. "Same as frontend" is preselected when that platform can run the backend.
   - **Database host** (shown when Data needs a server): Supabase, Render Postgres, Railway Postgres, External (a connection string the Owner enters), or None (local only). Data = Supabase (BaaS) fixes it to Supabase.
   - **Local dev loop**: Docker Compose (default when there's a backend or a database) or None. This loads the `local-docker-desktop` pack for the deploy engineer (D-V3-2).
   - The derived **origin model** (`same-origin`, `proxy` or `cross-origin`, §4.17 R10) is shown with a one-line explanation. Where more than one model is allowed, the Owner can switch between them.
   - Given Frontend = React + Vite on Netlify and Backend = ASP.NET on Render, then the database host options are Render Postgres, Supabase (host mode) and External, Railway Postgres is a `caveat` ("Database on a different platform from the backend: higher latency, public connection"), and the origin model is `proxy`.
4. **FR-97** Compositions are validated on the server with the same rules:
   - `POST /api/stack/validate` returns the normalised composition with its derived fields (§4.18) or `STACK_INCOMPATIBLE → 400` with `violations[] {layer, option, rule, reason}`.
   - Project create, `PUT /projects/:id/stack` and `POST /versions` with a stack run the same validation.
   - Given the composition `fe-vue` + `be-aspnet` + `data-prisma`, when it is posted, then `STACK_INCOMPATIBLE` with rule `R3` and reason "Prisma needs Express, NestJS or Next.js full-stack".
5. **FR-98** The composition is pinned on the release version *(replaces FR-74's preset pinning)* with the catalog version and hash.
   - An Owner can change it in **Settings → Stack** until the version's first implementation plan is locked. After that, `STACK_LOCKED → 409`. It can also be changed when starting a new version (v2 FR-60's dialog gains a "Change stack" link that opens the same five sub-steps, pre-filled).
   - Changing a layer of a release version whose base commit already has code (an iteration or import) is a **stack change**. The spec agent is told which layers changed, and the spec must contain a "Stack Migration" section (FR-104). The plan must contain at least one step owned by the new layer's specialist.
   - Changing the VCS layer is allowed only to the host of the project's repo (`VCS_MISMATCH → 400`), and is set automatically when the repo is chosen.
   - `PRESET_CHANGED` is replaced by `STACK_CHANGED {from, to, layersChanged[]}`.
   - Given a locked implementation plan, when an Owner changes the backend, then `STACK_LOCKED`.
6. **FR-99** **Import detection** *(amends FR-73)*: when a repo is chosen for import, AIDE runs the pure stack detector (§4.18 "Detection") over the repo's file list and manifests at the chosen base and pre-fills the Stack step.
   - Detected options are marked "detected" with the evidence (e.g. "`next` 16.1 in `package.json`").
   - The VCS layer is fixed to the repo's host. Undetected layers are left empty for the user.
   - Layer **paths** are detected too (e.g. `apps/web`, `api/`).
   - Detection never blocks: the user can change any detected choice, and the Repository Analyst (v2 FR-70) still runs.
   - Given an imported repo with `frontend/package.json` (vue, vite), `supabase/config.toml` and `supabase/migrations/`, and no backend, then the suggestion is `fe-vue` at `frontend/`, Backend None and `data-supabase` (BaaS).
7. **FR-100** Each composition records the **path** of each layer in the repo (§4.18). The defaults follow stack-packs' STACK template: `frontend/` and `backend/`; for a Next.js full-stack app or a frontend-only repo, the repo root `.` is allowed. Paths are relative and normalised, may not escape the repo (`..`), and must differ between frontend and backend unless one of them is None.
   - Specialist owned paths, plan rules and review checks are expanded from these paths.
   - Given a composition with `frontend: "."` and `fe-nextjs`, when the scaffolder's owned paths are listed, then they start at the repo root.

### 3.2 Specialist agents and pack skills

8. **FR-101** AIDE has one catalogue of **generic specialists**, adapted from stack-packs `stack-core` agents (§4.18). The agents in play for a project are those whose layer exists in its composition:

   | Key | From stack-core | Present when |
   | --- | --- | --- |
   | `scaffolder` | `stack-scaffolder` | always (with a composition) |
   | `data-engineer` | `stack-data-engineer` | Data ≠ None |
   | `backend-engineer` | `stack-backend-engineer` | Backend ≠ None |
   | `frontend-engineer` | `stack-frontend-engineer` | Frontend ≠ None |
   | `test-engineer` | `stack-qa-engineer` | always |
   | `ci-engineer` | `stack-ci-engineer` | always |
   | `deploy-engineer` | `stack-deploy-engineer` | any deployment host or the local dev loop is set |
   | `docs-writer` | `stack-docs-writer` | always |
   | `generalist` | — | only with "No stack (general)" (v1 behaviour) |

   `stack-reviewer` and `stack-auditor` map to AIDE's existing Code Review agent and review cycle; they aren't plan agents.
   - Given a composition with no backend and `data-supabase` (BaaS), then the plan generator's catalogue has scaffolder, data-engineer, frontend-engineer, test-engineer, ci-engineer, deploy-engineer and docs-writer, and no backend-engineer.
9. **FR-102** Each specialist **loads the pack skills for its layer** (the full adapted skill, or named sections of it), plus shared core skills, as listed in §4.18 "Skill resolution".
   - The specialist prompt order is: base `code_generator.md` → the composition summary (the STACK.md equivalent: packs, paths, origin model, env names, gates) → the agent's prompt → each resolved skill (`## Skill: <pack> — <section>`) → the step input.
   - Each agent run records `agent_key`, the composition id, and `packs[] {id, version, sections[]}` it loaded.
   - Given a composition with `be-fastapi` and `data-sqlalchemy`, when a `backend-engineer` step runs, then its prompt contains the adapted `be-fastapi` skill and `stack-api-contract`, and the run records `packs: [be-fastapi@1.0.0+aide.1, stack-api-contract@1.0.0+aide.1]`.
   - Given the same project switched to `be-aspnet` before planning, when the next `backend-engineer` step runs, then it loads `be-aspnet` instead.
10. **FR-103** Pack and agent content is **adapted, not copied**, from stack-packs (§4.16 "Adaptation rules"), in the same spirit as v2's claude-kits adaptation. Every pack file records its source plugin and version. A catalog that references a missing file, an unknown section heading or an unknown pack fails loading at startup: the readiness check fails (`stack-catalog` unhealthy, replacing `presets`), and a unit test asserts the shipped catalog loads.
    - Given a pack whose agent mapping names a section "Gates" that the adapted skill doesn't have (it has "Gate"), then catalog loading fails with that pack and section named.

### 3.3 Spec, plan, execution and review

11. **FR-104** The **Spec Generator** *(replaces FR-75's preset input)* gets the composition summary and the union of the selected packs' required spec sections with their guidance (§4.18 "Spec sections"). Spec validation requires those sections. The v2 rules on requirement IDs, acceptance criteria and iteration tags are unchanged.
    - A stack change (FR-98) adds the required section "Stack Migration" (what moves, data migration, compatibility, rollout).
    - Given a composition with a backend and a cross-origin deployment, when the spec lacks "API Contract & Error Codes" or "Origin Model (CORS, Cookies, Auth)", then the run gets the one corrective retry naming the missing sections.
12. **FR-105** The **Plan Generator** *(replaces FR-76's preset input)* gets the composition's specialists (key, name, description, owned paths) and the composed plan rules (§4.18 "Plan rules"). Validation is v2 §4.11's, with the rules generated from the composition instead of a preset file:
    - unknown or missing `agent` → `INVALID_PLAN`;
    - files outside the agent's owned paths → `AGENT_SCOPE` warning;
    - order and exclusivity rules → errors (soft rules → `PLAN_ORDER` warning, per the v2 decision);
    - uncovered new or changed requirements → `UNCOVERED_REQUIREMENTS` warning;
    - a stack change without a step for the new layer's specialist → `STACK_CHANGE_UNPLANNED` error.
    - Given a composition with `data-efcore-postgres`, when a plan has a `backend-engineer` step creating a file under the backend's `Migrations/` folder, then `INVALID_PLAN` names the exclusivity rule "migrations belong to data-engineer".
13. **FR-106** During **execution**, each step runs as its specialist with its resolved skills (FR-102). The Guardian's protected paths are the union of the v1 rules and the packs' protected paths (§4.18), with the v2 "owner may declare" exception. **Migrations that exist on the base commit are immutable for every data pack**, not only Supabase.
    - Given a composition with `data-jpa-flyway`, when a step modifies `backend/src/main/resources/db/migration/V2__orders.sql` that exists on the base commit, then the Guardian rejects the step with a protected-path violation.
14. **FR-107** The **Code Review** agent *(replaces FR-77)* scores against the composed checklist: each selected pack's `Checklist` items, mapped to a v1 category and severity in the pack file. The deterministic checks are those registered by the selected packs (§4.18 "Review checks"); checklist findings are agent violations without penalties, as in v2.
    - Given `fe-react-vite` and a file that reads `import.meta.env.VITE_STRIPE_SECRET_KEY`, when review runs, then a critical security finding "secret-like value exposed to the browser" applies −0.20 to security.
    - Given `data-efcore-postgres` and `EnsureCreated()` in non-test code, then a major `architecture` finding applies −0.10.
15. **FR-108** **Generated files** *(new; no build runner, D-V3-5)*: agents can't run generators, so the composition's packs declare how each generated file is handled (§4.18 "Generated files"):
    - **Hand-written in the generator's shape** by the owning specialist, when small and deterministic (the OpenAPI document for changed endpoints, the generated client types, Supabase `database.types.ts`, EF Core migration and snapshot files).
    - **Produced or checked by CI** composed by the `ci-engineer` (freshness checks such as `git diff --exit-code backend/openapi/api.json`, `dotnet ef migrations has-pending-model-changes`, `prisma migrate diff --exit-code`).
    - **Absent until the first CI run** (lockfiles): CI uses `npm install`, `uv sync` without `--frozen`, etc. when no lockfile exists (v2 decision).
    - CI results on the PR head are review evidence on every Git host (FR-120), so a failed freshness check becomes a finding that the optimisation plan must fix.
    - Given a backend step that adds an endpoint, when the plan is validated, then the step must declare `backend/openapi/api.json` (or a later step must), otherwise an `UNDECLARED_GENERATED_FILE` warning names it.
16. **FR-109** **Repository Analyst** and **refinement** *(amends FR-70/72)* get the composition summary and, for imports, the detector's evidence. The codebase summary's "Stack & Dependencies" section must name the detected packs and any mismatch with the composition.
    - Given an import detected as `fe-vue` whose Owner chose `fe-react-vite`, then the summary flags the mismatch and the spec must contain "Stack Migration".

### 3.4 Retiring the v2 presets

17. **FR-110** The v2 presets `nextjs-supabase` and `nextjs-frontend` are removed: the `Agents/Presets/` folder, the preset loader, `GET /api/presets[/:id]`, `PATCH /api/projects/:id/preset`, the wizard's preset cards, the projects-list preset filter and the `presets` health check. One EF migration converts existing data (§4.23):
    - Every release version pinned to a preset gets the equivalent composition (`nextjs-supabase` → `fe-nextjs` at `.` + no backend + `data-supabase` BaaS + VCS from the Git provider + Vercel frontend host + Supabase database host *unlinked*; `nextjs-frontend` → the same without data).
    - The preset id, version and hash stay on the release version and agent runs as history and are shown as "Preset nextjs-supabase@1.0.1 (retired, migrated)".
    - In-flight plans keep their v2 agent keys, mapped at run time (§4.23 table), so a locked plan still executes.
    - The dev seed's preset demo project becomes a composition demo (§9).
    - Given a v2 project in `execution` on `nextjs-supabase` whose step 4 is `db-engineer`, when step 4 runs after the upgrade, then it runs as `data-engineer` with the `data-supabase` skill, and the run records `legacyAgentKey: db-engineer`.
    - Given any client calling `GET /api/presets`, then `404` (breaking change accepted in D-V3-4).

### 3.5 GitLab

18. **FR-111** An Admin manages **GitLab instances** in **Admin → Integrations → GitLab**:
    - `gitlab.com` is offered by default; self-managed instances are added by base URL (`https://` only, unless "allow insecure HTTP" is ticked for a lab host).
    - Each instance stores a name, base URL, and the **OAuth application** credentials (client id, client secret encrypted). The page shows the exact callback URL (`<public base URL>/api/auth/gitlab/callback`) and scopes (§4.19) to register, since GitLab has no manifest flow.
    - Adding or changing a base URL runs the SSRF guard (§8) and a reachability check (`GET /api/v4/version` → must answer with GitLab's JSON shape, unauthenticated 401 counts as reachable). Instances can also be configured from `secrets.env` (`GitLab__Instances__<n>__*`), which take precedence.
    - An instance can be disabled (existing connections stop working, projects show "GitLab instance disabled") or removed when no project uses it (`CONFLICT → 409` otherwise).
    - Given an Admin adds `https://169.254.169.254`, then `GIT_HOST_NOT_ALLOWED → 400`. Given `https://gitlab.internal.example` resolving to `10.0.4.7`, then it is accepted (private networks are allowed, §8).
19. **FR-112** In **Account → Connections → GitLab**, a user connects GitLab per instance:
    - **OAuth** (authorization code + PKCE S256, scopes `api write_repository openid profile email`). Access tokens last 2 h; AIDE refreshes them before expiry and **stores the rotated refresh token atomically** (GitLab invalidates the old pair on refresh).
    - **Token fallback**: a personal access token (or project/group access token) with `api` and `write_repository`. AIDE validates it with `GET /api/v4/user` and `GET /api/v4/personal_access_tokens/self` (scopes, expiry) and warns 14 days before expiry.
    - "Continue with GitLab" on the sign-in page uses the same OAuth app; users are matched by `(gitlab, instance, user id)`.
    - A connection that GitLab rejects (401 after one refresh) is marked `invalid` and its owner is notified (v2 `connection_invalid`).
    - Given a user signs in with GitLab on gitlab.com for the first time, then an AIDE account is created (or linked by verified email only when the user confirms), and a GitLab connection exists.
20. **FR-113** With a GitLab connection, the **repo picker** lists projects the user can develop in (`GET /projects?membership=true&min_access_level=30&simple=true&search=`, keyset pagination), and **Create project**:
    - namespace: the user's personal namespace, or a group where `project_creation_level` allows the user (`GET /groups?min_access_level=30`);
    - name, path, description, visibility, `initialize_with_readme=true` (so `main` exists, `default_branch` set).
    - Errors: name or path taken → `REPO_NAME_TAKEN → 409`; not allowed in the group → `FORBIDDEN → 403`; GitLab.com project-creation limit (200/day) → `UPSTREAM_RATE_LIMITED → 429`.
    - Given a connected user, when they create `acme/todo-app` (private) in group `acme`, then the project exists with `main` and a README, and the wizard selects it.

### 3.6 Bitbucket Cloud

21. **FR-114** An Admin configures the **Bitbucket OAuth consumer** in **Admin → Integrations → Bitbucket**: client key and secret (encrypted), with the page listing the callback URL and the consumer permissions to tick (`account`, `email`, `repository:write`, `repository:admin`, `pullrequest:write`, `project`, `webhook`, §4.19), because Bitbucket fixes scopes on the consumer. It can also come from `secrets.env` (`Bitbucket__OAuth__*`). App passwords are **not supported** (removed by Atlassian on 2026-07-28).
22. **FR-115** In **Account → Connections → Bitbucket**, a user connects Bitbucket:
    - **OAuth** (authorization code; access tokens last 2 h; AIDE refreshes and always persists whatever refresh token comes back).
    - **Token fallback**: an Atlassian **API token with scopes** (`read:user`, `read:workspace`, `read:project`, `read:repository`, `write:repository`, `admin:repository`, `read:pullrequest`, `write:pullrequest`, `read:pipeline`, all `:bitbucket`), plus the Atlassian account email. AIDE validates with `GET /2.0/user` and checks each scope with a harmless call, listing missing scopes (`CONNECTION_SCOPE_INSUFFICIENT → 400`).
    - "Continue with Bitbucket" signs in through the consumer (identity from `GET /2.0/user`, email from `GET /2.0/user/emails`).
    - Given a user saves an API token without `write:pullrequest`, then `CONNECTION_SCOPE_INSUFFICIENT` lists it and nothing is saved.
23. **FR-116** With a Bitbucket connection, the **repo picker** lists repos across the user's workspaces where they have write access (`GET /user/workspaces`, then `GET /repositories/{workspace}?role=contributor&q=name~"…"`). **Create repository**:
    - workspace (from `GET /user/workspaces`), **project** (required by Bitbucket; picker from `GET /workspaces/{ws}/projects`), name, description, private;
    - `POST /repositories/{ws}/{slug}` with `scm: git`, then AIDE pushes an initial commit with a README on `main` (Bitbucket has no auto-init), so the default branch exists.
    - Errors: slug taken → `REPO_NAME_TAKEN → 409`; not permitted → `FORBIDDEN → 403`.
    - Given a user creates `acme/todo-app` in project `TODO`, then the repo exists with `main` holding a README and the wizard selects it.

### 3.7 Git hosts in the lifecycle

24. **FR-117** The Git provider set *(amends FR-82)* is `github_app`, `github_pat`, `gitlab`, `bitbucket` and `local`. A GitLab or Bitbucket project stores the instance, the external repo id and full path, and the **binding Owner's connection** (`git_connection_id`).
    - Background Git work (clone, fetch, push, MR/PR, merge, tag, release, polling) uses that connection's token, refreshed per job, passed as an `http.extraHeader` Basic header (`oauth2:<token>` for GitLab, `x-token-auth:<token>` for Bitbucket OAuth, `x-bitbucket-api-token-auth:<token>` for Bitbucket API tokens). Tokens are never written to disk, put in a remote URL, logged or given to an LLM (v2 §4.14).
    - If the binding Owner loses the Owner role, leaves, or deletes or invalidates the connection, the binding is cleared, Git jobs fail with `GIT_NOT_CONNECTED`, and Owners are notified (`git_binding_cleared`). Another Owner re-binds their own connection in Settings → Git, after AIDE checks it has developer/write access to the repo.
    - Branches stay `aide/<slug>/v<version>` on every host.
    - Given a GitLab project whose binding Owner leaves, when a push is due, then the job fails with `GIT_NOT_CONNECTED` and the remaining Owners are notified.
25. **FR-118** **Merge requests and pull requests** on the new hosts follow FR-36/FR-65 semantics (§4.19 mapping):
    - GitLab: one MR per release version, opened as draft (`Draft:` title prefix) and marked ready by removing the prefix. Bitbucket: one PR with `draft: true`, marked ready with `draft: false`.
    - The body links back to the AIDE project, as on GitHub.
    - Given a Bitbucket project whose execution run completes, then a draft PR from `aide/<slug>/v0.1.0` to `main` exists and the Release page links it.
26. **FR-119** **Merge readiness** *(amends FR-84)* on GitLab reads the MR's `detailed_merge_status` (re-polled while `unchecked`/`checking`/`preparing`/`approvals_syncing`), the approvals endpoint, protected branch merge levels and the head pipeline. On Bitbucket it reads `GET …/pullrequests/{id}/mergeability/checks` (ready when no check has `blocking: true`). Reasons map to the v2 reason codes (`review_required`, `checks_failing`, `checks_pending`, `conflicts`, `insufficient_permission`, `draft`, `discussions_unresolved`, `rebase_required`).
    - Given a GitLab MR with `detailed_merge_status = not_approved`, when readiness is checked, then `canMerge: false` with reason `review_required`.
    - Given a Bitbucket PR whose `standard_merge_check` for "require passing builds" is blocking and failed, then reason `checks_failing`.
27. **FR-120** **CI status** *(amends FR-85)* is a deterministic review input on every host: GitLab head pipeline and commit statuses (`GET …/repository/commits/:sha/statuses`), Bitbucket commit statuses (`GET …/commit/{sha}/statuses`). A failed status that the host requires for merge (GitLab "pipelines must succeed", Bitbucket `require_passing_builds_to_merge` as seen in mergeability checks) is a major `testing` finding; other failures are minor. Pending statuses are waited for up to 15 minutes (v2 rule).
    - Given a GitLab pipeline whose `test:backend` job fails on a project requiring pipelines to succeed, then a major finding "required pipeline job test:backend failed" with the job URL applies −0.10 to testing.
28. **FR-121** **Release** *(amends FR-64/65)*:
    - GitLab: merge with `PUT …/merge` passing `sha` (the reviewed head; 409 = head moved → re-review is required), `squash` per the project's merge strategy, `should_remove_source_branch: false`. The merged commit is `merge_commit_sha`, else `squash_commit_sha`, else (fast-forward) the head `sha`. Then `POST /projects/:id/releases` with `tag_name`, `ref` = merged commit, `tag_message` (annotated tag) and the AIDE release notes as `description`.
    - Bitbucket: `POST …/pullrequests/{id}/merge` with `merge_strategy` from the project setting (`merge_commit`, `squash` or `fast_forward`); a `202` is followed through `merge/task-status`, a `555` is retried. Then an **annotated tag** `POST …/refs/tags {name, target.hash, message}` whose message holds the release notes (Bitbucket has no Releases, D-V3-7). The full notes are kept on the AIDE release and shown on the Release page.
    - `manual` merge strategy and **awaiting merge** work as in v2 FR-65: AIDE marks the MR/PR ready and polls (§4.19 cadence) until merged (tag + release then complete) or closed (`PR_CLOSED`).
    - Errors: GitLab 405 / Bitbucket 400 (not mergeable) → `awaiting_merge` with readiness reasons; GitLab tag-creation limit (100 per 30 min) → retried at the reset.
    - Given a GitLab project with `aide_merges`, when the release job succeeds, then the MR is merged, a GitLab Release `v0.1.0` exists on the merged commit and the badge reads "Released 0.1.0".
    - Given a Bitbucket project, when the release completes, then the annotated tag `v0.1.0` exists on `merge_commit.hash` with the notes as its message.
29. **FR-122** **Import** *(amends FR-73)* works from GitLab and Bitbucket: the released-version picker lists GitLab Releases and semver tags (`GET …/releases`, `GET …/repository/tags`) or Bitbucket semver tags (`GET …/refs/tags`), the default branch comes from `default_branch` / `mainbranch.name`, and the stack is detected (FR-99).
    - Given a GitLab project with tags `v1.0.0` and `v1.1.0`, when it is imported, then `v1.1.0` is preselected and the version to develop defaults to `1.2.0`.

### 3.8 Platform connections

30. **FR-123** **Account → Connections** *(amends FR-87)* has four sections: LLM providers (v2), **Source control** (GitHub, GitLab per instance, Bitbucket), **Deployment platforms** (Vercel, Netlify, Cloudflare, Render, Railway) and **Databases** (Supabase).
    - A platform connection is a pasted token (§4.20 per provider: what to create, the scopes, how it's validated), with a label. AIDE validates on save, stores it encrypted, shows the account/team/workspace/org it reaches, scope, expiry and status, and never returns it. Re-test, rename and delete work as for v2 Vercel connections.
    - Existing v2 Vercel connections migrate unchanged into the generic model (§5).
    - A token the platform rejects (401, or the provider's invalid-token 403) marks the connection `invalid` and notifies its owner. A 403 for missing permissions marks it `insufficient_scope` with the missing permission names where the API returns them (Supabase `missing_permissions`, Cloudflare token policies).
    - Given a Cloudflare **account-owned** token (`cfat_` prefix), when it is saved, then `CONNECTION_SCOPE_INSUFFICIENT` explains that Workers Builds needs a user token.
    - Given a Supabase scoped token without "API Key Secrets: Read", then the connection saves with status `insufficient_scope` and the database setup page names the missing permission.
31. **FR-124** **GitHub Pages** needs no extra connection: it uses the project's GitHub App installation (or PAT). The AIDE GitHub App manifest adds **Pages: read & write** *(amends v2 §4.14)*. Existing App registrations show "Pages permission missing: update the App's permissions in GitHub, then accept them on each installation" until the installation token reports it.
    - Given an App without Pages permission, when an Owner selects GitHub Pages as the frontend host, then `PLATFORM_PERMISSION_MISSING → 409` with the App settings link.

### 3.9 Deployment targets and environments

32. **FR-125** **Settings → Deployment** *(replaces v2 FR-88's single Vercel setting)* shows one **target** card per component in the composition (frontend, backend), one **database** card per environment (production, preview), the environment inventory (FR-126) and the origin model. For each target, an Owner:
    - binds one of their own connections for the host chosen in the composition;
    - **creates** the platform resource linked to the repo, or **links** an existing one (picker), with the build configuration derived from the composition (§4.20: root directory, build command, output directory, start command, Dockerfile, health check path, pre-deploy migrate command);
    - sees a **readiness checklist** from `POST …/targets/:component/verify`: connection valid, platform Git app/credentials connected to the repo's host and repo, required env vars present, auto-deploys for `aide/**` disabled, preview environment ready, plan limits (§4.20).
    - The resource is created by a `deployment_setup` job (it may take several API calls and polls); the card shows its progress.
    - Errors: the platform's Git integration lacks access → `PLATFORM_GIT_NOT_CONNECTED → 409` with the platform's install or connect link; plan restriction (e.g. Vercel Hobby with an org repo, Netlify private org repo without Pro) → `PLATFORM_PLAN_LIMIT → 409`; the composition doesn't allow this host → `STACK_INCOMPATIBLE`.
    - Local Git projects may use targets only with the Fake provider (v2 decision kept).
    - Given an ASP.NET backend on Render, when the Owner creates the target, then a Docker web service with `dockerfilePath: backend/Dockerfile`, `healthCheckPath: /api/health/ready`, `autoDeployTrigger: "off"` for `aide/**` (production keeps auto-deploy from `main`, §4.20), and `preDeployCommand` running the EF migration bundle is created, plus a `-preview` service (FR-128).
33. **FR-126** The **environment inventory** *(amends FR-88 env vars)* lists, per variable: key, the components it applies to (frontend, backend), environments (preview, production), sensitive flag, public (`NEXT_PUBLIC_`/`VITE_`) flag, and its **source**:
    - `user`: entered by an Owner (value encrypted, never returned);
    - `derived`: written by AIDE from the databases, Supabase project and origin model (§4.22), read-only, shown with its origin ("from Render Postgres (production)");
    - `required`: declared by the packs and missing (flagged).
    - AIDE syncs the inventory to each target on save and before each deployment (§4.20 per provider: e.g. Netlify contexts `production`/`deploy-preview`/`branch-deploy`, Railway per environment with `skipDeploys: true`, Render per service with a full merged `PUT`). Sync failures keep the local value and show `syncError` (v2 rule).
    - A **public** variable whose key or value looks secret (§4.18 secret patterns) is refused (`VALIDATION`, "secret values can't be public").
    - Given a Vue + Supabase (BaaS) project, when the Supabase database is linked, then `VITE_SUPABASE_URL` and `VITE_SUPABASE_PUBLISHABLE_KEY` appear as derived, public, frontend variables for both environments, and no secret key is exposed to the frontend.
34. **FR-127** The **origin model** (§4.17 R10) is part of the composition and drives:
    - **same-origin**: one service serves both (Next.js full-stack; an SPA container proxying `/api` to the backend over the platform's private network on Railway; local Compose);
    - **proxy**: the frontend platform rewrites `/api/*` to `API_ORIGIN` (Next.js `rewrites()`, a Netlify `_redirects` file generated at build from `API_ORIGIN`, a Cloudflare Worker with `run_worker_first: ["/api/*"]`, Render static-site `routes` set by AIDE per service);
    - **cross-origin**: the frontend calls `API_BASE_URL` (build-time), the backend allows `CORS_ALLOWED_ORIGINS`, and auth uses bearer tokens or `SameSite=None; Secure` cookies.
    - The derived variables (`API_ORIGIN`, `API_BASE_URL`/`VITE_API_BASE_URL`/`BACKEND_URL`, `CORS_ALLOWED_ORIGINS`) are filled per environment from the backend target's production and preview URLs.
    - Given a React SPA on GitHub Pages with a FastAPI backend on Railway, then the origin model is `cross-origin`, `VITE_API_BASE_URL` is derived from the Railway production domain, and `CORS_ALLOWED_ORIGINS` on the backend includes the Pages URL.
35. **FR-128** **Preview environment** *(replaces FR-90's per-run Vercel preview)*: each project with deployment targets has **one persistent preview environment**, matching AIDE's one-development-version-at-a-time model (D-V3-10):
    - per target: Vercel/Netlify/Cloudflare preview deployments of the version branch; a Render `-preview` service per backend or SSR app (and static site when the frontend is on Render); a Railway `aide-preview` environment copied from production;
    - per database: a **preview database** (Render or Railway Postgres in the preview service/environment; a second Supabase project when the Owner links one), so migrations from unmerged code never touch production (§4.21);
    - When an execution run completes (PR opened or updated), a `preview_rollout` job deploys the PR head in dependency order: **preview database migrations** (Supabase BaaS by AIDE, §4.21) → **backend** (whose pre-deploy migrates its preview database) → **frontend** (built against the backend's preview URL, which is stable for the life of the version branch). Each step waits for `ready` before the next.
    - The Review page shows each component's preview URL, state and build-log tail. A Contributor can redeploy.
    - Review waits for the whole rollout (up to 30 minutes, v2 ceiling). A failed build or pre-deploy is a **critical `architecture`** finding with the log tail as evidence (v2 FR-90); a failed preview migration is a critical `data` finding.
    - Hosts without previews (GitHub Pages) are skipped with a note on the Review page; review doesn't wait for them.
    - Given a React (Netlify) + ASP.NET (Render) + Render Postgres project, when execution completes, then the preview database is migrated by the backend's pre-deploy on `<app>-api-preview`, the Netlify branch deploy is built with `API_ORIGIN` = the preview service URL, and the Review page lists both previews `ready`.
    - Given the backend preview fails its pre-deploy migration, then the frontend preview is not started, and the report has a critical finding with the pre-deploy log.
36. **FR-129** **Preview database reset**: an Owner can reset the preview database (drop and re-provision, or for Supabase, reset the linked preview project's schema by re-applying migrations from the base commit). Reset is offered automatically after a rollback that removed migrations (v1 FR-44) and is required before the next preview rollout when AIDE detects applied preview migrations that no longer exist in the branch (`PREVIEW_DATABASE_DIVERGED` warning, preview rollout blocked until reset).
    - Given a rollback to a checkpoint before migration `0003`, when the next preview rollout starts, then it is blocked with `PREVIEW_DATABASE_DIVERGED` and the Owner sees "Reset preview database".
37. **FR-130** **Production deployments** *(amends FR-91)*: after the release merge, a `production_rollout` job deploys every target in order **database migrations (Supabase BaaS only, §4.21) → backend → frontend**:
    - it adopts a deployment the platform started from the merge commit (Vercel, Netlify, Cloudflare production builds of the default branch; Render/Railway auto-deploys from `main`), matched by commit SHA within 60 s, or creates one explicitly (`POST /deploys {commitId}`, `serviceInstanceDeployV2(commitSha)`, Netlify `POST /builds` with the production branch, Workers Builds `POST /builds {branch, commit_hash}`);
    - idempotent per `(release_id, component)`; each deployment is recorded on the release.
    - A failed production deployment doesn't undo the release (v2), notifies Owners (`DEPLOYMENT_FAILED`) and shows "Redeploy" on the Release page.
    - GitHub Pages: AIDE waits for the Pages workflow run on the merge commit and reads `GET …/pages/deployments/{sha}` and `html_url`.
    - Given release 0.1.0 of the React + ASP.NET project merged, when both deployments are `ready`, then the Release page shows "Released 0.1.0 · live on Netlify and Render" with both URLs.
38. **FR-131** **Rollback and promote** *(amends FR-91)* per provider capability (§4.20): Vercel promote, Netlify `restore`, Cloudflare Workers deployment of an earlier version (100 %), Cloudflare Pages `rollback`, Render `rollback {deployId}`, Railway `deploymentRollback` (when `canRollback`). GitHub Pages has no rollback (`DEPLOYMENT_ACTION_UNSUPPORTED → 409`). Rolling back a backend never rolls back its database; the dialog warns when the target deployment is older than the latest applied migration.
    - Given a Render backend with two `live` production deploys, when an Owner rolls back to the older one, then Render's rollback deploy is recorded with trigger `rollback` and the Deployments page shows it as current.
39. **FR-132** The **Deployments** page *(amends FR-91)* lists every deployment with component, environment (preview/production), provider, version, SHA, state, URL, time and who triggered it, filterable by component and environment. The project overview shows the production URL of each component.

### 3.10 Managed databases and migrations

40. **FR-133** The **database card** (FR-125) for each environment lets an Owner:
    - **provision** a new database on the composition's database host: Supabase project (`POST /v1/projects` with `organization_slug`, `region_selection`, an AIDE-generated `db_pass`), Render Postgres (`POST /v1/postgres`, version 17), Railway Postgres (a Postgres service with a volume in the project's environment);
    - **link** an existing one (picker from the connection);
    - enter an **External** connection string (stored encrypted; `postgres://`/`postgresql://` URI, or an ADO.NET string for SQL Server).
    - A `database_provisioning` job polls until available (§4.21) and stores the connection details encrypted. The card shows status, region, plan, and warnings: Render free Postgres expiry (30 days + 14-day grace), Supabase free-plan pausing after 7 days of inactivity and the 2-free-projects limit (`PLATFORM_PLAN_LIMIT` with "link an existing project instead").
    - The preview database is created automatically with the preview service or environment on Render and Railway; for Supabase it is optional (the Owner links or provisions a second project).
    - Given an Owner provisions a Supabase project, when its status reaches `ACTIVE_HEALTHY` and the health check reports db, pooler, auth and rest healthy, then the card shows `available`, and the derived env vars appear (FR-126).
41. **FR-134** **Supabase BaaS** projects:
    - AIDE fetches the project's API keys (`GET …/api-keys?reveal=true`), using **publishable** (`sb_publishable_…`) and **secret** (`sb_secret_…`) keys; it creates them when missing. Legacy `anon`/`service_role` keys are used only for migrated v2 projects that already reference them.
    - It sets Auth `site_url` to the frontend production URL and `uri_allow_list` to the production URL, the preview URL pattern of the frontend host, and `http://localhost:<dev port>/**` (`PATCH …/config/auth`), and updates them when target URLs change.
    - **Migrations** in `supabase/migrations/*.sql` are applied by AIDE's own migration runner (§4.21) to the preview project during preview rollout and to the production project during release. After applying, AIDE reads `GET …/advisors/security` and adds its findings (e.g. a table without RLS) to the next review as deterministic findings.
    - Given a BaaS project releasing a version that adds `0003_orders.sql`, when the release job runs, then the migration is applied to the production project before the PR is merged (§4.21 "When"), recorded in `supabase_migrations.schema_migrations` with version `0003`'s timestamp, and listed under the release's migration run.
42. **FR-135** **Host-mode databases** (Supabase, Render, Railway or External as the Postgres for an ORM): AIDE never runs ORM migrations itself. The ORM's one-off migrate command (from the data pack) runs on the platform:
    - **Render/Railway backends**: the service's pre-deploy command (Render paid instances; on Render free instances the deploy engineer runs migrations at container start, single instance, recorded as a caveat);
    - **serverless hosts** (Vercel/Netlify functions, Next.js full-stack on Vercel/Netlify/Cloudflare): the production build command runs the migrate command only when `AIDE_RUN_MIGRATIONS=true`, which AIDE sets only in environments that have their own database (production, and preview when a preview database exists) (D-V3-13).
    - Connection variables: `DATABASE_URL` (Supabase: transaction pooler `:6543` with prepared statements off for serverless, session pooler `:5432` otherwise), `DIRECT_URL` (session pooler `:5432`, for migrations). Supabase direct `db.<ref>.supabase.co` is never used (IPv6-only).
    - Given a Next.js + Prisma project on Vercel with Supabase host mode and no preview database, then `AIDE_RUN_MIGRATIONS` is `true` for production only, preview builds skip migrations, and the Review page notes "Preview uses the production database read path without this version's migrations" (FR-128 rule: unmerged migrations never reach production).

### 3.11 Events, notifications and CLI

43. **FR-136** New events (live, activity feed, admin): `STACK_CHANGED`, `STACK_DETECTED`, `GIT_HOST_INSTANCE_CHANGED`, `GIT_BINDING_CLEARED`, `DEPLOYMENT_TARGET_CONFIGURED`, `DEPLOYMENT_TARGET_FAILED`, `DATABASE_PROVISIONED`, `DATABASE_PROVISIONING_FAILED`, `DATABASE_EXPIRING`, `DATABASE_RESET`, `MIGRATION_RUN_STARTED`, `MIGRATION_RUN_COMPLETED`, `MIGRATION_RUN_FAILED`, `PREVIEW_ROLLOUT_COMPLETED`. `PRESET_CHANGED` is retired (history kept).
    - New notification types: Git binding cleared (Owners), database provisioning failed (Owners), database expiring (Owners, 7 days before a Render free database expires or a Supabase project is paused), migration failed (Owners), deployment target needs attention (Owners: platform Git access lost, token invalid).
    - Given a Render free Postgres 7 days from expiry, then Owners get a "database expiring" notification with the date.
44. **FR-137** CLI additions *(amends FR-93)*:
    - `aide stack catalog` (layers and options), `aide stack show <id>`, `aide stack set <id> --frontend <opt> --backend <opt> --data <opt>[:baas|host] --vcs <opt> --frontend-host <p> --backend-host <p> --database-host <p> [--local compose|none]` (validated; prints violations), `aide stack detect <repo-url>` for a connected repo.
    - `aide version start … --preset` is replaced by the `--stack-*` flags above (`--preset` exits 2 with "presets were removed in v3; use --frontend …").
    - `aide deployments <id> [--component frontend|backend] [--env preview|production]`, `aide databases <id>`, `aide migrations <id>`.
    - `status` includes the composition summary, each target's production URL and the latest migration run.
    - Exit codes follow v1 (`STACK_INCOMPATIBLE`/`STACK_LOCKED` → 4).
    - Given `aide stack set <id> --backend be-aspnet --data data-prisma`, then it exits 4 and prints the `R3` violation.

## 4. Domain rules

v1 §4.1–4.9 and v2 §4.10, §4.13–§4.15 apply, with these amendments and additions. **v2 §4.11 and §4.12 are replaced** by §4.16–§4.18 and §4.23.

### 4.16 Stack catalog format *(replaces v2 §4.11)*

- The catalog is the folder `backend/src/Aide.Infrastructure/Agents/Stack/` (embedded resources), loaded once at startup into pure Domain types:

  ```
  Stack/
    catalog.json            version, layers[], options[], rules (§4.17), recommendations[]
    packs/<pack-id>/
      pack.json             manifest (below)
      skill.md              adapted SKILL.md; its "## " headings are the section interface
    core/
      stack-api-contract.md adapted stack-core skill
      composition-summary.md  template rendering the STACK.md equivalent for prompts
    agents/<key>.md         adapted stack-core agent prompts (FR-101)
  ```
- **Version and hash:** `catalog.json.version` (semver) plus a SHA-256 over all files (v2's preset hash algorithm: sorted paths, `path \0 content \0`, `\n` line endings, BOM stripped). Both are pinned on the release version and recorded on agent runs as `stack@<version>`.
- **`pack.json`** (unknown properties are load errors, as in v2):

  ```
  id (= folder, = stack-packs plugin name), layer (frontend|backend|data|vcs|deploy|local),
  name, version ("<plugin version>+aide.<n>"), source {plugin, version, snapshot: "2026-10-18"},
  chips[], description, defaultPath ("frontend"|"backend"|"."|null),
  publicEnvPrefix ("NEXT_PUBLIC_"|"VITE_"|null),          # frontend packs
  runtime ("node"|"python"|"dotnet"|"java"|"static"),       # frontend/backend packs, for deploy shapes
  modes[] {id, name, description}                           # e.g. data-supabase: baas, host; fe-nextjs: full-stack, ui
  specSections[] {title, guidance, when?}                   # when: a condition on the composition (e.g. "mode == baas")
  ownership {<agentKey>: [globs with {path} placeholders]}  # see §4.18
  agentSections {<agentKey>: ["*" | section heading]}       # which skill sections each agent loads
  planRules {order[], exclusive[], maxFilesPerStep[]}       # v2 rule shapes, globs with placeholders
  protectedPaths[] {glob, existingOnBase?, except?, reason}
  generatedFiles[] {glob, handling: hand_written|ci_checked|ci_generated, ciCheck?}
  requiredEnv[] {key, appliesTo[], targets[], sensitive, public, description, when?}
  review {checklist[] {item, category, severity}, deterministic[] (rule ids, §4.18)}
  gate (the pack's Gate commands, for the ci-engineer and the composition summary)
  migrate {command, needsDirectUrl}                         # data packs: the one-off migrate command
  deploy {...}                                              # deploy packs: provider id and supported shapes (§4.17 R6–R8)
  ```
- **Adaptation rules** (D-V3-3, extending v2's claude-kits rules):
  - Keep each pack's required section headings from stack-packs `author-pack` (they are the interface): frontend `Detect · Scaffold · Conventions · API consumption · Errors, permissions, states · Config · Testing · Gate · Containerise · Deploy notes · Checklist`; backend, data, VCS and deploy likewise.
  - Drop: CLI scaffolding commands (the scaffolder writes the files they would create), "run X", "only when the user asks" remote actions (AIDE performs Git and platform operations itself, under its own rules), MCP setup, `link`/`login` steps, and anything about pushing, PRs or reviews.
  - Keep, as output requirements: conventions, folder layouts, error and auth patterns, env names, health paths, container recipes, gate commands (for CI files), migration rules and checklists.
  - Replace "current stable, check the docs" with "use current stable versions you are confident exist; pin Node/.NET/Java/Python versions in their version files".
  - Every adapted file starts with a comment naming its source file and plugin version. Content changes bump the pack's `+aide.<n>` revision and the catalog version.
- Missing files, unknown section headings in `agentSections`, unknown pack ids in rules or invalid globs fail loading (FR-103).

### 4.17 Compatibility rules

The rules are data in `catalog.json`, evaluated by one pure Domain function `StackCompatibility.Evaluate(partialComposition) → options per layer with state and reasons`, used by `/options`, `/validate`, project create, `PUT /stack`, `POST /versions` and the CLI. Rule ids are stable (tests and violations cite them). They are taken from stack-packs `compose-stack` and the provider research in §4.19–§4.21.

| Rule | Statement |
| --- | --- |
| R1 | Frontend and Backend can't both be None. |
| R2 | `fe-nextjs` mode is derived: **full-stack** when Backend = None, **ui** when there's a backend. |
| R3 | Data ↔ backend: `data-efcore-postgres`, `data-efcore-sqlserver` → `be-aspnet`; `data-jpa-flyway` → `be-spring-boot`; `data-sqlalchemy` → `be-fastapi`; `data-prisma`, `data-drizzle` → `be-express`, `be-nestjs`, or Backend None with `fe-nextjs`; `data-supabase` (BaaS) → Backend None and any frontend; None → always. |
| R4 | `data-efcore-sqlserver` is a `caveat`: "No managed SQL Server on the catalog's platforms: use an External connection string (e.g. Azure SQL) or local only." |
| R5 | VCS: any option, but fixed to the repo's host once a repo is chosen or imported (`VCS_MISMATCH`). |
| R6 | Frontend host by frontend: static SPAs (`fe-react-vite`, `fe-vue`, `fe-angular`, `fe-vanilla`) → Vercel, Netlify, Cloudflare, GitHub Pages, Render (static site), Railway (container). `fe-nextjs` → Vercel; Netlify; Render (Node service); Railway; Cloudflare `caveat` ("OpenNext adapter; some Node middleware limits"); GitHub Pages only in **ui** mode or with `data-supabase`, as `caveat` ("static export: no server actions, route handlers or SSR"), otherwise forbidden ("Next.js full-stack needs a server"). |
| R7 | Backend host by backend: `be-aspnet`, `be-spring-boot` → Render (Docker), Railway; `be-fastapi` → Vercel `caveat` ("serverless functions: no background work, pooled DB connections"), Render, Railway; `be-express`, `be-nestjs` → Vercel `caveat` (same), Render, Railway. Netlify, Cloudflare and GitHub Pages can't host backends ("not covered by the deploy pack"). |
| R8 | Database host by data: ORM packs on PostgreSQL (`data-efcore-postgres`, `data-jpa-flyway`, `data-sqlalchemy`, `data-prisma`, `data-drizzle`) → Supabase (host mode), Render Postgres, Railway Postgres, External, None. Render/Railway Postgres are `allowed` when the backend (or Next.js full-stack app) runs on the same platform and `caveat` otherwise ("database on another platform: public connection, higher latency"). `data-efcore-sqlserver` → External or None. `data-supabase` (BaaS) → Supabase only. Data None → no database host. |
| R9 | Deploy ↔ VCS (each platform's Git integration, §4.20): GitHub → every platform; **gitlab.com** → Vercel, Netlify, Cloudflare, Render (not Railway: "Railway only builds from GitHub", not GitHub Pages); **self-managed GitLab** → Netlify only ("only Netlify builds from self-managed GitLab"); **Bitbucket** → Vercel, Netlify, Render (not Cloudflare, not Railway, not GitHub Pages). Netlify with GitLab or Bitbucket is a `caveat` ("linked with a deploy key and webhook that AIDE adds to the repo"). |
| R10 | Origin model: Next.js full-stack, or no backend → `same-origin`. Backend and SPA both on Railway → `same-origin` (SPA container proxies over the private network) or `cross-origin`. Frontend on Netlify, Cloudflare or Render static, or `fe-nextjs` ui mode anywhere → `proxy` (default) or `cross-origin`. Frontend on Vercel as a static SPA, or on GitHub Pages → `cross-origin` only. Frontend host None → `same-origin` (local). |
| R11 | Every host may be None (local only). A database host other than None needs a deploy target that can reach it (a backend host, or the frontend host in Next.js full-stack / BaaS); otherwise `caveat` "database only used by the local dev loop". |
| R12 | `local-docker-desktop` (Compose dev loop) is offered when there's a backend or a database; it's the default then. |

Worked examples (each is a unit test):

| # | Composition | Result |
| --- | --- | --- |
| 1 | `fe-react-vite` · `be-aspnet` · `data-efcore-postgres` · gitlab.com · frontend Netlify, backend Render, DB Render Postgres | ✓ allowed; Netlify `caveat` (R9 deploy key); origin `proxy` |
| 2 | as 1 with backend host Vercel | ✗ R7 "Vercel can't run ASP.NET Core" |
| 3 | `fe-vue` · None · `data-supabase` · Bitbucket · frontend Cloudflare | ✗ R9 "Cloudflare can't build Bitbucket repos"; Vercel, Netlify, Render offered |
| 4 | `fe-nextjs` · None · `data-prisma` · GitHub · frontend GitHub Pages | ✗ R6 "Next.js full-stack needs a server"; Vercel ✓ with DB Supabase (host) |
| 5 | None · `be-fastapi` · `data-sqlalchemy` · GitHub · backend Railway, DB Railway Postgres | ✓; origin n/a (API only) |
| 6 | `fe-angular` · `be-spring-boot` · `data-jpa-flyway` · self-managed GitLab · backend Render | ✗ R9; backend host offers only None |
| 7 | `fe-react-vite` · `be-aspnet` · `data-efcore-sqlserver` · GitHub · backend Railway, DB External | ✓ with R4 `caveat` |
| 8 | `fe-vanilla` · None · None · GitHub · frontend GitHub Pages | ✓; origin `same-origin` |
| 9 | `fe-react-vite` · `be-nestjs` · `data-drizzle` · GitHub · frontend Vercel, backend Render, DB Supabase | ✓; origin `cross-origin` only (R10); Supabase host mode |
| 10 | `fe-nextjs` · `be-express` · `data-prisma` · gitlab.com · frontend Vercel, backend Railway | ✗ R9 "Railway only builds from GitHub" |

`POST /api/stack/options` returns, for each layer after the last chosen one, every option with `state` and `reasons[] {rule, text}`; earlier layers are re-evaluated so a now-forbidden choice is reported as `invalidated`.

### 4.18 Composition → agents, skills, rules and checks

- **Normalised composition** (stored on the release version as `jsonb`, validated by the Domain):

  ```
  { catalogVersion, catalogHash,
    frontend: {pack, path} | null, backend: {pack, path} | null,
    data: {pack, mode?} | null, vcs: {pack},
    deploy: {frontendHost?, backendHost?, databaseHost?, local: "compose"|"none"},
    originModel, derived: {nextjsMode?, agents[], specSections[], envNames[], gates[]} }
  ```
- **Skill resolution** (which sections each specialist loads; `*` = the whole adapted skill):

  | Agent | Loads |
  | --- | --- |
  | `scaffolder` | every selected pack: `Detect`, `Scaffold`, `Conventions`, `Config`; plus `stack-api-contract` when there's a backend |
  | `data-engineer` | data pack `*`; with database host Supabase in host mode, also `data-supabase`: `Production notes` |
  | `backend-engineer` | backend pack `*`, `stack-api-contract`, data pack `Setup` and `Modelling` (to use the client, not to own the schema) |
  | `frontend-engineer` | frontend pack `*`; `stack-api-contract` when there's a backend; `data-supabase` `*` in BaaS mode; data pack `Setup` in Next.js full-stack mode |
  | `test-engineer` | `Testing` and `Gate` of every selected frontend/backend/data pack |
  | `ci-engineer` | VCS pack `*`; `Gate` of every other selected pack; data pack `Migrations` |
  | `deploy-engineer` | each selected deploy pack `*`; frontend/backend `Containerise` and `Deploy notes`; data pack `Migrations` and `Production notes`; `local-docker-desktop` `*` when the dev loop is Compose |
  | `docs-writer` | the composition summary only (README outline from `stack-docs-writer`) |
- **Ownership** (globs expanded with `{fe}`, `{be}` = the layer paths; examples from the pack files):

  | Agent | Owns |
  | --- | --- |
  | `scaffolder` | `**` (any file), but only in step 1 of an empty repo or the first step of a stack change (plan rule) |
  | `data-engineer` | data pack paths, e.g. EF: `{be}/src/*Infrastructure*/Persistence/**`, `{be}/src/*Infrastructure*/Migrations/**`; Flyway: `{be}/src/main/resources/db/migration/**`, `{be}/src/main/java/**/domain/**/*Entity.java`; Alembic: `{be}/alembic/**`, `{be}/app/models/**`; Prisma: `{be or fe}/prisma/**`; Drizzle: `{be or fe}/src/db/**`, `{be or fe}/drizzle/**`; Supabase: `supabase/**`, `{fe}/src/lib/database.types.ts` |
  | `backend-engineer` | `{be}/**` except the data-engineer's globs and the deploy files; `{be}/openapi/api.json` |
  | `frontend-engineer` | `{fe}/**` except data, deploy and E2E globs; in Next.js full-stack, also `{fe}/src/app/api/**`, `{fe}/src/server/**` |
  | `test-engineer` | `**/e2e/**`, `**/playwright.config.*`, backend integration test projects/folders (`{be}/tests/**`, `{be}/test/**`, `{be}/src/test/**`) |
  | `ci-engineer` | `.github/**` (except `pages.yml`), `.gitlab-ci.yml`, `.gitlab/**`, `bitbucket-pipelines.yml`, `renovate.json`, `scripts/ci/**`, `docs/pr/**` |
  | `deploy-engineer` | `**/Dockerfile`, `**/.dockerignore`, `compose.yaml`, `**/nginx.conf`, `vercel.json`, `netlify.toml`, `**/_redirects`, `**/wrangler.jsonc`, `{fe}/worker/**`, `.github/workflows/pages.yml`, `render.yaml` (documentation only), `.env.example` |
  | `docs-writer` | `README.md`, `docs/**` (except `docs/pr/**`) |
- **Plan rules** (composed; v2 rule shapes): scaffold first when the repo is empty (scaffolder exempt from order rules, per the v2 decision); the data pack's migrations before backend (or Next.js server) steps that use them; a step changing `{be}/openapi/api.json` before frontend steps that change the generated client path (soft); exclusivity: data-engineer steps must not declare frontend UI or endpoint files, backend/frontend steps must not declare migration globs; one migration per step; ci-engineer is the only agent for CI files; deploy-engineer is the only agent for platform config.
- **Spec sections** (union, deduplicated by title, each with the pack's guidance): any backend → "API Contract & Error Codes"; any data pack → "Data Model & Migrations"; `data-supabase` BaaS → "Data Model & RLS Policies", "Auth Flows" (replacing the former); `fe-nextjs` → "Routes & Rendering Strategy"; frontend without backend or data → "Data Sources & Consumed APIs"; any deploy host → "Environments & Configuration"; origin model `proxy` or `cross-origin` → "Origin Model (CORS, Cookies, Auth)"; stack change → "Stack Migration".
- **Review checks** (deterministic, language-independent text checks; ids are registered by packs; severities per v1 §4.7):

  | Rule id | Packs | Finding |
  | --- | --- | --- |
  | `rls_on_every_table` | `data-supabase` BaaS | `create table` in `supabase/migrations/` without `enable row level security` → critical security (v2 rule) |
  | `grants_for_exposed_tables` | `data-supabase` BaaS | a new `public` table without a `grant … to authenticated`/`anon` → major `api_contracts` ("not exposed to the Data API", Supabase 2026 default) |
  | `no_secret_in_client` | `fe-nextjs`, `fe-react-vite`, `fe-vue`, `fe-vanilla`, `fe-angular` | a public-prefixed name (`NEXT_PUBLIC_`/`VITE_`) or Angular `environment*.ts` key matching the secret patterns (`SECRET`, `SERVICE_ROLE`, `PRIVATE`, `PASSWORD`, `sb_secret_`, `sk_live`), or a non-public env name read in a `"use client"` file → critical security (generalises v2) |
  | `no_ensure_created` | `data-efcore-*` | `EnsureCreated(` or `Database.Migrate(` outside tests → major architecture |
  | `no_ddl_auto_update` | `data-jpa-flyway` | `ddl-auto` `update`/`create`/`create-drop` outside test config → major architecture |
  | `no_create_all` | `data-sqlalchemy` | `create_all(` outside tests → major architecture |
  | `no_schema_push` | `data-prisma`, `data-drizzle` | `prisma db push` / `drizzle-kit push` in package scripts → major architecture |
  | `migration_backward_compatible` | every data pack | `drop table`, `drop column`, `rename column` or a type change in a new migration → major `data` ("expand/contract required", because preview and production deploys aren't atomic) |
  | `container_non_root` | backend packs with a Docker deploy | a backend `Dockerfile` without a non-root `USER` → minor security |
  | `deploy_branch_control` | `deploy-vercel`, `deploy-netlify`, `deploy-cloudflare` | `vercel.json` missing `git.deploymentEnabled {"aide/**": false}`; `netlify.toml` missing the `aide/*` ignore rule; Workers preview trigger without the `aide/*` exclude → major architecture (generalises v2 FR-89) |
  | `health_endpoints` | backend packs | the pack's live/ready health paths absent from the source → minor feature_coverage |
- **Generated files** (FR-108) per pack, examples: `be-*` → `{be}/openapi/api.json` `hand_written` + CI export-and-diff; `fe-react-vite`/`fe-vue`/`fe-vanilla` → `{fe}/src/api/generated/**` `hand_written` + CI regenerate-and-diff; `fe-angular` → Orval output, same; `data-efcore-*` → migration + `*ModelSnapshot.cs` `hand_written` + CI `dotnet ef migrations has-pending-model-changes`; `data-supabase` → `database.types.ts` `hand_written` (v2 decision); lockfiles → `ci_generated` (absent until the first CI run).
- **Detection** (FR-99), a pure function over the file list and manifest contents: `next` in a `package.json` → `fe-nextjs`; `@angular/core` + `angular.json` → `fe-angular`; `vue` + `vite` → `fe-vue`; `react` + `vite` → `fe-react-vite`; `index.html` with no framework dependency → `fe-vanilla`; `*.csproj` referencing `Microsoft.AspNetCore` → `be-aspnet`; `spring-boot-starter-web` in `pom.xml`/`build.gradle*` → `be-spring-boot`; `fastapi` in `pyproject.toml`/`requirements*.txt` → `be-fastapi`; `@nestjs/core` → `be-nestjs`; `express` (without Nest) → `be-express`; `Npgsql.EntityFrameworkCore.PostgreSQL` → `data-efcore-postgres`; `Microsoft.EntityFrameworkCore.SqlServer` → `data-efcore-sqlserver`; `flyway` + JPA → `data-jpa-flyway`; `alembic` + `sqlalchemy` → `data-sqlalchemy`; `prisma` → `data-prisma`; `drizzle-orm` → `data-drizzle`; `supabase/config.toml` with no other data pack → `data-supabase` BaaS; with one → host mode noted; `vercel.json`/`netlify.toml`/`wrangler.jsonc`/`render.yaml`/`.railway/` or `railway.json` → the host suggestion. Each match carries its evidence; conflicting matches leave the layer empty with both shown.

### 4.19 Git hosts (GitLab, Bitbucket)

- **Common:** the provider interface (v2 `IGitProvider`) gains `ListReposAsync`, `ListOwnersAsync`, `CreateRepoAsync`, `GetMergeReadinessAsync`, `GetCommitStatusesAsync`, `FindReleaseRefsAsync` and `PublishReleaseAsync`, implemented for GitHub (existing behaviour), GitLab, Bitbucket and Local. Host base URLs are fixed (`https://gitlab.com`, `https://api.bitbucket.org/2.0`, `https://bitbucket.org`) except Admin-registered GitLab instances.
- **GitLab** (`{base}/api/v4`, `Authorization: Bearer <token>`):

  | Operation | Call |
  | --- | --- |
  | Identity | `GET /user`; OIDC `GET /oauth/userinfo` |
  | OAuth | `/oauth/authorize` (code + PKCE S256), `/oauth/token` (rotating refresh), `/oauth/revoke`; scopes `api write_repository openid profile email` |
  | Token info | `GET /personal_access_tokens/self` (scopes, `expires_at`) |
  | Repos / owners | `GET /projects?membership=true&min_access_level=30&simple=true&search=&pagination=keyset&order_by=id`; `GET /groups?min_access_level=30` + `project_creation_level` |
  | Create | `POST /projects {name, path, namespace_id?, visibility, initialize_with_readme: true, default_branch: "main"}` |
  | Default branch, merge settings | `GET /projects/:id` → `default_branch`, `merge_method` (`merge`/`rebase_merge`/`ff`), `squash_option`, `only_allow_merge_if_pipeline_succeeds` |
  | Tags / releases | `GET /projects/:id/repository/tags`, `GET /projects/:id/releases`; `POST /projects/:id/releases {tag_name, ref, name, description, tag_message}` |
  | MR | `POST /projects/:id/merge_requests {source_branch, target_branch, title: "Draft: …", description, remove_source_branch: false}`; `PUT …/merge_requests/:iid {title}` (ready = prefix removed); `GET …/merge_requests?source_branch=&state=opened` |
  | Readiness | `GET …/merge_requests/:iid` (`detailed_merge_status`, `has_conflicts`, `blocking_discussions_resolved`, `sha`, `head_pipeline`); `GET …/approvals` (`approved`, `approvals_left` on Premium only); `GET /projects/:id/protected_branches/:name` (`merge_access_levels`) |
  | Merge | `PUT …/merge_requests/:iid/merge {sha, squash, should_remove_source_branch: false}`; 405 not mergeable, 409 head moved, 422 failed; result SHA = `merge_commit_sha` ?? `squash_commit_sha` ?? `sha` (fast-forward) |
  | CI | `GET /projects/:id/repository/commits/:sha/statuses`; `GET /projects/:id/pipelines?sha=` |
  | Netlify link support | `POST /projects/:id/deploy_keys {title, key, can_push: false}`, `POST /projects/:id/hooks {url, push_events: true}` |

  Status values `unchecked`, `checking`, `preparing`, `approvals_syncing` are transient. Required roles: Developer for push, MR and tags; **Maintainer** for merging into the protected default branch and for releases (a 403 maps to `insufficient_permission`). Minimum supported version: GitLab 17.11 (checked on instance add and shown as a warning if older).
- **Bitbucket Cloud** (`https://api.bitbucket.org/2.0`, OAuth Bearer or API-token Basic `email:token`):

  | Operation | Call |
  | --- | --- |
  | Identity | `GET /user`, `GET /user/emails` |
  | OAuth | `https://bitbucket.org/site/oauth2/authorize`, `/site/oauth2/access_token` (client Basic auth); scopes fixed on the consumer |
  | Workspaces / repos | `GET /user/workspaces`; `GET /repositories/{ws}?role=contributor&q=name~"…"&sort=-updated_on` (follow `next`) |
  | Create | `GET /workspaces/{ws}/projects`; `POST /repositories/{ws}/{slug} {scm: "git", is_private, project: {key}, description}`; initial commit pushed by AIDE |
  | Default branch | `GET /repositories/{ws}/{slug}` → `mainbranch.name` |
  | Tags | `GET …/refs/tags`; `POST …/refs/tags {name, target: {hash}, message}` (annotated; full SHA) |
  | PR | `POST …/pullrequests {title, description, source.branch.name, destination.branch.name, close_source_branch: false, draft: true}`; `PUT …/pullrequests/{id} {draft: false}`; `GET …/pullrequests?state=OPEN&q=source.branch.name="…"` |
  | Readiness | `GET …/pullrequests/{id}/mergeability/checks` (`type`, `status` PASSED/FAILED/PENDING/SKIPPED/UNKNOWN, `required`, `blocking`) |
  | Merge | `POST …/pullrequests/{id}/merge {merge_strategy, close_source_branch: false, message}`; 202 + `Location` → poll `…/merge/task-status/{task}`; 409 refs changed; 555 timeout (retry); result SHA = `merge_commit.hash` |
  | CI | `GET …/commit/{sha}/statuses` (`FAILED`, `INPROGRESS`, `STOPPED`, `SUCCESSFUL`) |
  | Netlify link support | `POST …/deploy-keys {key, label}`, `POST …/hooks {url, events: ["repo:push"], active: true}` |

  Merge strategies map from AIDE's per-project setting: `merge_commit` (default), `squash`, `fast_forward`.
- **Polling budget** (no webhooks, v2 D-V2-8): GitLab merge poll every 2 min for an hour, then 10 min (v2); status polls every 30 s while waiting, backing off on `RateLimit-Remaining` < 10 % and on 429 (`Retry-After`). Bitbucket allows **1,000 requests per hour per user on repository endpoints** with no remaining-count headers, so AIDE keeps a **per-connection token bucket** (900/h for background work), polls a PR no more than once a minute, and uses the single mergeability call instead of separate restriction, approval and status calls. Rate-limited jobs are re-queued without counting an attempt (v2 rule). No ETag support is assumed for either host.

### 4.20 Deployment platforms

- **Provider interface** *(generalises v2 `IDeploymentProvider`)*: `ValidateConnectionAsync`, `ListAccountsAsync`, `ListResourcesAsync` (link picker), `SetupTargetAsync` (create or link, branch control, preview resource), `VerifyTargetAsync` (readiness checklist), `SyncEnvAsync`, `DeployAsync {environment, ref, sha}`, `GetDeploymentAsync`, `GetBuildLogTailAsync`, `FindDeploymentForShaAsync`, `CancelAsync`, `RollbackAsync`, plus a `Capabilities` record (`ShaPinned`, `Previews`, `Rollback`, `PerEnvironmentVariables`, `RuntimeSecrets`). Providers: Vercel, Netlify, Cloudflare (Workers Builds; Pages adopt-only), Render, Railway, GitHub Pages, Fake.
- **Deployment states** (v2 §4.15) `queued → building → ready | error | canceled`, mapped per provider:

  | Provider | Token (pasted) and validation | Create / link | Branch control for `aide/**` | Preview | Production | Rollback | State mapping | Logs |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | **Vercel** (v2) | team/account token; `GET /v2/user` | `POST /v11/projects` with `gitRepository` (GitHub, GitLab, Bitbucket) | `vercel.json` `git.deploymentEnabled {"aide/**": false}` (minimatch; no other matching `true` rule) | `POST /v13/deployments` `gitSource {ref, sha}` | adopt auto-deploy of merge SHA, else create | `POST /v10/projects/{id}/promote/{deploymentId}` | v2 | v2 events API |
  | **Netlify** | personal access token (expiry chosen by user); `GET /user`, `GET /accounts` | `POST /{account_slug}/sites` with `repo {provider, id, repo_path, repo_branch, cmd, dir, allowed_branches}`: GitHub via `installation_id` (Netlify GitHub App installed by the user), GitLab/Bitbucket via `POST /deploy_keys` + repo deploy key + push webhook to `https://api.netlify.com/hooks/<provider>` added by AIDE | `allowed_branches: [<production branch>]`, Deploy Previews off, and `netlify.toml` `[build] ignore` exiting 0 for `aide/*` | `POST /sites/{id}/builds?branch=<version branch>&title=aide:<run>` (builds branch HEAD: AIDE checks `deploy.commit_ref` = PR head, else re-triggers; falls back to a build hook, which ignore rules don't cancel) | adopt `GET /sites/{id}/deploys?production=true` with `commit_ref` = merge SHA, else `POST /builds` | `POST /sites/{id}/deploys/{deploy_id}/restore` (reads `locked` after) | `new/pending_review/accepted/enqueued` → queued; `building/uploading/uploaded/preparing/prepared/processing/processed/retrying` → building; `ready` → ready; `error/rejected` → error | best effort: `deploy.error_message` + `admin_url` (no REST log endpoint) |
  | **Cloudflare** (Workers + Workers Builds) | **user** API token (Workers Scripts Edit, Workers CI Edit, Account Settings Read, User Details Read; + Pages Edit for Pages mode); `GET /user/tokens/verify`, `GET /accounts` | user installs the Cloudflare GitHub/GitLab app in the dashboard; AIDE `PUT /builds/repos/connections`, `POST /builds/tokens`, and two triggers: production (`branch_includes: [main]`, `deploy_command: npx wrangler deploy`) and preview (`branch_excludes: ["aide/*"]`, `deploy_command: npx wrangler versions upload`); first Worker bootstrap per §11 check | preview trigger excludes `aide/*` | `POST /builds/triggers/{preview}/builds {branch, commit_hash}` (**SHA-pinned**); `preview_url` from `GET /builds/builds/{uuid}` | adopt production build with `build_trigger_metadata.commit_hash` = merge SHA, else start it with the hash | `POST /workers/scripts/{name}/deployments {strategy: percentage, versions: [{version_id, percentage: 100}]}` | `queued/initializing` → queued; `running` → building; `stopped` + `build_outcome` success → ready, fail/terminated → error, cancelled/skipped → canceled | `GET /builds/builds/{uuid}/logs` (cursor) |
  | **Cloudflare Pages** (adopt existing only) | as above + Pages Edit | link existing project; set `source.config.preview_deployment_setting: custom`, `preview_branch_excludes: ["aide/*"]` | as left | `POST /pages/projects/{name}/deployments` form `branch` (HEAD; verify `deployment_trigger.metadata.commit_hash`) | adopt | `POST …/deployments/{id}/rollback` | `latest_stage` `deploy`+`success` → ready; `failure` → error; `canceled/skipped` → canceled; else building | `GET …/deployments/{id}/history/logs` |
  | **Render** | API key (account-wide; user then picks a workspace from `GET /v1/owners`); `GET /v1/users` | user connects the Git host once in Render (GitHub, GitLab, Bitbucket); AIDE `POST /v1/services` (`static_site {buildCommand, publishPath, routes}` or `web_service {runtime: docker|node|python, envSpecificDetails, preDeployCommand, healthCheckPath, plan, region}`, `rootDir`, `branch: main`, `autoDeployTrigger: "commit"` on production) | production service tracks `main` only; preview service `<name>-preview` has `autoDeployTrigger: "off"` | `PATCH` preview service `branch` to the version branch, then `POST /v1/services/{id}/deploys {commitId}` (**SHA-pinned**) | adopt the auto-deploy of the merge SHA, else `POST /deploys {commitId}` | `POST /v1/services/{id}/rollback {deployId}` | `created/queued` → queued; `build_in_progress/update_in_progress/pre_deploy_in_progress` → building; `live` → ready; `build_failed/update_failed/pre_deploy_failed` → error; `canceled/deactivated` → canceled | `GET /v1/logs?ownerId&resource[]&type=build&startTime&endTime&direction=backward&limit=100` (30/min) |
  | **Railway** | workspace token (`Authorization: Bearer`; account token accepted); `query { workspace(workspaceId) }` / `me` | user installs the Railway GitHub app; `projectCreate {workspaceId, prDeploys: false}`, `serviceCreate {projectId, source: {repo}, branch: "main"}`, `serviceInstanceUpdate {rootDirectory, dockerfilePath | buildCommand, startCommand, healthcheckPath}`, `serviceDomainCreate`; pre-deploy command per §11 check | services trigger only on `main`; PR environments off | `environmentCreate {name: "aide-preview", sourceEnvironmentId: production, skipInitialDeploys: true}` once, then `serviceInstanceDeployV2(serviceId, previewEnvId, commitSha)` (**SHA-pinned**) | `serviceInstanceDeployV2(prodEnvId, mergeSha)` or adopt | `deploymentRollback(id)` when `canRollback` | `QUEUED/WAITING/INITIALIZING` → queued; `BUILDING/DEPLOYING` → building; `SUCCESS` → ready; `FAILED/CRASHED` → error; `SKIPPED/REMOVED` → canceled | `buildLogs(deploymentId, limit: 100)` |
  | **GitHub Pages** | the project's GitHub App/PAT (Pages RW) | `POST /repos/{o}/{r}/pages {build_type: "workflow"}`; deploy-engineer writes `.github/workflows/pages.yml` | n/a (only the default branch deploys) | none (Capabilities.Previews = false) | Actions run on the merge SHA, then `GET /repos/{o}/{r}/pages/deployments/{sha}` and `GET …/pages` (`html_url`) | none | `deployment_in_progress/syncing_files/updating_pages` → building; `succeed` → ready; failures → error | failed workflow job log URL |
- **Environment variable sync:** Vercel (v2); Netlify `POST/PATCH /accounts/{account_id}/env?site_id=` with contexts `production` and, for preview, both `deploy-preview` and `branch-deploy` (API branch builds run as branch deploys), `is_secret` for sensitive values; Cloudflare build variables per trigger (`PATCH /builds/triggers/{uuid}/environment_variables`, `is_secret`) and runtime secrets `PUT /workers/scripts/{name}/secrets` (shared by preview and production: a preview-only secret is refused with `VALIDATION`); Render per service with `PUT /v1/services/{id}/env-vars` carrying the **full merged set** (it replaces), never deploying; Railway `variableCollectionUpsert {projectId, environmentId, serviceId, variables, replace: false, skipDeploys: true}`.
- **Polling cadence:** v2's 10 s for 2 min then 30 s, 30-minute ceiling, except **Railway** (≥ 20 s; Free plan 100 requests/hour), **Render logs** (≤ 30/min per user) and **Netlify** (deploy creation ≤ 3/min and 100/day per account, enforced before calling). All honour 429 `Retry-After`/reset headers without counting an attempt.
- **Plan and cost notes surfaced in the readiness checklist** (not enforced): Netlify production deploys cost 15 credits on credit plans, and the Free plan pauses all sites when credits run out; Render free web services sleep after 15 minutes and free Postgres expires after 30 days; Railway Free/Trial limits; Workers Builds 20-minute build timeout.
- **Fake provider** (v2, generalised): every provider id has a Fake mode (`Deployments:Provider=Fake` applies to all) that reaches `ready` after 2 polls with URL `https://fake-<sha8>-<component>.<provider>.test`, fails when the branch contains `.aide-fake/fail-build`, and fails a backend pre-deploy when it contains `.aide-fake/fail-migration`.

### 4.21 Managed databases and migrations

- **Database states:** `provisioning → available | error`, plus `paused` (Supabase `INACTIVE`), `expiring` (Render free within 7 days of `expiresAt`) and `removed`. Polling every 10 s, 10-minute ceiling (`DATABASE_PROVISIONING_TIMEOUT`).
- **Supabase** (`https://api.supabase.com/v1`, scoped PAT, 120 requests/min per user per endpoint; 429 with `X-RateLimit-Reset`):

  | Step | Call |
  | --- | --- |
  | Validate | `GET /organizations`, `GET /organizations/{slug}/projects` (401 invalid; 403 `missing_permissions` → `insufficient_scope`) |
  | Create | `POST /projects {name, organization_slug, db_pass, region_selection: {type: "specific", code} | {type: "smartGroup", code: americas|emea|apac}}` (`plan`, `region`, `organization_id` are deprecated; never sent) |
  | Wait | `GET /projects/{ref}` until `ACTIVE_HEALTHY` (`INIT_FAILED` → error), then `GET /projects/{ref}/health?services=db,pooler,auth,rest` all `ACTIVE_HEALTHY` |
  | Keys | `GET /projects/{ref}/api-keys?reveal=true` (`type` legacy/publishable/secret); `POST /projects/{ref}/api-keys {type, name}` when missing |
  | Connection | `GET /projects/{ref}/config/database/pooler` → session (`:5432`) and transaction (`:6543`) pooler host/user (the `aws-<n>-<region>` host can't be derived); password = AIDE-generated `db_pass` (reset via `PATCH /projects/{ref}/database/password` when linking a project whose password AIDE doesn't hold) |
  | Auth URLs | `PATCH /projects/{ref}/config/auth {site_url, uri_allow_list}` (comma-separated) |
  | Advisors | `GET /projects/{ref}/advisors/security` after each migration run |
  | Paused | `INACTIVE` → card shows "Paused"; Owner action calls `POST /projects/{ref}/restore` |

  Required scoped-token permissions: Projects RW, Database RW, Connection Pooling R, API Keys R, **API Key Secrets R**, Auth Config RW, Data API Config RW (Organization Projects R for the picker).
- **Render Postgres:** `POST /v1/postgres {name, ownerId, plan, version: "17", region (= the backend's), databaseName}`; poll `GET /v1/postgres/{id}` until available; `GET /v1/postgres/{id}/connection-info` → `internalConnectionString` for services in the same region and workspace (`externalConnectionString` only when the consumer is elsewhere). The preview database is a second instance named `<name>-preview`.
- **Railway Postgres:** a Postgres service with a volume in each environment (the production environment's service is copied into `aide-preview`; §11 check for the template/creation call shape), referenced by the app as `DATABASE_URL = ${{Postgres.DATABASE_URL}}` (private network).
- **Migration runner (Supabase BaaS only, D-V3-12):** AIDE's own Npgsql runner, over the **session pooler**, applies the pending `supabase/migrations/<version>_<name>.sql` files of a commit in version order, each in a transaction, and records `(version, name, statements)` in `supabase_migrations.schema_migrations`, the table the Supabase CLI uses, so a later `supabase db push` sees consistent history. It never edits or re-applies a recorded version; a recorded version whose file changed or disappeared is `MIGRATION_HISTORY_DIVERGED` (production: the run fails; preview: FR-129 reset). Runs are recorded (`database_migration_run`) with files, durations and errors. The Management API migrations endpoint isn't used (it assigns its own versions), nor the Beta query endpoint.
- **When migrations run:**
  - Preview: during preview rollout, before backend and frontend previews (FR-128).
  - Production, Supabase BaaS: in the release job **after merge readiness passes and immediately before AIDE merges** (`aide_merges`), or when the release enters `awaiting_merge` (`manual`), because platforms auto-deploy the default branch on merge. The `migration_backward_compatible` review check (§4.18) is what makes "schema before code" safe. If a PR is closed without merging after its migrations were applied, the release records "migrations applied, code not merged" and the next version must keep those files (they're on the branch).
  - Production, host mode: on the platform, as part of the backend's (or the serverless build's) deployment of the merge commit (FR-135).
  - **Unmerged migrations never run against a production database** (all modes).
- **SQL Server / External:** AIDE stores and syncs the connection string only; migrations run on the platform as in host mode.

### 4.22 Environment inventory and derived variables

| Variable | Applies to | Value | Environments |
| --- | --- | --- | --- |
| `DATABASE_URL` | backend (or the Next.js full-stack app) | the database's URI: Render `internalConnectionString`, Railway `${{Postgres.DATABASE_URL}}`, Supabase pooler (transaction `:6543` + `?pgbouncer=true`/`prepare=false` guidance for serverless hosts, session `:5432` otherwise), External value | per environment's database |
| `DIRECT_URL` | same, when the data pack's `migrate.needsDirectUrl` (Prisma, Drizzle on serverless) | Supabase session pooler `:5432`, or the same as `DATABASE_URL` | same |
| `AIDE_RUN_MIGRATIONS` | serverless hosts in host mode | `true` where that environment has its own database, else `false` | per environment |
| `<prefix>SUPABASE_URL`, `<prefix>SUPABASE_PUBLISHABLE_KEY` | frontend (public; prefix from the frontend pack: `NEXT_PUBLIC_`, `VITE_`; Angular reads `SUPABASE_URL`/`SUPABASE_PUBLISHABLE_KEY` at build into `assets/config.json`) | `https://<ref>.supabase.co`, `sb_publishable_…` | per environment's Supabase project |
| `SUPABASE_URL`, `SUPABASE_SECRET_KEY` | server code only (Next.js server, backends), sensitive, only when the spec needs admin actions (pack rule) | `sb_secret_…` | same |
| `API_ORIGIN` | frontend, origin model `proxy` | backend target URL | production / preview |
| `API_BASE_URL` (`VITE_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, Angular config) | frontend, `cross-origin` | backend target URL | production / preview |
| `BACKEND_URL` | `fe-nextjs` ui mode (server-side calls) | backend target URL | production / preview |
| `CORS_ALLOWED_ORIGINS` | backend, `cross-origin` | frontend production URL; preview URL pattern of the frontend host | production / preview |

Derived values are recomputed when a target URL or database changes, and synced (FR-126). Derived keys can't be overridden by user variables (`VALIDATION`); the Owner can rename the public prefix only by changing the frontend pack.

### 4.23 Preset migration *(replaces v2 §4.12 content)*

- Composition per preset: `nextjs-supabase` → `{frontend: fe-nextjs @ ".", backend: null, data: data-supabase (baas), vcs: <from git provider: github_app/github_pat → vcs-github, local → vcs-github>, deploy: {frontendHost: vercel if the project had Vercel settings else null, databaseHost: supabase (unlinked), local: none}, originModel: same-origin}`; `nextjs-frontend` → the same with `data: null` and no database host.
- Existing v2 env vars (`NEXT_PUBLIC_SUPABASE_URL`, `NEXT_PUBLIC_SUPABASE_ANON_KEY`, `SUPABASE_SERVICE_ROLE_KEY`) stay as `user` variables. Linking a Supabase connection later adds the derived publishable/secret variables, and the UI suggests deleting the legacy ones.
- v2 agent keys → v3 specialists (for plans locked before the upgrade, and history display):

  | v2 key | v3 key |
  | --- | --- |
  | `project-scaffolder` | `scaffolder` |
  | `db-engineer` | `data-engineer` |
  | `domain-logic-engineer`, `api-engineer`, `ui-engineer` | `frontend-engineer` (Next.js full-stack owns domain, route handlers and UI) |
  | `test-engineer` | `test-engineer` |
  | `devops-engineer` | `ci-engineer` for `.github/**`, else `deploy-engineer` (decided per step by its declared files; both → `deploy-engineer` with a warning) |
- Owned-path checks for migrated in-flight plans use the v2 preset's globs (kept as a frozen legacy mapping in the catalog under `legacy/`) until the plan is replaced, so a locked plan doesn't suddenly produce `AGENT_SCOPE` warnings.
- One EF migration performs the data conversion, with the backfill SQL reviewed in PROGRESS.md.

## 5. Data model

v1 §5 and v2 §5 apply. Changes (all snake_case, UUIDv7, UTC):

| Entity | Key fields | Constraints / indexes | Notes |
| --- | --- | --- | --- |
| release_version (changed) | + `stack_composition jsonb?`, `catalog_version?`, `catalog_hash?`; `preset_id/version/hash` kept (legacy, read-only) | | composition validated by the Domain on write |
| project (changed) | + `stack_packs text[]` (denormalised pack ids of the current version) | GIN index on `stack_packs` | list filter `?pack=`; replaces the preset filter |
| execution_step (changed) | `agent_key` values are v3 keys; + `legacy_agent_key?` | | |
| agent_run (changed) | + `packs jsonb` (`[{id, version, sections[]}]`), `catalog_version?`; `preset_*` kept | | |
| **git_host_instance** (new) | id, kind (`gitlab`/`bitbucket`), name, base_url (null for Bitbucket and gitlab.com), oauth_client_id, oauth_client_secret_encrypted, allow_insecure_http, enabled, version?, created_by, created_at | unique (kind, base_url) | env config overrides rows with the same base URL |
| **user_external_login** (new) | id, user_id, provider (`github`/`gitlab`/`bitbucket`), instance_id?, external_user_id, login, created_at | unique (provider, instance_id, external_user_id) | `users.github_id` is backfilled here (kept for v2 code paths until removed) |
| **git_connection** (new) | id, user_id, provider (`gitlab`/`bitbucket`), instance_id, auth_kind (`oauth`/`token`), external_user_id, login, access_token_encrypted, access_expires_at?, refresh_token_encrypted?, token_email_encrypted? (Bitbucket API token), scopes text[], expires_at?, status (`valid`/`invalid`/`insufficient_scope`), last_validated_at, updated_at | unique (user_id, provider, instance_id, external_user_id) | `github_connection` (v2) unchanged |
| project_git_settings (changed) | provider adds `gitlab`, `bitbucket`; + `instance_id?`, `git_connection_id?` (FK set null), `bound_by_user_id?`, `external_repo_id?`; `repo_full_name` holds `group/sub/project` or `workspace/slug` | check: gitlab/bitbucket need instance_id, external_repo_id, repo_full_name | |
| **platform_connection** (new, replaces `vercel_connection`) | id, user_id, provider (`vercel`/`netlify`/`cloudflare`/`render`/`railway`/`supabase`), label, secret_encrypted, last4, account_id?, account_name?, metadata jsonb (team/workspace/org ids, token policies), scopes text[], expires_at?, status (`valid`/`invalid`/`insufficient_scope`/`unchecked`), last_validated_at?, created_at | index (user_id, provider) | v2 rows copied with the same id, so FKs survive; `vercel_connection` dropped in the same migration |
| **deployment_target** (new, replaces `project_deployment_settings`) | id, project_id, component (`frontend`/`backend`), provider, connection_id? (FK set null), bound_by_user_id?, resource_id (Vercel project / Netlify site / Worker / Pages project / Render service / Railway project+service), preview_resource_id? (Render preview service / Railway environment / Workers preview trigger), name, root_directory, build_config jsonb, production_url?, preview_url?, status (`not_configured`/`configuring`/`ready`/`attention`/`error`), status_reason?, updated_at | unique (project_id, component) | v2 settings rows become `frontend` targets |
| **managed_database** (new) | id, project_id, environment (`production`/`preview`), provider (`supabase`/`render`/`railway`/`external`/`fake`), connection_id?, resource_id?, region?, plan?, status, connection_encrypted (URIs, keys, pooler config as JSON), db_password_encrypted?, expires_at?, created_at, updated_at | unique (project_id, environment) | |
| **database_migration_run** (new) | id, project_id, managed_database_id, release_id?, release_version_id, git_sha, environment, status (`running`/`succeeded`/`failed`), applied jsonb (`[{version, name, ms}]`), error_code?, error_message?, started_at, finished_at? | index (project_id, started_at) | Supabase BaaS runner only |
| deployment_env_var (changed) | + `applies_to text[]` (`frontend`/`backend`), `public bool`, `source` (`user`/`derived`), `derived_from?` | unique (project_id, key) unchanged | derived rows are written by AIDE only |
| deployment (changed) | + `target_id`, `component`, `environment` (replaces `target` preview/production naming; v2 values kept), `kind` (`deploy`/`rollback`/`promote`), `rollout_id?` | unique (release_id, component) where environment = production | |
| release (changed) | + `migration_run_id?`, `release_notes text` (always stored; the Bitbucket notes surface), `host_release_url?` (GitHub/GitLab Release) | | `github_release_url` becomes `host_release_url` (renamed in the migration) |

Migration notes:
- One migration per milestone (catalog/presets, GitLab+Bitbucket, platforms+targets, databases). No committed migration is edited.
- The presets migration and the `vercel_connection`/`project_deployment_settings` replacement are not rolling-update safe; the Recreate strategy applies (v2 precedent).

## 6. API

v1 §6 and v2 §6 apply. All new routes are under `/api`, with ProblemDetails `errorCode`.

**Breaking-change decision (D-V3-4):** the preset endpoints and fields are removed (`/api/presets`, `PATCH /projects/:id/preset`, `presetId` on create and `POST /versions`, the `?preset=` list filter); `/api/me/connections/vercel` becomes `/api/me/connections/platforms`; `/settings/deployment` changes shape to targets + databases; Vercel-specific error codes are replaced by provider-neutral ones (`VERCEL_GIT_NOT_CONNECTED` → `PLATFORM_GIT_NOT_CONNECTED`, `VERCEL_PLAN_LIMIT` → `PLATFORM_PLAN_LIMIT`, `VERCEL_ERROR` → `PLATFORM_ERROR`). The web app and CLI ship together (D-V2-11 precedent); record the `oasdiff` result.

| Method | Route | Body / query | Response | Errors (code → status) | Auth |
| --- | --- | --- | --- | --- | --- |
| GET | /api/stack/catalog | — | layers, options (chips, source, modes), recommendations, catalog version | — | user |
| POST | /api/stack/options | partial composition | per-layer options with `state` (`allowed`/`caveat`/`forbidden`/`invalidated`) and `reasons[]` | `VALIDATION → 400` | user |
| POST | /api/stack/validate | composition | normalised composition + derived (agents, sections, env names, origin model) | `STACK_INCOMPATIBLE → 400` | user |
| POST | /api/stack/detect | git {provider, instanceId?, repo, ref?} | suggested composition with evidence | `GIT_NOT_CONNECTED → 409`, `NOT_FOUND → 404` | user |
| GET / PUT | /api/projects/:id/stack | composition | composition (GET: member) | `STACK_INCOMPATIBLE → 400`, `STACK_LOCKED → 409`, `VCS_MISMATCH → 400` | owner |
| GET/POST/PATCH/DELETE | /api/admin/integrations/gitlab[/:instanceId] | name, baseUrl, clientId, clientSecret, allowInsecureHttp, enabled | instances (secrets as flags) | `GIT_HOST_NOT_ALLOWED → 400`, `UPSTREAM_ERROR → 502`, `CONFLICT → 409` | admin (browser session) |
| GET/PUT/DELETE | /api/admin/integrations/bitbucket | clientId, clientSecret | consumer status | `CONFLICT → 409` | admin (browser session) |
| GET | /api/auth/{gitlab,bitbucket}/start | instanceId?, mode (`signin`/`connect`), returnUrl | redirect | `NOT_FOUND → 404` | anonymous / user |
| GET | /api/auth/{gitlab,bitbucket}/callback | code, state | redirect | `GIT_AUTH_FAILED` redirect | — |
| GET/POST/DELETE | /api/me/connections/{gitlab,bitbucket}[/:id] · /:id/test | POST: token fallback {instanceId?, token, email?} | connections (login, instance, auth kind, scopes, expiry, status) | `CREDENTIAL_INVALID → 400`, `CONNECTION_SCOPE_INSUFFICIENT → 400` | user (browser session) |
| GET | /api/me/git/:provider/repos · /owners · /repos/:repoId/refs | connectionId, search, cursor | repos / namespaces, groups, workspaces+projects / tags, releases, default branch | `GIT_NOT_CONNECTED → 409` | user |
| POST | /api/me/git/:provider/repos | connectionId, owner, project? (Bitbucket), name, description, private | 201 repo | `REPO_NAME_TAKEN → 409`, `FORBIDDEN → 403`, `UPSTREAM_RATE_LIMITED → 429` | user |
| GET/POST/PATCH/DELETE | /api/me/connections/platforms[/:id] · /:id/test | provider, label, token, accountId? | connections (provider, account, scopes, expiry, status) | `CREDENTIAL_INVALID → 400`, `CONNECTION_SCOPE_INSUFFICIENT → 400` | user (browser session) |
| GET | /api/me/connections/platforms/:id/accounts · /resources | type? (sites, services, projects, workers, orgs, regions) | picker data | `UPSTREAM_ERROR → 502` | user |
| GET | /api/projects/:id/settings/deployment | — | targets, databases, env inventory, origin model, readiness | — | member |
| PUT / DELETE | /api/projects/:id/settings/deployment/targets/:component | provider, connectionId, create {name, region?, plan?} \| link {resourceId} | 202 target (setup job) | `STACK_INCOMPATIBLE → 400`, `CREDENTIAL_NOT_OWNER → 403`, `PLATFORM_GIT_NOT_CONNECTED → 409`, `PLATFORM_PLAN_LIMIT → 409`, `PLATFORM_PERMISSION_MISSING → 409`, `UPSTREAM_ERROR → 502` | owner |
| POST | /api/projects/:id/settings/deployment/targets/:component/verify | — | readiness checklist | — | owner |
| PUT / DELETE | /api/projects/:id/settings/deployment/databases/:environment | provider, connectionId, create {region, plan} \| link {resourceId} \| external {connectionString} | 202 database (provisioning job) | `STACK_INCOMPATIBLE → 400`, `PLATFORM_PLAN_LIMIT → 409`, `CREDENTIAL_NOT_OWNER → 403`, `UPSTREAM_ERROR → 502` | owner |
| POST | /api/projects/:id/settings/deployment/databases/preview/reset | — | 202 | `INVALID_STATE → 409` (rollout running) | owner |
| GET | /api/projects/:id/migrations · /migrations/:runId | releaseVersionId? | migration runs | `NOT_FOUND → 404` | member |
| GET/PUT/DELETE | /api/projects/:id/settings/deployment/env[/:key] (changed) | + appliesTo[], public | inventory (`hasValue`, `source`, `derivedFrom`) | `VALIDATION → 400` (derived key, secret-looking public value), `UPSTREAM_ERROR → 502` | owner |
| GET/POST | /api/projects/:id/deployments (changed) · /deployments/preview | component?, environment? / — | deployments / 202 preview rollout | `DEPLOYMENT_NOT_CONFIGURED → 409`, `PREVIEW_DATABASE_DIVERGED → 409` | member / writer |
| POST | /api/projects/:id/deployments/:deploymentId/rollback (renamed from /promote; /promote kept as alias for Vercel) · /redeploy | — | 202 deployment | `INVALID_DEPLOYMENT → 409`, `DEPLOYMENT_ACTION_UNSUPPORTED → 409` | owner |
| PATCH | /api/projects/:id/settings/git (changed) | + connectionId (rebind, GitLab/Bitbucket), mergeStrategy adds `fast_forward` (Bitbucket/GitLab ff projects) | section | `CREDENTIAL_NOT_OWNER → 403`, `GIT_NOT_CONNECTED → 409`, `FORBIDDEN → 403` | owner |
| POST | /api/projects (changed) | − presetId; + stack (composition); git {mode: github_app\|github_pat\|gitlab\|bitbucket\|local, instanceId?, connectionId?, repoId?, repoFullName? …} | 201 project | + `STACK_INCOMPATIBLE`, `VCS_MISMATCH`, `GIT_NOT_CONNECTED` | user |
| POST | /api/projects/:id/versions (changed) | − presetId; + stack? (omitted = keep the base version's, re-pinned to the current catalog) | 201 | + `STACK_INCOMPATIBLE` | owner |
| GET | /api/admin/stack | — | catalog with pack sources, versions, hash, load status | — | admin |

Notes:
- New run and job failure codes: `GIT_NOT_CONNECTED`, `MIGRATION_FAILED`, `MIGRATION_HISTORY_DIVERGED`, `DATABASE_PROVISIONING_FAILED`, `DATABASE_PROVISIONING_TIMEOUT`, `PLATFORM_ERROR`, `PLATFORM_RATE_LIMITED`, `GITLAB_RATE_LIMITED`, `BITBUCKET_RATE_LIMITED`, `STACK_CHANGE_UNPLANNED` (plan), `UNDECLARED_GENERATED_FILE` (plan warning).
- Every new error code gets frontend copy in the one error map (v1 rule 4).
- Connection and Admin integration mutations require a browser session (v2 rule).

## 7. Pages and UI

v2 §7 (design system, shell, routes) applies. Changes:

| Route | Page | Key content / states |
| --- | --- | --- |
| /projects/new | New project wizard *(amends)* | Source → Idea/Request → **Stack** (five sub-steps: Frontend, Backend, Data, VCS, Deployment; option cards with chips and recommended badge; "Not available with your choices (n)" collapsed list with reasons; caveat banners; live summary card with origin model and specialists) → **Repository** (connections of the chosen VCS: GitHub App/PAT, GitLab instance, Bitbucket; picker / create; Local) → Version → Configuration → **Deployment** (bind connections per target and database; create or link; readiness preview) → Review & create. Import: Source → Repository (any host) → Stack (pre-filled by detection, VCS locked, "detected" chips) → … |
| /projects/:id/settings/stack | Stack *(new; replaces the preset section)* | Current composition per layer with pack versions; "Change stack" (same sub-steps) while unlocked; locked state with the reason; stack-change warning for iterations; legacy preset note for migrated projects |
| /projects/:id/settings/git | Git *(amends)* | Host, instance, repo, mode; for GitLab/Bitbucket the binding ("connected by Ana") and Rebind; merge strategy options per host |
| /projects/:id/settings/deployment | Deployment *(replaces)* | Origin model card; target cards per component (provider logo, connection, resource link, URLs, status, readiness checklist with fix links); database cards per environment (status, region, plan, expiry/pause warnings, Reset preview); environment inventory table with source badges (user / derived / required-missing) and component and environment columns |
| /projects/:id/review | Review *(amends)* | Preview rollout panel: one row per component (and preview migration run) with state, URL, log tail, redeploy |
| /projects/:id/release | Release *(amends)* | Production rollout timeline (migration run → backend → frontend), host release link (GitHub/GitLab) or annotated tag with notes (Bitbucket) |
| /projects/:id/deployments | Deployments *(amends)* | Component and environment filters; rollback per provider capability (hidden with the reason when unsupported) |
| /projects/:id/migrations | Migrations *(new, under Project nav when a Supabase BaaS database exists)* | Runs with environment, SHA, files, duration, status, error |
| /connections | Connections *(amends)* | Sections: LLM providers · Source control (GitHub, GitLab per instance, Bitbucket) · Deployment platforms (Vercel, Netlify, Cloudflare, Render, Railway) · Databases (Supabase). Each connection dialog shows exactly what to create on the provider (token type, scopes/permissions, expiry advice) with a link to the provider's token page |
| /login | Auth *(amends)* | "Continue with GitHub / GitLab (per enabled instance) / Bitbucket" |
| /admin/integrations | Admin *(amends)* | GitHub (v2) · GitLab instances (add/edit/disable; callback URL and scopes to register; version and reachability) · Bitbucket consumer · **Stack catalog** (layers, packs, sources, versions, hash, load status; replaces the presets list) |

UI rules (in addition to v1/v2):
- Forbidden options are never selectable; their reason is always visible (no hover-only reasons, for touch and screen readers).
- The wizard never keeps its own compatibility logic; it renders `/api/stack/options` (CLAUDE.md rule 13).
- Provider logos are inline SVG components (no external image hosts; v1 CSP).
- Every target and database card shows which user's connection it uses ("connected by …").

## 8. Non-functional requirements

v1 §8 and v2 §8 apply, plus:
- **Security:**
  - All new secrets (GitLab/Bitbucket OAuth client secrets and user tokens, platform tokens, Supabase PAT, database passwords and connection strings, API keys) are encrypted with Data Protection (a purpose per secret kind), never returned, never logged, never in LLM prompts, and masked in build-log tails (v2 rule extended to every provider).
  - **SSRF:** only Admin-registered GitLab instances are configurable hosts. Base URLs must be `https` (unless `allow_insecure_http`); the resolved addresses are checked **on every connection** (DNS-rebinding guard) and loopback, link-local (169.254.0.0/16, fe80::/10), metadata (`fd00:ec2::254`) and `0.0.0.0` are refused (private ranges are allowed, since self-managed GitLab often lives there); cross-host redirects aren't followed; Git runs with `protocol.allow=never` and `protocol.https.allow=always` (plus `http` only for insecure instances); tokens are bound to the instance they were issued by and never sent to another host.
  - OAuth flows (GitLab, Bitbucket) use signed, single-use `state` (v2 manifest rule) and PKCE where supported (GitLab).
  - Deploy keys AIDE adds for Netlify are read-only (`can_push: false`), and the webhooks point only at `https://api.netlify.com/hooks/<provider>`.
  - Supabase secret keys, database URLs and passwords are never assigned to public variables (FR-126 rule); `<prefix>SUPABASE_PUBLISHABLE_KEY` is the only Supabase key a frontend gets.
- **Reliability:**
  - Setup, provisioning, rollout, migration and poll work are jobs in the v1 job table (outbox, leases, retries); a restart resumes them.
  - Per-provider rate budgets (§4.19, §4.20, §4.21) are enforced in one shared limiter keyed by connection; rate-limit waits never count as failed attempts.
  - Migration runs are idempotent by `(managed_database_id, version)`; a crashed run resumes from the last committed file.
- **Performance:** `POST /api/stack/options` p95 < 50 ms (pure, in-memory); projects list p95 < 300 ms unchanged with the `stack_packs` filter; deployment settings page p95 < 300 ms with two targets, two databases and 50 variables.
- **Accessibility:** the Stack step is keyboard navigable (option cards are radio groups with `aria-describedby` reasons; disabled options remain focusable for reading reasons); axe checks cover the Stack step, Settings → Stack, Settings → Deployment and Connections.
- **Testing:**
  - Every rule in §4.17 has unit tests, including all worked examples, plus an exhaustive test that every composition `/options` reports as allowed passes `/validate`, and vice versa (the composition space is enumerated: about 6 × 6 × 8 × 3 VCS (×2 GitLab kinds) × hosts).
  - Detection, skill resolution, ownership expansion, composed plan rules, spec sections, review checks (each rule id: a hit and a near miss), env derivation and preset migration have unit tests.
  - GitLab, Bitbucket, Netlify, Cloudflare, Render, Railway, GitHub Pages and Supabase are tested with stub HTTP handlers per operation, including 401, 403-scope, 429 with reset, transient statuses (GitLab `checking`, Bitbucket 202 merge task, 555) and provider state sequences. **No test calls a real provider API** (CLAUDE.md rule 11).
  - The migration runner is integration-tested against Testcontainers PostgreSQL 17 with a `supabase_migrations` schema: ordering, history rows, idempotent re-run, divergence detection.
  - E2E (Fake LLM + Local Git + Fake deployments and databases): create a **React + ASP.NET + EF Core** composition with Netlify/Render/Render Postgres hosts → preview rollout with both components → release → production rollout; a **Vue + Supabase BaaS** composition with a Fake migration run; the Stack step's filtering (pick ASP.NET, see Prisma disabled with its reason); a migrated v2 preset project releasing a version.

## 9. Local deployment

v1 §9 and v2 §9 apply, plus:
- **Optional secrets** (`secrets.env`): `GitLab__Instances__0__Name`, `__BaseUrl` (empty = gitlab.com), `__ClientId`, `__ClientSecret`; `Bitbucket__OAuth__ClientId`, `Bitbucket__OAuth__ClientSecret`. These override DB-registered entries with the same host.
- **Callbacks** use `App:PublicBaseUrl` (v2): `<base>/api/auth/gitlab/callback`, `<base>/api/auth/bitbucket/callback`. GitLab treats `http://localhost` redirect URIs as "development" but accepts them (§11 check).
- **Egress:** the API pod reaches `gitlab.com`, Admin-registered GitLab hosts, `api.bitbucket.org`, `bitbucket.org`, `api.netlify.com`, `api.cloudflare.com`, `api.render.com`, `backboard.railway.com`, `api.supabase.com` and Supabase pooler hosts (`*.pooler.supabase.com:5432`) for migrations (Docker Desktop default networking; no change).
- **Fake providers:** `Deployments:Provider=Fake` (v2) applies to every deployment provider, and `Databases:Provider=Fake` fakes provisioning and the migration runner (records files without a database). Both need `Deployments:AllowFake=true` outside Development/Testing (v2 rule). The E2E overlay (`deploy-local.sh --e2e`) sets both.
- **Seed (dev):**
  - The released demo project (v2) gets the composition `fe-react-vite` + `be-aspnet` + `data-efcore-postgres` + `vcs-github` with Fake hosts, so "Start new version" shows the stack.
  - The v2 preset demo project ("nextjs-frontend in planning") is migrated like any preset project (§4.23), demonstrating the retired-preset note.
  - A third demo project uses `fe-vue` + `data-supabase` (BaaS) with a Fake Supabase database in `specification`.

## 10. Decisions

v1 decisions D1–D21 and v2 decisions D-V2-1–D-V2-15 stand, except where amended below. v3 decisions:

- **D-V3-1 — Stack composition from stack-packs replaces presets.** Old (v2): two data-defined presets mirroring claude-kits, each a fixed bundle. New: one option per layer from a catalog adapted from `stack-packs` (5 frontends, 5 backends, 7 data packs, 3 VCS, 6 deploy targets), with compatibility rules from its `compose-stack` skill. Why: Ben's request; any supported combination works with the same agents; adding a stack is adding a pack folder. Presets are a subset of compositions, so no capability is lost.
- **D-V3-2 — Deployment is one layer with per-component hosts.** The stack-packs model allows split deploys (an SPA on Netlify with an API and Postgres on Render), and the compatibility of a host depends on the component it runs. So the Deployment step picks a frontend host, a backend host and a database host, plus the Compose dev loop (the `local-docker-desktop` pack, used as a skill only). Option order stays Frontend → Backend → Data → VCS → Deployment, as Ben asked.
- **D-V3-3 — Generic specialists + pack skills, mirroring stack-core.** Old: a per-preset agent catalogue with its own prompts (`db-engineer`, `ui-engineer`, `domain-logic-engineer`, …). New: stack-core's generic agents, each loading its layer's pack skill (or named sections). Domain logic moves to the backend engineer (or the frontend engineer for Next.js full-stack and BaaS apps), as in stack-core. `stack-reviewer`/`stack-auditor` map to AIDE's existing review agent. Content is adapted, not copied (§4.16), as in D-V2-12.
- **D-V3-4 — Presets removed with a data migration and a breaking API change.** Ben asked for the v2 presets to be removed in favour of the new implementation. Existing projects are converted to equivalent compositions, locked plans keep running through an agent-key map, and history keeps the preset labels. The preset endpoints and the Vercel-specific connection and error names go; the web app and CLI ship together (D-V2-11 precedent).
- **D-V3-5 — Still no build or generator runner.** Agents can't run `npm create`, `dotnet ef migrations add` or contract exports, and AIDE doesn't execute generated code (D-V2-9: a shell plus credentials is too much power; running `npm install` executes untrusted scripts on the user's machine). Generated files are hand-written by the owning specialist or produced/checked by CI, and CI results feed review on every Git host (FR-108). A sandboxed runner remains a candidate for v4 (§11).
- **D-V3-6 — GitLab through an Admin-registered OAuth application per instance, with PAT fallback; background work as the binding Owner.** GitLab has no App manifest or installation tokens, so each instance's OAuth app is registered by hand (callback and scopes shown in AIDE). OAuth access tokens last 2 h and refresh tokens rotate. `api` is unavoidable (MRs, merges, releases, project creation). Self-managed instances are allowed only from an Admin allowlist with SSRF checks.
- **D-V3-7 — Bitbucket Cloud through an OAuth consumer, with scoped API-token fallback; no app passwords; annotated tags instead of Releases.** App passwords were removed by Atlassian (creation disabled 2025-09-09, removal 2026-07-28). Bitbucket has no Releases API, so the release is an annotated tag whose message holds the notes, and AIDE's Release page holds the full notes. The 1,000 requests/hour/user budget drives a per-connection limiter and the single mergeability-checks call.
- **D-V3-8 — Pasted, scoped tokens for every deployment platform and Supabase.** OAuth would need each self-hoster to register an app with every provider (Supabase OAuth apps are per org and Beta; Railway OAuth needs an app; Render and Netlify have no suitable REST OAuth; Cloudflare's self-managed OAuth clients are new and undocumented for localhost; Vercel's OAuth REST permissions are still private beta). Tokens match the v2 Vercel pattern: validated, encrypted, never returned, scope-checked where the API allows.
- **D-V3-9 — A platform must build from the repo's Git host; no local builds or uploads.** Railway builds only from GitHub; Cloudflare doesn't build Bitbucket or self-managed GitLab; GitHub Pages needs GitHub; only Netlify builds from self-managed GitLab. Those combinations are incompatible (R9) rather than worked around with `wrangler deploy`, `railway up` or Docker image builds, which would mean AIDE executing the generated code (D-V3-5). Revisit with a sandboxed runner (§11).
- **D-V3-10 — One persistent preview environment per project, with its own preview database.** Old (v2): a Vercel preview per execution run, stateless. New: because AIDE develops one release version at a time on one branch, each project keeps one preview environment (Render `-preview` services, a Railway `aide-preview` environment, platform previews for frontends) with a preview database, rolled out in dependency order. Why: neither Render nor Railway lets an API create Git PR previews, their native PR previews copy the production database URL, and **migrations from unmerged code must never touch production**. Rollbacks that remove migrations require a preview database reset.
- **D-V3-11 — Platform APIs over repo config-as-code for resources.** Railway's `railway.json`/`railway.toml` stop working on 2026-12-01 and its IaC is applied only by the CLI; Render Blueprints can't be created or synced through the API. So AIDE creates and configures services, databases and variables through the APIs. Repo config files are used only where the platform reads them during its own build (`vercel.json`, `netlify.toml`, `_redirects`, `wrangler.jsonc`, the Pages workflow); `render.yaml` may be committed as documentation.
- **D-V3-12 — Supabase becomes a connection and managed database; BaaS migrations by AIDE's own Npgsql runner.** Replaces D-V2-10's "Supabase stays manual". Scoped PATs (GA 2026-10-06) give least privilege. Migrations go through the session pooler (the direct host is IPv6-only) and record CLI-compatible history in `supabase_migrations.schema_migrations`; the Management API's migrations endpoint assigns its own versions and the query endpoint is Beta, so neither is used; bundling the Supabase CLI isn't needed. New keys (`sb_publishable_`/`sb_secret_`) are used because Supabase retires `anon`/`service_role` by the end of 2026.
- **D-V3-13 — ORM migrations always run on the platform, gated per environment.** Host-mode ORM migrations run in the backend's pre-deploy command (Render paid, Railway), at container start on Render free (single instance), or in the production build step on serverless hosts when `AIDE_RUN_MIGRATIONS=true`. AIDE never needs .NET/Node/Python/Java toolchains for generated apps. The variable is set only where the environment has its own database, so previews without one never migrate production.
- **D-V3-14 — Supabase BaaS production migrations run immediately before the merge.** Platforms deploy the default branch automatically on merge, so applying the schema first avoids new code running against an old schema; the `migration_backward_compatible` check enforces expand/contract so the old code keeps working against the new schema. The edge case (PR closed after migrations applied) is recorded on the release.
- **D-V3-15 — Cloudflare defaults to Workers with static assets and Workers Builds; Pages only for existing projects.** Cloudflare's docs say "start new projects with Workers"; Workers Builds is the only researched platform with SHA-pinned API builds and promotes the exact previewed version. Workers Builds rejects account-owned tokens, so a user token is required.
- **D-V3-16 — Netlify with GitLab/Bitbucket is linked by AIDE with a read-only deploy key and a push webhook to Netlify.** Netlify's API supports it, AIDE already has write access to the repo, and it keeps Bitbucket/GitLab users off the GitHub-only path. It's a caveat in the wizard and the readiness checklist verifies the first build (§11 check).
- **D-V3-17 — GitHub Pages is a production-only frontend host using the GitHub App.** It needs only a Pages permission added to the App manifest. It has no previews or rollback, which the UI states.
- **D-V3-18 — Sign-in with GitLab and Bitbucket.** v1/v2 offered GitHub sign-in; once an instance or consumer is configured, the same OAuth app gives sign-in for free. Accounts link by provider identity (`user_external_login`), never silently by email.
- **D-V3-19 — Compatibility lives only in the Domain and is served to the UI.** One function powers the wizard, settings, API validation and CLI; the UI renders its output (`/api/stack/options`). This prevents drift between "what you can click" and "what the API accepts", which matters with ~10⁴ combinations.

## 11. Open questions

- [ ] Sandboxed build/generator runner (run pack generators, lockfile resolution, local `wrangler`/`railway up` builds in an isolated job without credentials)? It would remove the hand-written generated files and the R9 gaps. (Proposed: v4.) — Ben
- [ ] Supabase branching (Pro plan, ~$0.01344/branch-hour) as an alternative preview database for BaaS projects instead of a second Supabase project? (Proposed: later; v3 uses a linked preview project.) — Ben
- [ ] OAuth for Netlify, Cloudflare, Railway and Supabase as "bring your own client" options for teams that forbid long-lived tokens? (Proposed: later.) — Ben
- [ ] Vercel OAuth REST permissions are still private beta (Sign in with Vercel itself is GA since Nov 2025). Revisit when they're GA. (Carried from v2.) — Ben
- [ ] Abandon a version in development (carried from v2). (Proposed: still later.) — Ben
- [ ] Should AIDE ship more packs (e.g. a Svelte frontend, a Go backend, Fly.io) through stack-packs' `author-pack`, and should the catalog be refreshed from a newer stack-packs snapshot as a routine task? (Proposed: refresh per AIDE version.) — Ben
- [ ] Build-time checks against live accounts (not questions for Ben; record results in PROGRESS.md):
  - Netlify: does `POST /sites/{id}/builds?branch=aide/…` run when the branch isn't in `allowed_branches` or an `ignore` rule matches (else use the build hook)? The API field to turn off Deploy Previews; PAT expiry options; build-log access with a PAT.
  - Netlify GitLab/Bitbucket linking by deploy key + webhook builds reliably.
  - Cloudflare: bootstrapping the Worker (and its tag) before the first build; whether trigger branch filters apply to API-started builds; GitLab repo connections end to end via the API; Pages manual deployments of excluded branches.
  - Railway: `preDeployCommand` in `ServiceInstanceUpdateInput`; whether `commitSha` must be on the connected branch; whether a copied environment gets a fresh Postgres and volume; the Postgres creation call (`templateDeployV2` input or image + volume); the full `DeploymentStatus` enum.
  - Render: Postgres status values after `creating`; the service URL field; whether a service can be created on a private repo before the Git provider is connected.
  - Supabase: `supabase_migrations.schema_migrations` columns; whether pooler `connection_string` contains a placeholder; provisioning time; whether new projects still get legacy keys.
  - GitLab: `http://localhost` OAuth redirect URIs on gitlab.com; fast-forward merges leave `merge_commit_sha` null.
  - Bitbucket: `draft: false` via `PUT` marks a PR ready; refresh-token rotation; the consumer permission name for webhooks.
  - GitHub Pages: whether `POST /pages` with `build_type: workflow` needs Administration as well as Pages.

## 12. Milestones

**M0 Baseline (replaces the kit's scaffold phase):**
- The repo, CI, deploy scripts and the v2 app already exist. **Do not re-scaffold.**
- Move v2's `SPEC.md` and `PROGRESS.md` to `docs/v2/`, put this spec and `docs/v3/CLAUDE.md` at the root, and branch `build/aide-v3` from `main`. Run the full gate and `deploy-local.sh`, and record the baseline in a new root `PROGRESS.md`.

1. **M17 Stack catalog + compatibility:**
   - Catalog format and loader, adapted pack and agent content from `../stack-packs/` (the 27 layer packs in scope, i.e. all but `local-docker-desktop-k8s`, plus the stack-core skills and agents), `StackCompatibility` with §4.17 rules and tests, `/stack/catalog`, `/stack/options`, `/stack/validate`, Admin → Stack catalog, readiness check (FR-94, FR-97, FR-103).
2. **M18 Composition in projects + preset retirement:**
   - Composition on release versions, Settings → Stack, wizard Stack step, `STACK_LOCKED`, stack change, layer paths, detector and import pre-fill, preset migration and removal, seeds (FR-95, FR-96, FR-98–FR-100, FR-110).
3. **M19 Specialists across the lifecycle:**
   - Skill resolution, ownership, composed spec sections, plan rules, Guardian protected paths, review checklists and deterministic checks, generated-file policy, Fake scripted outputs per specialist (FR-101, FR-102, FR-104–FR-109).
4. **M20 GitLab:**
   - Instances (Admin, SSRF guard), OAuth + PAT connections, sign-in, picker and create, provider operations (push, MR, readiness, pipelines, merge, Releases, tags), import (FR-111–FR-113, FR-117–FR-122 GitLab parts).
5. **M21 Bitbucket:**
   - Consumer, OAuth + API-token connections, sign-in, picker and create with initial commit, provider operations (PR, mergeability checks, statuses, merge tasks, annotated tags), rate limiter, import (FR-114–FR-116, FR-117–FR-122 Bitbucket parts).
6. **M22 Platforms I: connections, targets, frontends:**
   - Generic platform connections (Vercel migrated), deployment targets model and migration, env inventory and derivation, origin model, readiness checklist, Netlify, Cloudflare (Workers Builds + Pages adopt), GitHub Pages, Vercel on the new model (FR-123–FR-127, FR-131, FR-132 frontend parts).
7. **M23 Platforms II: backends, databases, environments:**
   - Render, Railway, managed databases (Supabase, Render Postgres, Railway Postgres, External), Supabase BaaS migration runner and advisors, host-mode migration wiring, preview environment rollout, preview reset, production rollout (FR-125, FR-128–FR-135).
8. **M24 Integrate & harden:**
   - Events and notifications (FR-136), CLI (FR-137), E2E suites (§8), axe, perf, README and SPEC sync, spec-verifier over **v1, v2 and v3 requirements**.

## Revision notes

- 2026-10-18: v3 spec created. The v3 `CLAUDE.md` is drafted in `docs/v3/CLAUDE.md`. Research sources (fetched 2026-10-18, details in the sections above): stack-packs (`../stack-packs/`, all plugins at 1.0.0: `stack-core` skills `compose-stack`, `author-pack`, `orchestrate-stack`, `stack-api-contract` and its ten agents, plus every layer pack's `SKILL.md`); GitLab REST/OAuth docs (OAuth 2.0 + PKCE, tokens, merge requests and `detailed_merge_status`, approvals, protected branches, tags, releases, pipelines, rate limits); Bitbucket Cloud API 2.0 OpenAPI (repositories, refs/tags, pull requests, mergeability checks, merge tasks, statuses), API tokens, OAuth consumers, rate limits and the app-password removal timeline; Netlify OpenAPI (sites, builds, deploys, env, deploy keys, build hooks, restore) and docs (ignore builds, credits); Cloudflare Workers Builds, Workers scripts/versions/deployments, Pages and token docs; Render API (services, deploys, rollback, Postgres, logs, env vars, Blueprints, rate limits); Railway public GraphQL API (tokens, projects, services, environments, deployments, variables, pre-deploy, config-as-code deprecation, rate limits); Supabase Management API OpenAPI (projects, api-keys, pooler, migrations, auth config, branches, advisors), scoped PATs, API keys and the 2026 Data API exposure change; GitHub Pages REST; Vercel git configuration and Sign in with Vercel.
