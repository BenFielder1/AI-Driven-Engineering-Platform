# AIDE Platform — Specification (v4)

2026-10-25 · Ben Fielder · **v4**, built on the v3 codebase in this repo. Earlier versions: v1 [`docs/v1/SPEC.md`](docs/v1/SPEC.md), v2 [`docs/v2/SPEC.md`](docs/v2/SPEC.md), v3 [`docs/v3/SPEC.md`](docs/v3/SPEC.md). Stack: [`STACK.md`](STACK.md). Source: `docs/v4/DRAFT_SPEC.md` (drafted 2026-10-18 for a v3 that became the Next.js/Railway platform version) plus Ben's v4 brief (2026-10-25).

> **How this spec relates to v1–v3.** v4 is an increment on the running v3 app. **Everything in `docs/v1/SPEC.md`, `docs/v2/SPEC.md` and `docs/v3/SPEC.md` stays in force unless this document amends or replaces it.** That covers FR-1 to FR-112, the domain rules (v1 §4.1–4.9, v2 §4.10–4.15), the data models, APIs, NFRs and deployment sections. Amended items are listed in §3.0 and marked *(amends FR-n)* where they change. **The v2 tech stack presets (v2 FR-74–FR-77, §4.11, §4.12) are replaced** by stack composition (§3.1–§3.4). New requirements continue from **FR-113**, domain rules from **§4.16**, decisions use `D-V4-n`, and milestones continue from **M23**. Earlier tests stay green unless an amendment changes the behaviour they assert. In that case, update the test and name the amendment in it.
>
> **Paths.** Links and paths are relative to the repo root. `../stack-packs/stack-packs/plugins/` is Ben's stack-packs marketplace (read-only reference; all plugins at 1.0.0, read 2026-10-25).

## 1. Overview

AIDE turns an idea into released software through a governed six-phase lifecycle: **Refinement → Specification → Planning → Execution → Review → Release**. AI agents produce versioned artifacts, humans approve them at gates, and every state change is deterministic, logged and reversible. v2 added release versions, imports, per-user connections, two Next.js presets and Vercel. v3 moved AIDE's own frontend to Next.js and deployed AIDE to Railway.

**v4 makes AIDE stack-agnostic, and lets agents write long outputs:**
- **Stack composition replaces presets.** A user picks one option per layer, in this order: **Frontend → Backend → Data → VCS → Deployment**. Options that don't fit earlier choices are filtered out, with the reason shown. The catalog and its rules are adapted from stack-packs: 5 frontends, 5 backends, 7 data layers, 3 Git hosts and 6 deployment platforms.
- **Specialist agents per layer.** A fixed team of generic specialists (from stack-packs `stack-core`) each load the **pack skill** chosen for their layer. For example, the backend engineer loads `be-fastapi` for FastAPI and `be-aspnet` for ASP.NET.
- **More Git hosts:** GitLab (gitlab.com and allow-listed self-managed instances) and Bitbucket Cloud join GitHub.
- **More deployment platforms:** Netlify, Cloudflare, Render, Railway and GitHub Pages join Vercel. Hosts are chosen per component, with preview environments and production deployments.
- **Managed databases:** Supabase (BaaS and host mode), Render Postgres and Railway Postgres are provisioned or linked, wired into env vars, and migrated safely.
- **LLM choice at project creation, and long outputs.** The wizard picks the provider, connection and model. The Railway deployment hides Ollama and Fake. Each model's real output limit is used (up to 128,000 tokens on current Claude models), and cut-off answers get up to 5 continuations.

Success means:
- A user composes **React + Vite / ASP.NET Core / EF Core + PostgreSQL / gitlab.com / Netlify (frontend) + Render (backend and Postgres)**. Incompatible options (e.g. Vercel for the ASP.NET backend) can't be picked. The specialists build version 0.1.0 with the right pack skills. The review sees a Netlify preview talking to a Render preview service with its own preview database. The release merges the GitLab merge request, creates a GitLab Release, and deploys both components to production.
- A second user imports a Bitbucket repo that AIDE detects as **Vue + Supabase (BaaS)** and deploys it to Vercel. On release, AIDE applies the version's Supabase migrations to the linked Supabase project.
- Projects created on the v2 `nextjs-supabase` and `nextjs-frontend` presets keep working as compositions, with no action from their owners.
- On Railway production, a user creates a project, chooses Anthropic with their own key and `claude-opus-5-5` in the wizard, and never sees Ollama or Fake. A long spec is written in one 128,000-token call, without hitting the old 16,000 cap.

**In scope (v4):**
- Stack catalog and composition, compatibility rules, the wizard Stack step, stack settings, import detection (§3.1)
- Specialist agents and pack skills (§3.2), driving spec, plan, execution and review (§3.3)
- Retiring the v2 presets: data migration, then removal of preset code and endpoints (§3.4)
- GitLab (§3.5) and Bitbucket Cloud (§3.6), with provider-neutral Git lifecycle rules (§3.7)
- Platform connections: Netlify, Cloudflare, Render, Railway, Supabase, with Vercel moved onto the same model; GitHub Pages through the GitHub connection (§3.8)
- Deployment targets per component, env inventory, origin model, preview environment, production deployments, rollback (§3.9)
- Managed databases and migrations (§3.10)
- Events, notifications and CLI additions (§3.11)
- LLM providers: enablement per deployment, choice in the wizard, per-model output limits, streaming, continuations (§3.12)

**Out of scope (v4):**
- A build or generator runner. Agents still can't run commands, and AIDE doesn't build, install or execute generated code (D-V2-9, D-V4-5). CI on the Git host and the platforms' own builds do the verification.
- Deploying by local build, upload or CI-run platform CLI (`wrangler deploy`, `railway up`, `netlify deploy`, Docker image pushes). Where a platform can't build from a Git host, that combination is incompatible (D-V4-9), even though stack-packs documents CLI deploys from CI. Candidate for a later version (§11).
- The stack-packs `local-docker-desktop-k8s` pack as a target for generated apps. The Compose dev-loop pack (`local-docker-desktop`) is used only as a skill for the deploy engineer.
- Supabase branching, Netlify Database, Vercel Marketplace databases, Neon as a first-class host (it's usable as External), Cloudflare D1/Hyperdrive/Containers, and managed SQL Server.
- OAuth for Netlify, Cloudflare, Render, Railway and Supabase. Pasted, scoped tokens are used instead, as for Vercel (D-V4-8).
- GitHub Enterprise Server, Bitbucket Data Center, Azure DevOps.
- New stack packs beyond those in stack-packs today. Adding one is a catalog change (§4.16), not a feature.
- New LLM providers. Live model listing from provider APIs (the model catalog is configuration, §4.24).
- Changes to AIDE's own stack and hosting (v3 stays as built: Next.js web, ASP.NET API, Railway + Docker Desktop Kubernetes).

## 2. Users and permissions

The v1–v3 roles are unchanged. v4 adds:

| Role | Can (new in v4) | Cannot |
| --- | --- | --- |
| Admin | Add, edit, disable and remove **Git host instances**: gitlab.com and self-managed GitLab (allow-listed base URLs) with their OAuth application credentials, and the Bitbucket Cloud OAuth consumer. View the stack catalog and pack versions | Add a GitLab host that resolves to loopback, link-local or metadata addresses, or to private ranges when `GitLab:AllowPrivateNetworks=false` (§8). See any user's credentials. Enable an LLM provider the deployment disabled (§4.24) |
| User | Connect GitLab and Bitbucket accounts (OAuth or token fallback) and sign in with them. Connect Netlify, Cloudflare, Render, Railway and Supabase accounts (pasted tokens). Pick or create repos on any connected Git host. Choose the LLM provider, connection and model when creating a project | Use another user's connection directly |
| Project Owner | Compose or change the stack until the plan is locked. Bind their own Git host connection for GitLab/Bitbucket projects. Configure deployment targets and databases with their own platform connections. Reset the preview database. Promote and roll back production deployments | Bind another user's connection (`CREDENTIAL_NOT_OWNER → 403`). Change the stack after the version's implementation plan is locked (`STACK_LOCKED → 409`) |
| Project Contributor | Trigger preview deployments (v2). View the composition, targets, databases and migration runs | Change the stack, targets, databases or env vars, or reset the preview database (403) |
| Project Viewer | View the composition, targets, databases, deployments and migration runs | Any mutation (403) |

**Auth:** the v1 cookie auth, API tokens and antiforgery, and the v2 GitHub App sign-in, are unchanged.
- **"Continue with GitLab" and "Continue with Bitbucket"** are added to the sign-in page for each enabled Git host instance (FR-131, FR-134).
- Accounts are matched by `(provider, instance, external user id)`, and a signed-in user can link more Git identities from Account.
- Connections stay personal (v2 §2). GitLab and Bitbucket background Git work runs with the **binding Owner's** connection, because neither host has GitHub-style installation tokens (D-V4-6).

## 3. Functional requirements

Each FR names its layer in italics. Acceptance criteria follow the requirement, as "Given … when … then …".

### 3.0 Amended earlier requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| FR-2 (v1), FR-80 (v2) | Sign-in also offers GitLab and Bitbucket | FR-131, FR-134 |
| FR-5 (v1), FR-73 (v2) | The wizard's **Stack** step becomes five ordered layer sub-steps. The Repository step lists repos of the chosen VCS. Import may come from GitLab or Bitbucket and pre-fills the stack by detection. The Configuration step gains an **AI model** section | FR-114, FR-118, FR-141, FR-159 |
| FR-9 (v1) | Project settings gain **Stack**. Git settings gain the `gitlab` and `bitbucket` providers. Deployment settings become per-component targets plus databases | FR-117, FR-136, FR-144 |
| FR-22/23 (v1), FR-71 (v2) | The LLM call uses each model's output limit and streams. Continuations go up to 5 | FR-160–FR-162 |
| FR-28 (v1), FR-75 (v2) | Spec sections come from the composition's packs | FR-123 |
| FR-29/31 (v1), FR-76 (v2) | Plan steps use generic specialist keys, and validation rules come from the composition | FR-124, FR-125 |
| FR-35/36 (v1), FR-82 (v2) | Git providers gain GitLab and Bitbucket. Background work uses the binding Owner's connection for those hosts | FR-136, FR-137 |
| FR-42/43 (v1), FR-64/65 (v2) | Release creates a GitLab Release, or an annotated tag with the notes on Bitbucket. Merge strategies are mapped per host | FR-140 |
| FR-74–FR-77 (v2), §4.11–§4.12 | **Removed**, replaced by stack composition. Existing projects are migrated | FR-113–FR-129 |
| FR-78/79 (v2) | LLM providers can be disabled per deployment. The connections page gains Git host, platform and database sections | FR-142, FR-157 |
| FR-84/85 (v2) | Merge readiness and CI status for GitLab and Bitbucket | FR-138, FR-139 |
| FR-87–FR-91 (v2), §4.15 | Vercel moves onto the generic platform connection and deployment target model. Previews become a project **preview environment**. Production deploys every target in order | FR-142–FR-151 |
| FR-92/93 (v2) | New events, notifications and CLI commands | FR-155, FR-156 |
| D-V2-10 (v2) | "Supabase stays manual" is replaced: Supabase becomes a connection and a managed database | FR-152–FR-154, D-V4-12 |
| v3 §9 Railway variables, FR-108 | Railway also disables the Ollama and Fake LLM providers | FR-157 |

### 3.1 Stack catalog and composition

- **FR-113** *(backend + data)* The **stack catalog** is versioned data shipped in the repo (§4.16), adapted from stack-packs. It defines:
  - the five layers
  - their options: one per stack-packs pack, plus "None" where allowed
  - each option's chips, description and source pack version
  - the compatibility rules (§4.17) and the recommended combinations (from `compose-stack`)

  `GET /api/stack/catalog` returns it for the wizard, settings and Admin → Stack catalog. The catalog version and content hash are recorded on every release version and agent run.
  - Given the shipped catalog, when `GET /api/stack/catalog` is called, then it lists, each with its `source` pack and version:
    - 5 frontend options (`fe-react-vite`, `fe-nextjs`, `fe-angular`, `fe-vue`, `fe-vanilla`) plus None
    - 5 backends (`be-aspnet`, `be-spring-boot`, `be-fastapi`, `be-express`, `be-nestjs`) plus None
    - 7 data options plus None
    - 3 VCS options
    - 6 deployment platforms
- **FR-114** *(frontend + backend)* The new-project wizard's **Stack** step *(amends FR-5, replaces the v2 preset cards)* has five ordered sub-steps: **Frontend → Backend → Data → VCS → Deployment**.
  - **Option cards:** each sub-step shows cards with a name, chips, a one-line description and a recommended badge. Options come from `POST /api/stack/options` with the choices made so far (§4.17).
  - **Option states:** each option is one of:
    - `allowed`
    - `caveat`: allowed, with the ⚠ text shown on the card
    - `forbidden`: shown disabled in a collapsed "Not available with your choices (n)" list, each with its reason
  - A forbidden option can't be selected, and the API rejects it anyway (`STACK_INCOMPATIBLE`).
  - **Going back:** changing an earlier layer re-evaluates the later layers. A later choice that became forbidden is cleared, and a toast names what was cleared and why.
  - **Summary card:** shows the composition, the derived facts (Next.js mode, origin model, which specialists will work) and the caveats.
  - **"No stack (general)"** stays as a choice at the top of the step. It gives v1/v2 behaviour with the `generalist` agent (FR-120).
  - Given Frontend = React + Vite, when the user reaches Backend, then every backend is `allowed`.
  - Given Backend = ASP.NET Core, when the user reaches Data, then only these are selectable:
    - EF Core + PostgreSQL
    - EF Core + SQL Server, as a caveat ("no managed SQL Server on the catalog's platforms")
    - None

    Prisma shows the reason "Prisma works with Express, NestJS or Next.js full-stack".
  - Given the user changes Backend from FastAPI to ASP.NET after choosing SQLAlchemy, then Data is cleared and the toast says "SQLAlchemy was cleared: it needs FastAPI".
- **FR-115** *(frontend + backend)* The **Deployment** sub-step has up to four choices, each filtered by the earlier layers and by each other (§4.17):
  - **Frontend host** (when there's a frontend): Vercel, Netlify, Cloudflare, GitHub Pages, Render, Railway, or None.
  - **Backend host** (when there's a backend): Vercel, Render, Railway, or None. "Same as frontend" is preselected when that platform can run the backend.
  - **Database host** (when Data needs a server): Supabase, Render Postgres, Railway Postgres, External (a connection string the Owner enters, e.g. Neon or Azure SQL), or None (local only). Data = Supabase (BaaS) fixes it to Supabase.
  - **Local dev loop:** Docker Compose (the default when there's a backend or a database) or None. Compose loads the `local-docker-desktop` pack for the deploy engineer (D-V4-2).
  - **Origin model:** the derived model (`same-origin`, `proxy` or `cross-origin`, §4.17 R10) is shown with a one-line explanation. Where more than one model is allowed, the Owner can switch between them.
  - Given Frontend = React + Vite on Netlify and Backend = ASP.NET on Render:
    - then the database host options are Render Postgres, Supabase (host mode) and External
    - Railway Postgres is a `caveat`: "Database on a different platform from the backend: higher latency, public connection"
    - the origin model is `proxy`
- **FR-116** *(backend)* Compositions are validated on the server with the same rules.
  - `POST /api/stack/validate` returns the normalised composition with its derived fields (§4.18). Otherwise it returns `STACK_INCOMPATIBLE → 400` with `violations[] {layer, option, rule, reason}`.
  - Project create, `PUT /projects/:id/stack` and `POST /versions` with a stack run the same validation.
  - Given the composition `fe-vue` + `be-aspnet` + `data-prisma`, when it is posted, then `STACK_INCOMPATIBLE` with rule `R3` and reason "Prisma needs Express, NestJS or Next.js full-stack".
- **FR-117** *(backend + frontend + data)* The composition is pinned on the release version, with the catalog version and hash *(replaces FR-74's preset pinning)*.
  - **Editing:** an Owner can change it in **Settings → Stack** until the version's first implementation plan is locked. After that, `STACK_LOCKED → 409`. It can also be changed when starting a new version: v2 FR-60's dialog gains a "Change stack" link that opens the same five sub-steps, pre-filled.
  - **Stack change:** changing a layer of a release version whose base commit already has code (an iteration or import) is a stack change.
    - The spec agent is told which layers changed, and the spec must contain a "Stack Migration" section (FR-123).
    - The plan must contain at least one step owned by the new layer's specialist.
  - **VCS layer:** it can only change to the host of the project's repo (`VCS_MISMATCH → 400`), and is set automatically when the repo is chosen.
  - **Event:** `PRESET_CHANGED` is replaced by `STACK_CHANGED {from, to, layersChanged[]}`.
  - Given a locked implementation plan, when an Owner changes the backend, then `STACK_LOCKED`.
- **FR-118** *(backend + frontend)* **Import detection** *(amends FR-73)*: when a repo is chosen for import, AIDE runs the pure stack detector (§4.18 "Detection") over the repo's file list and manifests at the chosen base, and pre-fills the Stack step.
  - Detected options are marked "detected", with the evidence (e.g. "`next` 16.1 in `package.json`").
  - The VCS layer is fixed to the repo's host. Undetected layers are left empty for the user.
  - Layer **paths** are detected too (e.g. `apps/web`, `api/`).
  - Detection never blocks. The user can change any detected choice, and the Repository Analyst (v2 FR-70) still runs.
  - Given an imported repo with `frontend/package.json` (vue, vite), `supabase/config.toml` and `supabase/migrations/`, and no backend, then the suggestion is `fe-vue` at `frontend/`, Backend None, and `data-supabase` (BaaS).
- **FR-119** *(backend)* Each composition records the **path** of each layer in the repo (§4.18).
  - **Defaults** follow the stack-packs STACK template: `frontend/` and `backend/`. A Next.js full-stack app or a frontend-only repo may use the repo root `.`.
  - **Constraints:** paths are relative and normalised, may not escape the repo (`..`), and must differ between frontend and backend unless one of them is None.
  - Specialists' owned paths, plan rules and review checks are expanded from these paths.
  - Given a composition with `frontend: "."` and `fe-nextjs`, when the scaffolder's owned paths are listed, then they start at the repo root.

### 3.2 Specialist agents and pack skills

- **FR-120** *(backend)* AIDE has one catalogue of **generic specialists**, adapted from the stack-packs `stack-core` agents (§4.18). The agents in play for a project are those whose layer exists in its composition:

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

  `stack-reviewer` and `stack-auditor` map to AIDE's existing Code Review agent and review cycle. They aren't plan agents. Per-agent model overrides (v2 `ModelsByAgent`) apply to the plan agents as a group (`codeGenerator`), unchanged.
  - Given a composition with no backend and `data-supabase` (BaaS), then the plan generator's catalogue has scaffolder, data-engineer, frontend-engineer, test-engineer, ci-engineer, deploy-engineer and docs-writer, and no backend-engineer.
- **FR-121** *(backend)* Each specialist **loads the pack skills for its layer**, plus shared core skills: the full adapted skill, or named sections of it, as listed in §4.18 "Skill resolution".
  - **Prompt order:**
    1. the base `code_generator.md`
    2. the composition summary: the STACK.md equivalent (packs, paths, origin model, env names, gates)
    3. the agent's own prompt
    4. each resolved skill, as `## Skill: <pack> — <section>`
    5. the step input
  - Each agent run records `agent_key`, the composition id, and the `packs[] {id, version, sections[]}` it loaded.
  - Given a composition with `be-fastapi` and `data-sqlalchemy`, when a `backend-engineer` step runs, then:
    - its prompt contains the adapted `be-fastapi` skill, `stack-api-contract`, and the `data-sqlalchemy` `Setup` and `Modelling` sections
    - the run records `packs: [be-fastapi@1.0.0+aide.1, stack-api-contract@1.0.0+aide.1, data-sqlalchemy@1.0.0+aide.1]`
  - Given the same project switched to `be-aspnet` before planning, when the next `backend-engineer` step runs, then it loads `be-aspnet` instead.
- **FR-122** *(backend)* Pack and agent content is **adapted, not copied**, from stack-packs (§4.16 "Adaptation rules"), in the same spirit as v2's claude-kits adaptation.
  - Every pack file records its source plugin and version.
  - A catalog that references a missing file, an unknown section heading or an unknown pack fails to load at startup. The readiness check then fails (`stack-catalog` unhealthy, replacing `presets`).
  - A unit test asserts that the shipped catalog loads.
  - Given a pack whose agent mapping names a section "Gates" that the adapted skill doesn't have (it has "Gate"), then catalog loading fails, naming that pack and section.

### 3.3 Spec, plan, execution and review

- **FR-123** *(backend)* The **Spec Generator** *(replaces FR-75's preset input)* gets the composition summary, plus the union of the selected packs' required spec sections with their guidance (§4.18 "Spec sections").
  - Spec validation requires those sections. The v2 rules on requirement IDs, acceptance criteria and iteration tags are unchanged.
  - A stack change (FR-117) adds the required section "Stack Migration": what moves, data migration, compatibility, rollout.
  - Given a composition with a backend and a cross-origin deployment, when the spec lacks "API Contract & Error Codes" or "Origin Model (CORS, Cookies, Auth)", then the run gets the one corrective retry, naming the missing sections.
- **FR-124** *(backend)* The **Plan Generator** *(replaces FR-76's preset input)* gets the composition's specialists (key, name, description, owned paths) and the composed plan rules (§4.18 "Plan rules"). Validation is v2 §4.11's, with the rules generated from the composition instead of a preset file:
  - unknown or missing `agent` → `INVALID_PLAN`
  - files outside the agent's owned paths → `AGENT_SCOPE` warning
  - order and exclusivity rules → errors (soft rules → `PLAN_ORDER` warning, per the v2 decision)
  - uncovered new or changed requirements → `UNCOVERED_REQUIREMENTS` warning
  - a stack change without a step for the new layer's specialist → `STACK_CHANGE_UNPLANNED` error
  - Given a composition with `data-efcore-postgres`, when a plan has a `backend-engineer` step creating a file under the backend's `Migrations/` folder, then `INVALID_PLAN` names the exclusivity rule "migrations belong to data-engineer".
- **FR-125** *(backend)* During **execution**, each step runs as its specialist with its resolved skills (FR-121).
  - The Guardian's protected paths are the union of the v1 rules and the packs' protected paths (§4.18), with the v2 "owner may declare" exception.
  - **Migrations that exist on the base commit are immutable for every data pack**, not only Supabase.
  - Given a composition with `data-jpa-flyway`, when a step modifies `backend/src/main/resources/db/migration/V2__orders.sql` and that file exists on the base commit, then the Guardian rejects the step with a protected-path violation.
- **FR-126** *(backend)* The **Code Review** agent *(replaces FR-77)* scores against the composed checklist: each selected pack's `Checklist` items, mapped to a v1 category and severity in the pack file.
  - The deterministic checks are those registered by the selected packs (§4.18 "Review checks").
  - Checklist findings are agent violations without penalties, as in v2.
  - Given `fe-react-vite` and a file that reads `import.meta.env.VITE_STRIPE_SECRET_KEY`, when review runs, then a critical security finding "secret-like value exposed to the browser" applies −0.20 to security.
  - Given `data-efcore-postgres` and `EnsureCreated()` in non-test code, then a major `architecture` finding applies −0.10.
- **FR-127** *(backend)* **Generated files.** There's no build runner (D-V4-5), so agents can't run generators. The composition's packs declare how each generated file is handled (§4.18 "Generated files"):
  - **Hand-written in the generator's shape** by the owning specialist, when small and deterministic. Examples: the OpenAPI document for changed endpoints, the generated client types, Supabase `database.types.ts`, EF Core migration and snapshot files.
  - **Produced or checked by CI**, composed by the `ci-engineer`. Examples: freshness checks such as `git diff --exit-code backend/openapi/api.json`, `dotnet ef migrations has-pending-model-changes`, `prisma migrate diff --exit-code`.
  - **Absent until the first CI run** (lockfiles). When no lockfile exists, CI uses `npm install`, `uv sync` without `--frozen`, and so on (v2 decision).
  - CI results on the PR head are review evidence on every Git host (FR-139). A failed freshness check becomes a finding that the optimisation plan must fix.
  - Given a backend step that adds an endpoint, when the plan is validated, then the step (or a later step) must declare `backend/openapi/api.json`. Otherwise an `UNDECLARED_GENERATED_FILE` warning names it.
- **FR-128** *(backend)* The **Repository Analyst** and **refinement** *(amends FR-70/72)* get the composition summary and, for imports, the detector's evidence.
  - The codebase summary's "Stack & Dependencies" section must name the detected packs and any mismatch with the composition.
  - Given an import detected as `fe-vue` whose Owner chose `fe-react-vite`, then the summary flags the mismatch and the spec must contain "Stack Migration".

### 3.4 Retiring the v2 presets

- **FR-129** *(backend + frontend + data)* The v2 presets `nextjs-supabase` and `nextjs-frontend` are removed. That means:
  - the `Agents/Presets/` folder, `Aide.Domain/Presets` loader and rules, and `PresetCatalog`
  - `GET /api/presets[/:id]` and `PATCH /api/projects/:id/preset`
  - `frontend/src/features/presets/`, the wizard's preset cards and the projects-list preset filter
  - the `presets` health check

  One EF migration converts existing data (§4.23):
  - Every release version pinned to a preset gets the equivalent composition:
    - `nextjs-supabase` → `fe-nextjs` at `.` + no backend + `data-supabase` BaaS + VCS from the Git provider + Vercel frontend host + Supabase database host *unlinked*
    - `nextjs-frontend` → the same, without data
  - The preset id, version and hash stay on the release version and agent runs as history, shown as "Preset nextjs-supabase@1.0.1 (retired, migrated)".
  - In-flight plans keep their v2 agent keys, mapped at run time (§4.23 table), so a locked plan still executes.
  - The dev seed's preset demo project becomes a composition demo (§9).
  - Given a v2 project in `execution` on `nextjs-supabase` whose step 4 is `db-engineer`, when step 4 runs after the upgrade, then it runs as `data-engineer` with the `data-supabase` skill, and the run records `legacyAgentKey: db-engineer`.
  - Given any client calling `GET /api/presets`, then `404` (breaking change accepted in D-V4-4).

### 3.5 GitLab

- **FR-130** *(backend + frontend + data)* An Admin manages **GitLab instances** in **Admin → Integrations → GitLab**.
  - **Instances:** `gitlab.com` is offered by default. Self-managed instances are added by base URL: `https://` only, unless "allow insecure HTTP" is ticked for a lab host.
  - **Stored per instance:** name, base URL, and the **OAuth application** credentials (client id, and the client secret, encrypted).
  - **Registration help:** GitLab has no manifest flow, so the page shows the exact callback URL (`<public base URL>/api/auth/gitlab/callback`) and the scopes (§4.19) to register.
  - **Checks:** adding or changing a base URL runs the SSRF guard (§8) and a reachability check. `GET /api/v4/version` must answer with GitLab's JSON shape, and an unauthenticated 401 counts as reachable.
  - **Config:** instances can also be configured from `secrets.env` or Railway variables (`GitLab__Instances__<n>__*`), which take precedence.
  - **Disable or remove:** a disabled instance stops existing connections working, and projects show "GitLab instance disabled". An instance can be removed only when no project uses it (`CONFLICT → 409` otherwise).
  - Given an Admin adds `https://169.254.169.254`, then `GIT_HOST_NOT_ALLOWED → 400`.
  - Given `https://gitlab.internal.example` resolving to `10.0.4.7`, then it is accepted on Docker Desktop Kubernetes (`GitLab:AllowPrivateNetworks=true`), and refused on Railway (`false`, §8).
- **FR-131** *(backend + frontend + data)* In **Account → Connections → GitLab**, a user connects GitLab per instance.
  - **OAuth:** authorization code + PKCE S256, scopes `api write_repository openid profile email`. Access tokens last 2 h. AIDE refreshes them before expiry and **stores the rotated refresh token atomically**, because GitLab invalidates the old pair on refresh.
  - **Token fallback:** a personal, project or group access token with `api` and `write_repository`. AIDE validates it with `GET /api/v4/user` and `GET /api/v4/personal_access_tokens/self` (scopes, expiry), and warns 14 days before expiry.
  - **Sign-in:** "Continue with GitLab" on the sign-in page uses the same OAuth app. Users are matched by `(gitlab, instance, user id)`.
  - **Invalid connections:** a connection that GitLab rejects (401 after one refresh) is marked `invalid`, and its owner is notified (v2 `connection_invalid`).
  - Given a user signs in with GitLab on gitlab.com for the first time, then an AIDE account is created (or linked by verified email, only when the user confirms) and a GitLab connection exists.
- **FR-132** *(backend + frontend)* With a GitLab connection:
  - **Repo picker:** lists the projects the user can develop in (`GET /projects?membership=true&min_access_level=30&simple=true&search=`, keyset pagination).
  - **Create project:**
    - namespace: the user's personal namespace, or a group whose `project_creation_level` allows the user (`GET /groups?min_access_level=30`)
    - name, path, description, visibility, and `initialize_with_readme=true`, so `main` exists and `default_branch` is set
  - **Errors:** name or path taken → `REPO_NAME_TAKEN → 409`. Not allowed in the group → `FORBIDDEN → 403`. The GitLab.com project-creation limit → `UPSTREAM_RATE_LIMITED → 429`.
  - Given a connected user, when they create `acme/todo-app` (private) in group `acme`, then the project exists with `main` and a README, and the wizard selects it.

### 3.6 Bitbucket Cloud

- **FR-133** *(backend + frontend + data)* An Admin configures the **Bitbucket OAuth consumer** in **Admin → Integrations → Bitbucket**.
  - **Stored:** the client key and secret (encrypted).
  - **Registration help:** Bitbucket fixes scopes on the consumer, so the page lists the callback URL and the consumer permissions to tick: `account`, `email`, `repository:write`, `repository:admin`, `pullrequest:write`, `project`, `webhook` (§4.19).
  - **Config:** it can also come from `secrets.env` or Railway variables (`Bitbucket__OAuth__*`).
  - App passwords are **not supported** (Atlassian has removed them).
- **FR-134** *(backend + frontend + data)* In **Account → Connections → Bitbucket**, a user connects Bitbucket.
  - **OAuth:** authorization code. Access tokens last 2 h. AIDE refreshes them and always persists whatever refresh token comes back.
  - **Token fallback:** an Atlassian **API token with scopes**, plus the Atlassian account email.
    - Required scopes, all `:bitbucket`: `read:user`, `read:workspace`, `read:project`, `read:repository`, `write:repository`, `admin:repository`, `read:pullrequest`, `write:pullrequest`, `read:pipeline`.
    - AIDE validates with `GET /2.0/user` and checks each scope with a harmless call. Missing scopes are listed (`CONNECTION_SCOPE_INSUFFICIENT → 400`).
  - **Sign-in:** "Continue with Bitbucket" signs in through the consumer (identity from `GET /2.0/user`, email from `GET /2.0/user/emails`).
  - Given a user saves an API token without `write:pullrequest`, then `CONNECTION_SCOPE_INSUFFICIENT` lists it and nothing is saved.
- **FR-135** *(backend + frontend)* With a Bitbucket connection:
  - **Repo picker:** lists repos across the user's workspaces where they have write access (`GET /user/workspaces`, then `GET /repositories/{workspace}?role=contributor&q=name~"…"`).
  - **Create repository:**
    - workspace (from `GET /user/workspaces`), **project** (required by Bitbucket; picker from `GET /workspaces/{ws}/projects`), name, description, private
    - AIDE calls `POST /repositories/{ws}/{slug}` with `scm: git`, then pushes an initial commit with a README on `main`, so the default branch exists (Bitbucket has no auto-init)
  - **Errors:** slug taken → `REPO_NAME_TAKEN → 409`. Not permitted → `FORBIDDEN → 403`.
  - Given a user creates `acme/todo-app` in project `TODO`, then the repo exists with `main` holding a README, and the wizard selects it.

### 3.7 Git hosts in the lifecycle

- **FR-136** *(backend + data)* The Git provider set *(amends FR-82)* is `github_app`, `github_pat`, `gitlab`, `bitbucket` and `local`.
  - A GitLab or Bitbucket project stores the instance, the external repo id and full path, and the **binding Owner's connection** (`git_connection_id`).
  - **Background Git work** (clone, fetch, push, MR/PR, merge, tag, release, polling) uses that connection's token, refreshed per job and passed as an `http.extraHeader` Basic header:
    - GitLab: `oauth2:<token>`
    - Bitbucket OAuth: `x-token-auth:<token>`
    - Bitbucket API tokens: `x-bitbucket-api-token-auth:<token>`

    Tokens are never written to disk, put in a remote URL, logged or given to an LLM (v2 §4.14).
  - **Lost binding:** if the binding Owner loses the Owner role, leaves, or deletes or invalidates the connection:
    - the binding is cleared
    - Git jobs fail with `GIT_NOT_CONNECTED`
    - Owners are notified (`git_binding_cleared`)
    - another Owner re-binds their own connection in Settings → Git, after AIDE checks it has developer/write access to the repo
  - Branches stay `aide/<slug>/v<version>` on every host.
  - Given a GitLab project whose binding Owner leaves, when a push is due, then the job fails with `GIT_NOT_CONNECTED` and the remaining Owners are notified.
- **FR-137** *(backend)* **Merge requests and pull requests** on the new hosts follow FR-36/FR-65 semantics (§4.19 mapping):
  - **GitLab:** one MR per release version, opened as draft (`Draft:` title prefix) and marked ready by removing the prefix.
  - **Bitbucket:** one PR with `draft: true`, marked ready with `draft: false`.
  - The body links back to the AIDE project, as on GitHub.
  - Given a Bitbucket project whose execution run completes, then a draft PR from `aide/<slug>/v0.1.0` to `main` exists, and the Release page links it.
- **FR-138** *(backend)* **Merge readiness** *(amends FR-84)*:
  - **GitLab:** reads the MR's `detailed_merge_status`, the approvals endpoint, the protected branch merge levels and the head pipeline. The status is re-polled while it is `unchecked`, `checking`, `preparing` or `approvals_syncing`.
  - **Bitbucket:** reads `GET …/pullrequests/{id}/mergeability/checks`. Ready means no check has `blocking: true`.
  - Reasons map to the v2 reason codes: `review_required`, `checks_failing`, `checks_pending`, `conflicts`, `insufficient_permission`, `draft`, `discussions_unresolved`, `rebase_required`.
  - Given a GitLab MR with `detailed_merge_status = not_approved`, when readiness is checked, then `canMerge: false` with reason `review_required`.
  - Given a Bitbucket PR whose `standard_merge_check` for "require passing builds" is blocking and failed, then reason `checks_failing`.
- **FR-139** *(backend)* **CI status** *(amends FR-85)* is a deterministic review input on every host.
  - **Sources:** GitLab head pipeline and commit statuses (`GET …/repository/commits/:sha/statuses`). Bitbucket commit statuses (`GET …/commit/{sha}/statuses`).
  - **Severity:** a failed status that the host requires for merge is a major `testing` finding (GitLab "pipelines must succeed"; Bitbucket `require_passing_builds_to_merge`, as seen in the mergeability checks). Other failures are minor.
  - Pending statuses are waited for up to 15 minutes (v2 rule).
  - Given a GitLab pipeline whose `test:backend` job fails, on a project requiring pipelines to succeed, then a major finding "required pipeline job test:backend failed", with the job URL, applies −0.10 to testing.
- **FR-140** *(backend)* **Release** *(amends FR-64/65)*:
  - **GitLab merge:** `PUT …/merge` with:
    - `sha`: the reviewed head (409 = the head moved, so re-review is required)
    - `squash` per the project's merge strategy
    - `should_remove_source_branch: false`

    The merged commit is `merge_commit_sha`, else `squash_commit_sha`, else the head `sha` (fast-forward).
  - **GitLab release:** `POST /projects/:id/releases` with `tag_name`, `ref` = the merged commit, `tag_message` (annotated tag), and the AIDE release notes as `description`.
  - **Bitbucket merge:** `POST …/pullrequests/{id}/merge` with `merge_strategy` from the project setting (`merge_commit`, `squash` or `fast_forward`). A `202` is followed through `merge/task-status`, and a `555` is retried.
  - **Bitbucket release:** an **annotated tag**, `POST …/refs/tags {name, target.hash, message}`, whose message holds the release notes. Bitbucket has no Releases (D-V4-7). The full notes are kept on the AIDE release and shown on the Release page.
  - **`manual` strategy:** the `manual` merge strategy and **awaiting merge** work as in v2 FR-65. AIDE marks the MR/PR ready and polls (§4.19 cadence) until it is merged (the tag and release then complete) or closed (`PR_CLOSED`).
  - **Errors:**
    - GitLab 405 or Bitbucket 400 (not mergeable) → `awaiting_merge`, with readiness reasons.
    - The GitLab tag-creation limit → retried at the reset.
  - Given a GitLab project with `aide_merges`, when the release job succeeds, then the MR is merged, a GitLab Release `v0.1.0` exists on the merged commit, and the badge reads "Released 0.1.0".
  - Given a Bitbucket project, when the release completes, then the annotated tag `v0.1.0` exists on `merge_commit.hash`, with the notes as its message.
- **FR-141** *(backend + frontend)* **Import** *(amends FR-73)* works from GitLab and Bitbucket.
  - **Released-version picker:** lists GitLab Releases and semver tags (`GET …/releases`, `GET …/repository/tags`), or Bitbucket semver tags (`GET …/refs/tags`).
  - **Default branch:** from `default_branch` (GitLab) or `mainbranch.name` (Bitbucket).
  - The stack is detected (FR-118).
  - Given a GitLab project with tags `v1.0.0` and `v1.1.0`, when it is imported, then `v1.1.0` is preselected and the version to develop defaults to `1.2.0`.

### 3.8 Platform connections

- **FR-142** *(backend + frontend + data)* **Account → Connections** *(amends FR-87)* has four sections:
  - **LLM providers** (v2; only the providers enabled on this deployment, FR-157)
  - **Source control:** GitHub, GitLab per instance, Bitbucket
  - **Deployment platforms:** Vercel, Netlify, Cloudflare, Render, Railway
  - **Databases:** Supabase

  How platform connections work:
  - A platform connection is a pasted token with a label. §4.20 lists, per provider, what to create, the scopes, and how it's validated.
  - AIDE validates the token on save and stores it encrypted, and never returns it. It shows the account, team, workspace or org the token reaches, its scope, expiry and status.
  - Re-test, rename and delete work as for v2 Vercel connections.
  - Existing v2 Vercel connections migrate unchanged into the generic model (§5).
  - **Rejected tokens:** a token the platform rejects (401, or the provider's invalid-token 403) marks the connection `invalid` and notifies its owner.
  - **Missing permissions:** a 403 for missing permissions marks it `insufficient_scope`, with the missing permission names where the API returns them (Supabase `missing_permissions`, Cloudflare token policies).
  - Given a Cloudflare **account-owned** token (`cfat_` prefix), when it is saved, then `CONNECTION_SCOPE_INSUFFICIENT` explains that Workers Builds needs a user token.
  - Given a Supabase scoped token without "API Key Secrets: Read", then the connection saves with status `insufficient_scope`, and the database setup page names the missing permission.
- **FR-143** *(backend + frontend)* **GitHub Pages** needs no extra connection: it uses the project's GitHub App installation (or PAT).
  - The AIDE GitHub App manifest adds **Pages: read & write** *(amends v2 §4.14)*.
  - Existing App registrations show "Pages permission missing: update the App's permissions in GitHub, then accept them on each installation" until the installation token reports the permission.
  - Given an App without the Pages permission, when an Owner selects GitHub Pages as the frontend host, then `PLATFORM_PERMISSION_MISSING → 409`, with the App settings link.

### 3.9 Deployment targets and environments

- **FR-144** *(backend + frontend + data)* **Settings → Deployment** *(replaces v2 FR-88's single Vercel setting)* shows:
  - one **target** card per component in the composition (frontend, backend)
  - one **database** card per environment (production, preview)
  - the environment inventory (FR-145) and the origin model

  For each target, an Owner:
  - binds one of their own connections for the host chosen in the composition
  - **creates** the platform resource linked to the repo, or **links** an existing one (picker). The build configuration is derived from the composition (§4.20): root directory, build command, output directory, start command, Dockerfile, health check path, pre-deploy migrate command.
  - sees a **readiness checklist** from `POST …/targets/:component/verify`:
    - the connection is valid
    - the platform's Git app or credentials are connected to the repo's host and repo
    - the required env vars are present
    - auto-deploys for `aide/**` are disabled
    - the preview environment is ready
    - plan limits (§4.20)

  Other rules:
  - The resource is created by a `deployment_setup` job, since it may take several API calls and polls. The card shows its progress.
  - **Errors:**
    - The platform's Git integration lacks access → `PLATFORM_GIT_NOT_CONNECTED → 409`, with the platform's install or connect link.
    - A plan restriction (e.g. Vercel Hobby with an org repo, or a Netlify private org repo without Pro) → `PLATFORM_PLAN_LIMIT → 409`.
    - The composition doesn't allow this host → `STACK_INCOMPATIBLE`.
  - Local Git projects may use targets only with the Fake provider (v2 decision kept).
  - Given an ASP.NET backend on Render, when the Owner creates the target, then AIDE creates a Docker web service with:
    - `dockerfilePath: backend/Dockerfile`
    - `healthCheckPath: /api/health/ready`
    - auto-deploy from `main` for production (§4.20)
    - a `preDeployCommand` running the EF migration bundle

    It also creates a `-preview` service with `autoDeployTrigger: "off"` (FR-147).
- **FR-145** *(backend + frontend + data)* The **environment inventory** *(amends FR-88 env vars)* lists, per variable:
  - key
  - the components it applies to (frontend, backend) and its environments (preview, production)
  - a sensitive flag, and a public flag (`NEXT_PUBLIC_`/`VITE_`)
  - its **source**:
    - `user`: entered by an Owner (the value is encrypted and never returned)
    - `derived`: written by AIDE from the databases, Supabase project and origin model (§4.22). Read-only, shown with its origin ("from Render Postgres (production)").
    - `required`: declared by the packs and missing (flagged)

  Other rules:
  - **Sync:** AIDE syncs the inventory to each target on save and before each deployment. Each provider has its own mechanics (§4.20), e.g. Netlify contexts `production`/`deploy-preview`/`branch-deploy`, Railway per environment with `skipDeploys: true`, Render per service with a full merged `PUT`. Sync failures keep the local value and show `syncError` (v2 rule).
  - **Public secrets refused:** a **public** variable whose key or value looks secret (§4.18 secret patterns) is refused (`VALIDATION`, "secret values can't be public").
  - Given a Vue + Supabase (BaaS) project, when the Supabase database is linked, then:
    - `VITE_SUPABASE_URL` and `VITE_SUPABASE_PUBLISHABLE_KEY` appear as derived, public, frontend variables for both environments
    - no secret key is exposed to the frontend
- **FR-146** *(backend + frontend)* The **origin model** (§4.17 R10) is part of the composition. Each model works as follows:
  - **same-origin:** one service serves both. Examples: Next.js full-stack; an SPA container proxying `/api` to the backend over the platform's private network on Railway; local Compose.
  - **proxy:** the frontend platform rewrites `/api/*` to `API_ORIGIN`. Mechanisms:
    - Next.js `rewrites()`
    - a Netlify `_redirects` file generated at build from `API_ORIGIN`
    - a Cloudflare Worker with `run_worker_first: ["/api/*"]`
    - Render static-site `routes` set by AIDE per service
  - **cross-origin:** the frontend calls `API_BASE_URL` (set at build time), the backend allows `CORS_ALLOWED_ORIGINS`, and auth uses bearer tokens or `SameSite=None; Secure` cookies.

  The derived variables (`API_ORIGIN`, `API_BASE_URL`/`VITE_API_BASE_URL`/`BACKEND_URL`, `CORS_ALLOWED_ORIGINS`) are filled per environment from the backend target's production and preview URLs. The stack-packs vocabulary has two values: AIDE `same-origin` and `proxy` are both pack "same-origin", and `cross-origin` matches.
  - Given a React SPA on GitHub Pages with a FastAPI backend on Railway, then:
    - the origin model is `cross-origin`
    - `VITE_API_BASE_URL` is derived from the Railway production domain
    - `CORS_ALLOWED_ORIGINS` on the backend includes the Pages URL
- **FR-147** *(backend + frontend)* **Preview environment** *(replaces FR-90's per-run Vercel preview)*. Each project with deployment targets has **one persistent preview environment**, matching AIDE's one-development-version-at-a-time model (D-V4-10).
  - **Per target:**
    - Vercel, Netlify and Cloudflare: preview deployments of the version branch
    - Render: a `-preview` service per backend or SSR app (and per static site when the frontend is on Render)
    - Railway: an `aide-preview` environment copied from production
  - **Per database:** a **preview database**, so migrations from unmerged code never touch production (§4.21). That's Render or Railway Postgres in the preview service or environment, or a second Supabase project when the Owner links one.
  - **Rollout:** when an execution run completes (PR opened or updated), a `preview_rollout` job deploys the PR head in dependency order. Each step waits for `ready` before the next:
    1. **preview database migrations** (Supabase BaaS, applied by AIDE, §4.21)
    2. **backend**, whose pre-deploy migrates its preview database
    3. **frontend**, built against the backend's preview URL, which is stable for the life of the version branch
  - The Review page shows each component's preview URL, state and build-log tail. A Contributor can redeploy.
  - Review waits for the whole rollout, up to 30 minutes (the v2 ceiling).
    - A failed build or pre-deploy is a **critical `architecture`** finding, with the log tail as evidence (v2 FR-90).
    - A failed preview migration is a critical `data_model` finding.
  - Hosts without previews (GitHub Pages) are skipped, with a note on the Review page. Review doesn't wait for them.
  - Given a React (Netlify) + ASP.NET (Render) + Render Postgres project, when execution completes, then:
    - the preview database is migrated by the backend's pre-deploy on `<app>-api-preview`
    - the Netlify branch deploy is built with `API_ORIGIN` = the preview service URL
    - the Review page lists both previews as `ready`
  - Given the backend preview fails its pre-deploy migration, then the frontend preview isn't started, and the report has a critical finding with the pre-deploy log.
- **FR-148** *(backend + frontend)* **Preview database reset.** An Owner can reset the preview database: drop and re-provision it or, for Supabase, reset the linked preview project's schema by re-applying the migrations from the base commit.
  - Reset is offered automatically after a rollback that removed migrations (v1 FR-44).
  - Reset is required before the next preview rollout when AIDE detects applied preview migrations that no longer exist in the branch. That raises a `PREVIEW_DATABASE_DIVERGED` warning, and the preview rollout is blocked until reset.
  - Given a rollback to a checkpoint before migration `0003`, when the next preview rollout starts, then it is blocked with `PREVIEW_DATABASE_DIVERGED`, and the Owner sees "Reset preview database".
- **FR-149** *(backend + frontend)* **Production deployments** *(amends FR-91)*. After the release merge, a `production_rollout` job deploys every target in order: **database migrations (Supabase BaaS only, §4.21) → backend → frontend**.
  - **Adopt or create:** it adopts a deployment the platform started from the merge commit, matched by commit SHA within 60 s. That covers Vercel, Netlify and Cloudflare production builds of the default branch, and Render/Railway auto-deploys from `main`. Otherwise it creates one explicitly:
    - Render: `POST /deploys {commitId}`
    - Railway: `serviceInstanceDeployV2(commitSha)`
    - Netlify: `POST /builds` with the production branch
    - Workers Builds: `POST /builds {branch, commit_hash}`
  - It's idempotent per `(release_id, component)`, and each deployment is recorded on the release.
  - **Failure:** a failed production deployment doesn't undo the release (v2). It notifies Owners (`DEPLOYMENT_FAILED`) and shows "Redeploy" on the Release page.
  - **GitHub Pages:** AIDE waits for the Pages workflow run on the merge commit, then reads `GET …/pages/deployments/{sha}` and `html_url`.
  - Given release 0.1.0 of the React + ASP.NET project is merged, when both deployments are `ready`, then the Release page shows "Released 0.1.0 · live on Netlify and Render" with both URLs.
- **FR-150** *(backend + frontend)* **Rollback and promote** *(amends FR-91)* follow each provider's capability (§4.20):
  - Vercel: promote
  - Netlify: `restore`
  - Cloudflare Workers: deploy an earlier version at 100 %
  - Cloudflare Pages: `rollback`
  - Render: `rollback {deployId}`
  - Railway: `deploymentRollback` (when `canRollback`)
  - GitHub Pages has no rollback (`DEPLOYMENT_ACTION_UNSUPPORTED → 409`)

  Rolling back a backend never rolls back its database. The dialog warns when the target deployment is older than the latest applied migration.
  - Given a Render backend with two `live` production deploys, when an Owner rolls back to the older one, then Render's rollback deploy is recorded with trigger `rollback`, and the Deployments page shows it as current.
- **FR-151** *(frontend + backend)* The **Deployments** page *(amends FR-91)* lists every deployment with: component, environment (preview/production), provider, version, SHA, state, URL, time and who triggered it.
  - It can be filtered by component and environment.
  - The project overview shows the production URL of each component.

### 3.10 Managed databases and migrations

- **FR-152** *(backend + frontend + data)* The **database card** (FR-144) for each environment lets an Owner:
  - **Provision** a new database on the composition's database host:
    - a Supabase project (`POST /v1/projects` with `organization_slug`, `region_selection` and an AIDE-generated `db_pass`)
    - Render Postgres (`POST /v1/postgres`, version 17)
    - Railway Postgres (a Postgres service with a volume in the project's environment)
  - **Link** an existing one (picker from the connection).
  - Enter an **External** connection string: a `postgres://` or `postgresql://` URI, or an ADO.NET string for SQL Server. It is stored encrypted.

  Provisioning:
  - A `database_provisioning` job polls until the database is available (§4.21), and stores the connection details encrypted.
  - The card shows status, region and plan, plus warnings:
    - Render free Postgres expiry (30 days, plus a 14-day grace period)
    - Supabase free-plan pausing after 7 days of inactivity
    - the Supabase 2-free-projects limit (`PLATFORM_PLAN_LIMIT`, with "link an existing project instead")
  - The preview database is created automatically with the preview service or environment on Render and Railway. For Supabase it's optional: the Owner links or provisions a second project.
  - Given an Owner provisions a Supabase project, when its status reaches `ACTIVE_HEALTHY` and the health check reports db, pooler, auth and rest as healthy, then the card shows `available` and the derived env vars appear (FR-145).
- **FR-153** *(backend)* **Supabase BaaS** projects:
  - **API keys:** AIDE fetches the project's keys (`GET …/api-keys?reveal=true`), using the **publishable** (`sb_publishable_…`) and **secret** (`sb_secret_…`) keys, and creates them when missing. Legacy `anon`/`service_role` keys are used only for migrated v2 projects that already reference them.
  - **Auth URLs:** AIDE sets Auth `site_url` to the frontend production URL, and `uri_allow_list` to the production URL, the frontend host's preview URL pattern, and `http://localhost:<dev port>/**` (`PATCH …/config/auth`). It updates them when target URLs change.
  - **Migrations:** files in `supabase/migrations/*.sql` are applied by AIDE's own migration runner (§4.21): to the preview project during preview rollout, and to the production project during release.
  - **Advisors:** after applying, AIDE reads `GET …/advisors/security` and adds its findings (e.g. a table without RLS) to the next review as deterministic findings.
  - Given a BaaS project releasing a version that adds `0003_orders.sql`, when the release job runs, then the migration is:
    - applied to the production project before the PR is merged (§4.21 "When")
    - recorded in `supabase_migrations.schema_migrations` with version `0003`'s timestamp
    - listed under the release's migration run
- **FR-154** *(backend + deploy)* **Host-mode databases** (Supabase, Render, Railway or External as the Postgres for an ORM). AIDE never runs ORM migrations itself. The ORM's one-off migrate command (from the data pack) runs on the platform:
  - **Render/Railway backends:** the service's pre-deploy command, on Render paid instances and on Railway. On Render free instances, the deploy engineer runs migrations at container start (single instance), recorded as a caveat.
  - **Serverless hosts** (Vercel/Netlify functions; Next.js full-stack on Vercel, Netlify or Cloudflare): the production build command runs the migrate command only when `AIDE_RUN_MIGRATIONS=true`. AIDE sets that only in environments with their own database: production, and preview when a preview database exists (D-V4-13).
  - **Connection variables:**
    - `DATABASE_URL`. For Supabase: the transaction pooler (`:6543`, prepared statements off) for serverless, the session pooler (`:5432`) otherwise.
    - `DIRECT_URL`: the session pooler (`:5432`), for migrations.
    - The Supabase direct host `db.<ref>.supabase.co` is never used (it is IPv6-only).
  - Given a Next.js + Prisma project on Vercel with Supabase host mode and no preview database, then:
    - `AIDE_RUN_MIGRATIONS` is `true` for production only
    - preview builds skip migrations
    - the Review page notes "Preview uses the production database without this version's migrations" (unmerged migrations never reach production)

### 3.11 Events, notifications and CLI

- **FR-155** *(backend + frontend)* New events (live, activity feed, admin): `STACK_CHANGED`, `STACK_DETECTED`, `GIT_HOST_INSTANCE_CHANGED`, `GIT_BINDING_CLEARED`, `DEPLOYMENT_TARGET_CONFIGURED`, `DEPLOYMENT_TARGET_FAILED`, `DATABASE_PROVISIONED`, `DATABASE_PROVISIONING_FAILED`, `DATABASE_EXPIRING`, `DATABASE_RESET`, `MIGRATION_RUN_STARTED`, `MIGRATION_RUN_COMPLETED`, `MIGRATION_RUN_FAILED`, `PREVIEW_ROLLOUT_COMPLETED`. `PRESET_CHANGED` is retired (its history is kept).
  - New notification types, all sent to Owners:
    - Git binding cleared
    - database provisioning failed
    - database expiring: 7 days before a Render free database expires, or when a Supabase project is paused
    - migration failed
    - deployment target needs attention: platform Git access lost, or the token is invalid
  - Given a Render free Postgres 7 days from expiry, then Owners get a "database expiring" notification with the date.
- **FR-156** *(cli + backend)* CLI additions *(amends FR-93)*:
  - **Stack commands:**
    - `aide stack catalog` (layers and options)
    - `aide stack show <id>`
    - `aide stack set <id> --frontend <opt> --backend <opt> --data <opt>[:baas|host] --vcs <opt> --frontend-host <p> --backend-host <p> --database-host <p> [--local compose|none]`. It is validated, and prints any violations.
    - `aide stack detect <repo-url>`, for a connected repo
  - **Version start:** `aide version start … --preset` is replaced by the `--stack-*` flags above. `--preset` exits 2 with "presets were removed in v4; use --frontend …".
  - **Project create:** `aide project create` gains `--llm-provider <p> --llm-model <id> [--llm-connection <id>]` (FR-159).
  - **Listings:** `aide deployments <id> [--component frontend|backend] [--env preview|production]`, `aide databases <id>`, `aide migrations <id>`.
  - `status` includes the composition summary, each target's production URL and the latest migration run.
  - Exit codes follow v1. `STACK_INCOMPATIBLE`, `STACK_LOCKED` and `LLM_PROVIDER_UNAVAILABLE` → 4.
  - Given `aide stack set <id> --backend be-aspnet --data data-prisma`, then it exits 4 and prints the `R3` violation.

### 3.12 LLM providers, models and long outputs

- **FR-157** *(backend + frontend + deploy)* **Provider enablement per deployment.** Each LLM provider has `Llm:Providers:<P>:Enabled` (default `true`). A disabled provider:
  - is absent from every provider list: `GET /api/llm/providers`, project LLM settings, Connections, Admin → Integrations → LLM, and the wizard
  - is refused on project create and on `PATCH …/settings/llm` (`LLM_PROVIDER_UNAVAILABLE → 400`)
  - makes agent runs of projects still bound to it fail with `LLM_PROVIDER_UNAVAILABLE`. Their settings page shows "This provider is turned off on this server. Choose another provider."

  Deployment rules:
  - Startup validation fails unless at least one provider is enabled, and `Llm:DefaultProvider` must be an enabled provider.
  - **Railway** (`preview` and `production`) sets `Llm__Providers__Ollama__Enabled=false` and `Llm__Providers__Fake__Enabled=false` in `.railway/railway.ts` and `deploy/railway/variables.md`.
  - The dev loop, Docker Desktop Kubernetes (including the E2E overlay) and GitHub Actions CI keep the defaults, so all four providers stay enabled and CI's `Llm__DefaultProvider: fake` keeps working.
  - Given Railway production, when a user opens the wizard's AI model section, Connections or project LLM settings, then only Anthropic and OpenAI are listed.
  - Given a Railway project still set to Fake from before v4, when its next agent run starts, then the run fails with `LLM_PROVIDER_UNAVAILABLE`, and the settings page asks the Owner to choose another provider.
  - Given CI with defaults, when `GET /api/llm/providers` is called, then all four providers are listed and Fake is `usable`.
- **FR-158** *(backend)* `GET /api/llm/providers` returns, for the signed-in user, each **enabled** provider with:
  - `usable` and `reason`: one of `system_key`, `own_connection`, `keyless`, or `needs_connection`
  - the user's LLM connections for it (id, label, last4; FR-78)
  - its **model catalog** (§4.24): id, display name, max output tokens, context window, recommended flag
  - the provider's default model
  - `isDefault`: true on the one provider the default rule (§4.24) would pick for this user, so the wizard preselects it *(build decision 2026-10-25)*

  It replaces the per-project `providers[]` as the wizard's source. `providers[]` on project LLM settings stays, filtered to enabled providers.
  - Given Anthropic with no system key and a user holding one Anthropic connection, then Anthropic is `usable` with reason `own_connection` and lists that connection. Its catalog shows `claude-opus-5-5` (128,000 max output tokens, recommended).
- **FR-159** *(frontend + backend + cli)* **LLM choice at project creation** *(amends FR-5)*. The wizard's **Configuration** step gains an **AI model** section with:
  - **Provider:** only enabled providers. Ones the user can't use yet show "Add a connection" and link to Connections, keeping the wizard's state.
  - **Connection:** the user's own connections for that provider. Shown when the provider needs a key and system keys aren't allowed or available. The first connection is preselected.
  - **Default model:** a picker over the provider's catalog, showing each model's max output tokens, plus "Other model id…" (free text).
  - **Per-agent models:** optional overrides in a collapsed "Advanced" area (the v2 `ModelsByAgent` agents).

  Create request and defaults:
  - `POST /api/projects` gains `llm {provider, credentialId?, defaultModel, modelsByAgent?}`. It creates `ProjectLlmSettings` bound to that credential, as v2 FR-79 binding does.
  - When the provider needs a key, no system key is usable and no `credentialId` is sent, the caller's newest valid connection for that provider is bound *(build decision 2026-10-25)*.
  - `llm` is optional for API and CLI callers. When omitted, today's default applies (`DefaultForNewProject`), **except that a disabled provider is never chosen**. If no enabled provider is usable, create fails with `LLM_PROVIDER_UNAVAILABLE`.
  - The Review step shows the provider, model and connection.
  - **Errors:** provider disabled or not usable by this user → `LLM_PROVIDER_UNAVAILABLE → 400`. A credential that isn't the caller's → `CREDENTIAL_NOT_OWNER → 403`. An empty model id → `VALIDATION → 400`.
  - Given a user on Railway with one Anthropic connection, when they create a project choosing Anthropic, that connection and `claude-opus-5-5`, then the project's LLM settings are bound to the connection with default model `claude-opus-5-5`, and its first refinement run uses them. No visit to Settings → LLM is needed.
  - Given an API call to `POST /api/projects` with no `llm`, on Railway with no system keys, by a user with no connections, then `LLM_PROVIDER_UNAVAILABLE` (it never silently falls back to Fake there).
- **FR-160** *(backend)* **Per-model maximum output tokens** *(replaces the single `Llm:MaxOutputTokens` cap)*. Each LLM call's `max_tokens` (Anthropic), `max_completion_tokens` (OpenAI) or `num_predict` (Ollama) is resolved per provider and model by the pure rule in §4.24.
  - Sources: the model catalog, provider discovery for unlisted Anthropic models, the operator ceiling, and the fallback.
  - The resolved value is recorded on each agent run (`maxOutputTokens`).
  - Given project model `claude-opus-5-5` and no ceiling, when a spec run calls Anthropic, then the request has `max_tokens: 128000`.
  - Given `claude-haiku-4-5`, then `64000`. Given an unlisted Ollama model, then `Llm:MaxOutputTokens` (default 16,000).
- **FR-161** *(backend)* **Streaming LLM calls.** Anthropic, OpenAI and Ollama calls, with or without tools, use the providers' streaming responses and assemble the final message, stop reason, tool calls and usage from the stream. This lets 128,000-token outputs finish without HTTP timeouts.
  - **Timeouts:** `Llm:RequestTimeout` becomes the whole-call ceiling (default `00:30:00`). The new `Llm:StreamIdleTimeout` (default `00:02:00`) fails a call that receives no stream data for that long.
  - **Failures:** both timeouts map to the existing provider failure handling and retries. A stream that ends without a final stop event is a retryable provider error. Cancellation (run cancelled or lease lost) aborts the stream.
  - Token usage and cost are taken from the stream's usage events, as before.
  - Given a scripted stub stream that delivers 100,000 output tokens over 12 minutes with no gap over 30 s, then the call succeeds.
  - Given a stub that stalls for 3 minutes mid-stream, then the call fails with the idle timeout and is retried under the v1 retry rule.
- **FR-162** *(backend + frontend)* **Continuing cut-off answers** *(amends the v2 continuation fix under FR-23/FR-71)*. When an answer stops at the output limit (`max_tokens`, `length`, or Ollama `length`), AIDE sends the answer so far plus the continue prompt, **up to 5 continuations** (6 calls) for one agent answer, i.e. one artifact or one run output.
  - Continuations are stitched as today (§4.24).
  - The v1 corrective retry (e.g. `MISSING_SECTIONS`) is a new answer with its own allowance of 5.
  - **Stop conditions:** AIDE stops early, with the same warning, when the next continuation's input would exceed the model's context window, or when the token budget hook refuses (v2: `LlmToolLoopStoppedException`).
  - **`output_truncated` warning:** if the answer is still cut off after the 5th continuation, the run still completes with the warning `output_truncated`.
    - It becomes an `AgentRunWarnings` constant.
    - Its frontend copy is "The model hit its output limit and was still writing after 5 continuations, so this output may be incomplete."
    - It shows on run details and the run's artifact page.
  - The run output records `continuations` (0–5).
  - Given a stub that stops with `max_tokens` on calls 1–3 and `end_turn` on call 4, then the joined answer is returned, `continuations: 3`, and there's no warning.
  - Given a stub that stops with `max_tokens` on all 6 calls, then exactly 6 requests are made, the run completes with `output_truncated`, and the UI shows the copy above.

## 4. Domain rules

v1 §4.1–4.9 and v2 §4.10 and §4.13–§4.15 apply, with these amendments and additions. **v2 §4.11 and §4.12 are replaced** by §4.16–§4.18 and §4.23. Every worked example is a unit test.

### 4.16 Stack catalog format *(replaces v2 §4.11)*

- **Location:** the catalog is the folder `backend/src/Aide.Infrastructure/Agents/Stack/` (embedded resources), loaded once at startup into pure Domain types:

  ```
  Stack/
    catalog.json              version, layers[], options[], rules (§4.17), recommendations[]
    packs/<pack-id>/
      pack.json               manifest (below)
      skill.md                adapted SKILL.md; its "## " headings are the section interface
    core/
      stack-api-contract.md   adapted stack-core skill
      composition-summary.md  template rendering the STACK.md equivalent for prompts
    agents/<key>.md           adapted stack-core agent prompts (FR-120)
    legacy/                   frozen v2 preset globs and agent-key map (§4.23)
  ```
- **Version and hash:** `catalog.json.version` (semver), plus a SHA-256 over all files using v2's preset hash algorithm: sorted paths, `path \0 content \0`, `\n` line endings, BOM stripped. Both are pinned on the release version and recorded on agent runs as `stack@<version>`.
- **`pack.json`** (unknown properties are load errors, as in v2):

  ```
  id (= folder, = stack-packs plugin name), layer (frontend|backend|data|vcs|deploy|local),
  name, version ("<plugin version>+aide.<n>"), source {plugin, version, snapshot: "2026-10-25"},
  chips[], description, defaultPath ("frontend"|"backend"|"."|null),
  publicEnvPrefix ("NEXT_PUBLIC_"|"VITE_"|null),          # frontend packs
  runtime ("node"|"python"|"dotnet"|"java"|"static"),       # frontend/backend packs, for deploy shapes
  modes[] {id, name, description}                           # e.g. data-supabase: baas, host; fe-nextjs: full-stack, ui
  specSections[] {title, guidance, when?}                   # when: a condition on the composition (e.g. "mode == baas")
  ownership {<agentKey>: [globs with {fe}/{be} placeholders]}
  agentSections {<agentKey>: ["*" | section heading]}       # which skill sections each agent loads
  planRules {order[], exclusive[], maxFilesPerStep[]}       # v2 rule shapes, globs with placeholders
  protectedPaths[] {glob, existingOnBase?, except?, reason}
  generatedFiles[] {glob, handling: hand_written|ci_checked|ci_generated, ciCheck?}
  requiredEnv[] {key, appliesTo[], targets[], sensitive, public, description, when?}
  review {checklist[] {item, category, severity}, deterministic[] (rule ids, §4.18)}
  gate (fe/be packs: the pack's Gate commands, for the ci-engineer and the composition summary)
  migrate {command, needsDirectUrl}                         # data packs: the one-off migrate command
  deploy {...}                                              # deploy packs: provider id and supported shapes (§4.17 R6–R8)
  ```
- **Section headings** are the pack's `## ` headings, matched after stripping a trailing parenthetical. For example, `## Modelling (BaaS)` matches `Modelling`, and `## Config as code (\`netlify.toml\`)` matches `Config as code`. The headings per layer, from stack-packs `author-pack` and the packs as they stand:
  - **frontend:** `Detect · Scaffold · Conventions · API consumption · Errors, permissions, states · Config · Testing · Gate · Containerise · Deploy notes · Checklist`
  - **backend:** `Detect · Scaffold · Conventions · Errors · Auth · Contract export · Config and database URL · Health · Testing · Gate · Containerise · Deploy notes · Checklist`
  - **data:** `Detect · Compatible with · Setup · Modelling · Migrations · Local database · Testing · Production notes · Checklist` (no `Gate`)
  - **vcs:** `Detect · Branches and commits · PRs · CI · Secrets and variables · Dependency updates · Templates · Safety · Checklist`. vcs-gitlab has `PRs/MRs` instead of `PRs`, and vcs-bitbucket adds `Auth`
  - **deploy:** `Supports · Prerequisites and auth · Config as code · Environment variables · Database · Deploy commands · Previews and promotion · Agent autonomy · Troubleshooting · Checklist`, plus `Base path` on GitHub Pages
  - **local:** `Prerequisites · Files · Commands · Safety · Troubleshooting · Checklist`
- **Adaptation rules** (D-V4-3, extending v2's claude-kits rules):
  - **Keep each pack's section headings.** They are the interface.
  - **Drop:**
    - CLI scaffolding commands (the scaffolder writes the files they would create)
    - "run X" instructions and "only when the user asks" remote actions (AIDE performs Git and platform operations itself, under its own rules)
    - MCP setup, `link`/`login` steps, CLI deploys (`railway up`, `wrangler deploy`, `netlify deploy`)
    - anything about pushing, PRs or reviews
    - the packs' `Agent autonomy` sections. AIDE's own rules replace them, and the section is loaded by no agent.
  - **Keep, as output requirements:** conventions, folder layouts, error and auth patterns, env names, health paths, container recipes, gate commands (for CI files), migration rules and checklists.
  - **Versions:** replace "current stable, check the docs" with "use current stable versions you are confident exist; pin Node/.NET/Java/Python versions in their version files".
  - **Provenance:** every adapted file starts with a comment naming its source file and plugin version. Content changes bump the pack's `+aide.<n>` revision and the catalog version.
- **Load errors:** missing files, unknown section headings in `agentSections`, unknown pack ids in rules, and invalid globs fail loading (FR-122).

### 4.17 Compatibility rules

The rules are data in `catalog.json`, evaluated by one pure Domain function: `StackCompatibility.Evaluate(partialComposition) → options per layer with state and reasons`.
- It's used by `/options`, `/validate`, project create, `PUT /stack`, `POST /versions` and the CLI.
- Rule ids are stable, because tests and violations cite them.
- The rules come from stack-packs `compose-stack` (its matrix lines 51–69), narrowed to Git-integration builds (D-V4-9), plus the provider research in §4.19–§4.21.

| Rule | Statement |
| --- | --- |
| R1 | Frontend and Backend can't both be None. |
| R2 | The `fe-nextjs` mode is derived: **full-stack** when Backend = None, **ui** when there's a backend. |
| R3 | Data ↔ backend: `data-efcore-postgres` and `data-efcore-sqlserver` → `be-aspnet`. `data-jpa-flyway` → `be-spring-boot`. `data-sqlalchemy` → `be-fastapi`. `data-prisma` and `data-drizzle` → `be-express`, `be-nestjs`, or Backend None with `fe-nextjs`. `data-supabase` (BaaS) → Backend None and any frontend. None → always allowed. |
| R4 | `data-efcore-sqlserver` is a `caveat`: "No managed SQL Server on the catalog's platforms: use an External connection string (e.g. Azure SQL) or local only." |
| R5 | VCS: any option, but fixed to the repo's host once a repo is chosen or imported (`VCS_MISMATCH`). |
| R6 | Frontend host, by frontend. **Static SPAs** (`fe-react-vite`, `fe-vue`, `fe-angular`, `fe-vanilla`) → Vercel, Netlify, Cloudflare, GitHub Pages, Render (static site), Railway (static or nginx container). **`fe-nextjs`** → Vercel, Netlify, Render (Node service), Railway, and Cloudflare as a `caveat` ("Cloudflare's Next.js adapter: some Node APIs and middleware are limited"). GitHub Pages is allowed only in **ui** mode or with `data-supabase`, as a `caveat` ("static export: no server actions, route handlers or SSR"). Otherwise it's forbidden ("Next.js full-stack needs a server"). |
| R7 | Backend host, by backend. `be-aspnet` and `be-spring-boot` → Render (Docker), Railway. `be-fastapi`, `be-express` and `be-nestjs` → Vercel as a `caveat` ("serverless functions: no background work, pooled DB connections"), Render, Railway. Netlify, Cloudflare and GitHub Pages can't host backends ("not covered by the deploy pack"). |
| R8 | Database host, by data. ORM packs on PostgreSQL (`data-efcore-postgres`, `data-jpa-flyway`, `data-sqlalchemy`, `data-prisma`, `data-drizzle`) → Supabase (host mode), Render Postgres, Railway Postgres, External, None. Render or Railway Postgres is `allowed` when the backend (or the Next.js full-stack app) runs on the same platform, and a `caveat` otherwise ("database on another platform: public connection, higher latency"). `data-efcore-sqlserver` → External or None. `data-supabase` (BaaS) → Supabase only. Data None → no database host. |
| R9 | Deploy ↔ VCS (each platform's Git integration, §4.20). **GitHub** → every platform. **gitlab.com** → Vercel, Netlify, Cloudflare, Render. Not Railway ("Railway only builds from GitHub") and not GitHub Pages. **Self-managed GitLab** → Netlify only ("only Netlify builds from self-managed GitLab"). **Bitbucket** → Vercel, Netlify, Render. Not Cloudflare, Railway or GitHub Pages. Netlify with GitLab or Bitbucket is a `caveat` ("linked with a deploy key and webhook that AIDE adds to the repo"). |
| R10 | Origin model. Next.js full-stack, or no backend → `same-origin`. Backend and SPA both on Railway → `same-origin` (the SPA container proxies over the private network) or `cross-origin`. Frontend on Netlify, Cloudflare or Render static, or `fe-nextjs` ui mode anywhere → `proxy` (default) or `cross-origin`. Frontend on Vercel as a static SPA, or on GitHub Pages → `cross-origin` only (D-V4-20). Frontend host None → `same-origin` (local). |
| R11 | Every host may be None (local only). A database host other than None needs a deploy target that can reach it: a backend host, or the frontend host in Next.js full-stack or BaaS mode. Otherwise it's a `caveat`: "database only used by the local dev loop". |
| R12 | `local-docker-desktop` (the Compose dev loop) is offered when there's a backend or a database, and is the default then. |

Worked examples:

| # | Composition | Result |
| --- | --- | --- |
| 1 | `fe-react-vite` · `be-aspnet` · `data-efcore-postgres` · gitlab.com · frontend Netlify, backend Render, DB Render Postgres | ✓ allowed. Netlify `caveat` (R9 deploy key). Origin `proxy` |
| 2 | as 1, with backend host Vercel | ✗ R7 "Vercel can't run ASP.NET Core" |
| 3 | `fe-vue` · None · `data-supabase` · Bitbucket · frontend Cloudflare | ✗ R9 "Cloudflare can't build Bitbucket repos". Vercel, Netlify and Render are offered |
| 4 | `fe-nextjs` · None · `data-prisma` · GitHub · frontend GitHub Pages | ✗ R6 "Next.js full-stack needs a server". Vercel is ✓ with DB Supabase (host) |
| 5 | None · `be-fastapi` · `data-sqlalchemy` · GitHub · backend Railway, DB Railway Postgres | ✓. Origin n/a (API only) |
| 6 | `fe-angular` · `be-spring-boot` · `data-jpa-flyway` · self-managed GitLab · backend Render | ✗ R9. Backend host offers only None |
| 7 | `fe-react-vite` · `be-aspnet` · `data-efcore-sqlserver` · GitHub · backend Railway, DB External | ✓ with the R4 `caveat` |
| 8 | `fe-vanilla` · None · None · GitHub · frontend GitHub Pages | ✓. Origin `same-origin` |
| 9 | `fe-react-vite` · `be-nestjs` · `data-drizzle` · GitHub · frontend Vercel, backend Render, DB Supabase | ✓. Origin `cross-origin` only (R10). Supabase host mode |
| 10 | `fe-nextjs` · `be-express` · `data-prisma` · gitlab.com · frontend Vercel, backend Railway | ✗ R9 "Railway only builds from GitHub" |

`POST /api/stack/options` returns, for each layer after the last chosen one, every option with its `state` and `reasons[] {rule, text}`. Earlier layers are re-evaluated, so a now-forbidden choice is reported as `invalidated`.

### 4.18 Composition → agents, skills, rules and checks

- **Normalised composition** (stored on the release version as `jsonb`, validated by the Domain):

  ```
  { catalogVersion, catalogHash,
    frontend: {pack, path} | null, backend: {pack, path} | null,
    data: {pack, mode?} | null, vcs: {pack, instanceId?},
    deploy: {frontendHost?, backendHost?, databaseHost?, local: "compose"|"none"},
    originModel, derived: {nextjsMode?, agents[], specSections[], envNames[], gates[]} }
  ```
- **Skill resolution** (which sections each specialist loads; `*` = the whole adapted skill):

  | Agent | Loads |
  | --- | --- |
  | `scaffolder` | frontend packs: `Detect`, `Scaffold`, `Conventions`, `Config`. Backend packs: `Detect`, `Scaffold`, `Conventions`, `Config and database URL`. Data pack: `Setup`. VCS pack: `Detect`. `stack-api-contract` when there's a backend |
  | `data-engineer` | data pack `*`. With database host Supabase in host mode, also `data-supabase`: `Production notes` |
  | `backend-engineer` | backend pack `*`, `stack-api-contract`, and the data pack's `Setup` and `Modelling` (to use the client, not to own the schema) |
  | `frontend-engineer` | frontend pack `*`. `stack-api-contract` when there's a backend. `data-supabase` `*` in BaaS mode. The data pack's `Setup` in Next.js full-stack mode |
  | `test-engineer` | `Testing` and `Gate` of the frontend/backend packs, and `Testing` of the data pack |
  | `ci-engineer` | VCS pack `*`. `Gate` of the frontend/backend packs. The data pack's `Migrations` |
  | `deploy-engineer` | each selected deploy pack `*` (minus `Agent autonomy`). Frontend/backend `Containerise` and `Deploy notes`. The data pack's `Migrations` and `Production notes`. `local-docker-desktop` `*` when the dev loop is Compose |
  | `docs-writer` | the composition summary only (README outline from `stack-docs-writer`) |
- **Ownership** (globs expanded with `{fe}` and `{be}`, the layer paths):

  | Agent | Owns |
  | --- | --- |
  | `scaffolder` | `**` (any file), but only in step 1 of an empty repo or the first step of a stack change (plan rule) |
  | `data-engineer` | data pack paths. EF: `{be}/src/*Infrastructure*/Persistence/**`, `{be}/src/*Infrastructure*/Migrations/**`. Flyway: `{be}/src/main/resources/db/migration/**`, `{be}/src/main/java/**/domain/**/*Entity.java`. Alembic: `{be}/alembic/**`, `{be}/app/models/**`. Prisma: `{be or fe}/prisma/**`. Drizzle: `{be or fe}/src/db/**`, `{be or fe}/drizzle/**`. Supabase: `supabase/**`, `{fe}/src/lib/database.types.ts` |
  | `backend-engineer` | `{be}/**`, except the data-engineer's globs and the deploy files. Includes `{be}/openapi/api.json` |
  | `frontend-engineer` | `{fe}/**`, except the data, deploy and E2E globs. In Next.js full-stack, also `{fe}/src/app/api/**` and `{fe}/src/server/**` |
  | `test-engineer` | `**/e2e/**`, `**/playwright.config.*`, and the backend integration test projects/folders (`{be}/tests/**`, `{be}/test/**`, `{be}/src/test/**`) |
  | `ci-engineer` | `.github/**` (except `pages.yml`), `.gitlab-ci.yml`, `.gitlab/**`, `bitbucket-pipelines.yml`, `renovate.json`, `CODEOWNERS`, `scripts/ci/**`, `docs/pr/**` |
  | `deploy-engineer` | `**/Dockerfile`, `**/.dockerignore`, `compose.yaml`, `**/nginx.conf`, `vercel.json`, `netlify.toml`, `**/_redirects`, `**/wrangler.jsonc`, `{fe}/worker/**`, `.github/workflows/pages.yml`, `render.yaml` and `.railway/**` (documentation only, D-V4-11), `.env.example` |
  | `docs-writer` | `README.md`, `docs/**` (except `docs/pr/**`) |
- **Plan rules** (composed, in v2 rule shapes):
  - Scaffold first when the repo is empty (the scaffolder is exempt from order rules, per the v2 decision).
  - The data pack's migrations come before the backend (or Next.js server) steps that use them.
  - A step changing `{be}/openapi/api.json` comes before frontend steps that change the generated client path (soft rule).
  - **Exclusivity:** data-engineer steps must not declare frontend UI or endpoint files. Backend and frontend steps must not declare migration globs.
  - One migration per step.
  - The ci-engineer is the only agent for CI files, and the deploy-engineer is the only agent for platform config.
- **Spec sections** (their union, deduplicated by title, each with the pack's guidance):
  - any backend → "API Contract & Error Codes"
  - any data pack → "Data Model & Migrations"
  - `data-supabase` BaaS → "Data Model & RLS Policies" (replacing the previous one) and "Auth Flows"
  - `fe-nextjs` → "Routes & Rendering Strategy"
  - a frontend without a backend or data → "Data Sources & Consumed APIs"
  - any deploy host → "Environments & Configuration"
  - origin model `proxy` or `cross-origin` → "Origin Model (CORS, Cookies, Auth)"
  - a stack change → "Stack Migration"
- **Review checks** (deterministic, language-independent text checks; ids are registered by packs; severities per v1 §4.7):

  | Rule id | Packs | Finding |
  | --- | --- | --- |
  | `rls_on_every_table` | `data-supabase` BaaS | `create table` in `supabase/migrations/` without `enable row level security` → critical security (v2 rule) |
  | `grants_for_exposed_tables` | `data-supabase` BaaS | a new `public` table without a `grant … to authenticated`/`anon` → major `api_contracts` ("not exposed to the Data API", the Supabase 2026 default) |
  | `no_secret_in_client` | `fe-nextjs`, `fe-react-vite`, `fe-vue`, `fe-vanilla`, `fe-angular` | critical security (generalises v2), when one of these appears: (1) a public-prefixed name (`NEXT_PUBLIC_`/`VITE_`), or an Angular `environment*.ts` key, matching the secret patterns (`SECRET`, `SERVICE_ROLE`, `PRIVATE`, `PASSWORD`, `sb_secret_`, `sk_live`); (2) a non-public env name read in a `"use client"` file |
  | `no_committed_secrets` | `vcs-*` | a match of the vcs-github secret-scan pattern (`PASSWORD=`, `SECRET=`, `API_KEY=`, `PRIVATE KEY`, `sk_live`, `service_role`) outside `*.example` → critical security |
  | `no_ensure_created` | `data-efcore-*` | `EnsureCreated(` or `Database.Migrate(` outside tests → major architecture |
  | `no_ddl_auto_update` | `data-jpa-flyway` | `ddl-auto` set to `update`, `create` or `create-drop` outside test config → major architecture |
  | `no_create_all` | `data-sqlalchemy` | `create_all(` outside tests → major architecture |
  | `no_schema_push` | `data-prisma`, `data-drizzle` | `prisma db push` or `drizzle-kit push` in package scripts → major architecture |
  | `migration_backward_compatible` | every data pack | `drop table`, `drop column`, `rename column` or a type change in a new migration → major `data_model` ("expand/contract required", because preview and production deploys aren't atomic) |
  | `container_non_root` | backend packs with a Docker deploy | a backend `Dockerfile` without a non-root `USER` → minor security |
  | `deploy_branch_control` | `deploy-vercel`, `deploy-netlify`, `deploy-cloudflare` | major architecture (generalises v2 FR-89) when: `vercel.json` lacks `git.deploymentEnabled {"aide/**": false}`, `netlify.toml` lacks the `aide/*` ignore rule, or the Workers preview trigger lacks the `aide/*` exclude |
  | `health_endpoints` | backend packs | the pack's live/ready health paths are absent from the source → minor feature_coverage |
- **Generated files** (FR-127), per pack. Examples:
  - `be-*` → `{be}/openapi/api.json`: `hand_written`, plus CI export-and-diff
  - `fe-react-vite`, `fe-vue`, `fe-vanilla` → `{fe}/src/api/generated/**`: `hand_written`, plus CI regenerate-and-diff
  - `fe-angular` → the Orval output, handled the same way
  - `data-efcore-*` → the migration and `*ModelSnapshot.cs`: `hand_written`, plus CI `dotnet ef migrations has-pending-model-changes`
  - `data-supabase` → `database.types.ts`: `hand_written` (v2 decision)
  - lockfiles → `ci_generated` (absent until the first CI run)
- **Detection** (FR-118) is a pure function over the file list and manifest contents.
  - **Frontend:**
    - `next` in a `package.json` (or a `next.config.*`) → `fe-nextjs`
    - `@angular/core` + `angular.json` → `fe-angular`
    - `vue` + `vite` → `fe-vue`
    - `react` + `vite` → `fe-react-vite`
    - an `index.html` with no framework dependency → `fe-vanilla`
  - **Backend:**
    - a `*.csproj` referencing `Microsoft.AspNetCore` → `be-aspnet`
    - `spring-boot-starter-web` in `pom.xml`/`build.gradle*` → `be-spring-boot`
    - `fastapi` in `pyproject.toml`/`requirements*.txt` → `be-fastapi`
    - `@nestjs/core` → `be-nestjs`
    - `express` (without Nest) → `be-express`
  - **Data:**
    - `Npgsql.EntityFrameworkCore.PostgreSQL` → `data-efcore-postgres`
    - `Microsoft.EntityFrameworkCore.SqlServer` → `data-efcore-sqlserver`
    - `flyway` + JPA → `data-jpa-flyway`
    - `alembic` + `sqlalchemy` → `data-sqlalchemy`
    - `prisma` → `data-prisma`
    - `drizzle-orm` → `data-drizzle`
    - `supabase/config.toml` with no other data pack → `data-supabase` BaaS. With another data pack, host mode is noted.
  - **Hosts:** `vercel.json`, `netlify.toml`, `wrangler.jsonc`, `render.yaml`, `.railway/`, `.github/workflows/pages.yml` → that host as a suggestion. A legacy `railway.json`/`railway.toml` suggests Railway, with the evidence "legacy config, deprecated by Railway".
  - Each match carries its evidence. Conflicting matches leave the layer empty, with both shown.

### 4.19 Git hosts (GitLab, Bitbucket)

- **Common:** the provider interface (v2 `IGitProvider`) gains `ListReposAsync`, `ListOwnersAsync`, `CreateRepoAsync`, `GetMergeReadinessAsync`, `GetCommitStatusesAsync`, `FindReleaseRefsAsync` and `PublishReleaseAsync`. They're implemented for GitHub (existing behaviour), GitLab, Bitbucket and Local. Host base URLs are fixed (`https://gitlab.com`, `https://api.bitbucket.org/2.0`, `https://bitbucket.org`), except Admin-registered GitLab instances.
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
  | Readiness | `GET …/merge_requests/:iid` (`detailed_merge_status`, `has_conflicts`, `blocking_discussions_resolved`, `sha`, `head_pipeline`); `GET …/approvals` (`approved`; `approvals_left` on Premium only); `GET /projects/:id/protected_branches/:name` (`merge_access_levels`) |
  | Merge | `PUT …/merge_requests/:iid/merge {sha, squash, should_remove_source_branch: false}`; 405 not mergeable, 409 head moved, 422 failed; result SHA = `merge_commit_sha` ?? `squash_commit_sha` ?? `sha` (fast-forward) |
  | CI | `GET /projects/:id/repository/commits/:sha/statuses`; `GET /projects/:id/pipelines?sha=` |
  | Netlify link support | `POST /projects/:id/deploy_keys {title, key, can_push: false}`, `POST /projects/:id/hooks {url, push_events: true}` |

  More GitLab rules:
  - The status values `unchecked`, `checking`, `preparing` and `approvals_syncing` are transient.
  - **Roles:** Developer for push, MR and tags. **Maintainer** for merging into the protected default branch and for releases (a 403 maps to `insufficient_permission`).
  - **Minimum version:** GitLab 17.11. It's checked when an instance is added, and an older version shows a warning.
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
- **Polling budget** (no webhooks, v2 D-V2-8):
  - **GitLab:** merge polls every 2 min for an hour, then every 10 min (v2). Status polls every 30 s while waiting, backing off when `RateLimit-Remaining` is below 10 % and on 429 (`Retry-After`).
  - **Bitbucket** allows **1,000 requests per hour per user on repository endpoints**, with no remaining-count headers. So AIDE:
    - keeps a **per-connection token bucket** (900/h for background work)
    - polls a PR no more than once a minute
    - uses the single mergeability call instead of separate restriction, approval and status calls
  - Rate-limited jobs are re-queued without counting an attempt (v2 rule). No ETag support is assumed for either host.

### 4.20 Deployment platforms

- **Provider interface** *(generalises v2 `IDeploymentProvider`)*:
  - **Operations:** `ValidateConnectionAsync`, `ListAccountsAsync`, `ListResourcesAsync` (link picker), `SetupTargetAsync` (create or link, branch control, preview resource), `VerifyTargetAsync` (readiness checklist), `SyncEnvAsync`, `DeployAsync {environment, ref, sha}`, `GetDeploymentAsync`, `GetBuildLogTailAsync`, `FindDeploymentForShaAsync`, `CancelAsync`, `RollbackAsync`.
  - **Capabilities record:** `ShaPinned`, `Previews`, `Rollback`, `PerEnvironmentVariables`, `RuntimeSecrets`.
  - **Providers:** Vercel, Netlify, Cloudflare (Workers Builds; Pages adopt-only), Render, Railway, GitHub Pages, Fake.
- **Deployment states** (v2 §4.15) `queued → building → ready | error | canceled`, mapped per provider. The table gives the auth, create/link and branch-control calls:

  | Provider | Token (pasted) and validation | Create / link | Branch control for `aide/**` |
  | --- | --- | --- | --- |
  | **Vercel** (v2) | team/account token; `GET /v2/user` | `POST /v11/projects` with `gitRepository` (GitHub, GitLab, Bitbucket) | `vercel.json` `git.deploymentEnabled {"aide/**": false}` (minimatch; no other matching `true` rule) |
  | **Netlify** | personal access token (expiry chosen by the user); `GET /user`, `GET /accounts` | `POST /{account_slug}/sites` with `repo {provider, id, repo_path, repo_branch, cmd, dir, allowed_branches}`. GitHub via `installation_id` (the Netlify GitHub App, installed by the user). GitLab/Bitbucket via `POST /deploy_keys`, a repo deploy key, and a push webhook to `https://api.netlify.com/hooks/<provider>`, added by AIDE | `allowed_branches: [<production branch>]`, Deploy Previews off, and a `netlify.toml` `[build] ignore` that exits 0 for `aide/*` |
  | **Cloudflare** (Workers + Workers Builds) | **user** API token (Workers Scripts Edit, Workers CI Edit, Account Settings Read, User Details Read; plus Pages Edit for Pages mode); `GET /user/tokens/verify`, `GET /accounts` | The user installs the Cloudflare GitHub/GitLab app in the dashboard. AIDE calls `PUT /builds/repos/connections` and `POST /builds/tokens`, and creates two triggers: production (`branch_includes: [main]`, `deploy_command: npx wrangler deploy`) and preview (`branch_excludes: ["aide/*"]`, `deploy_command: npx wrangler versions upload`). First Worker bootstrap per the §11 check | the preview trigger excludes `aide/*` |
  | **Cloudflare Pages** (adopt existing only) | as above, plus Pages Edit | link an existing project; set `source.config.preview_deployment_setting: custom`, `preview_branch_excludes: ["aide/*"]` | as left |
  | **Render** | API key (account-wide; the user then picks a workspace from `GET /v1/owners`); `GET /v1/users` | The user connects the Git host once in Render (GitHub, GitLab, Bitbucket). AIDE calls `POST /v1/services`: `static_site {buildCommand, publishPath, routes}` or `web_service {runtime: docker\|node\|python, envSpecificDetails, preDeployCommand, healthCheckPath, plan, region}`, with `rootDir`, `branch: main`, and `autoDeployTrigger: "commit"` for production | the production service tracks `main` only; the preview service `<name>-preview` has `autoDeployTrigger: "off"` |
  | **Railway** | workspace token (`Authorization: Bearer`; account token accepted); `query { workspace(workspaceId) }` / `me` | The user installs the Railway GitHub app. AIDE calls `projectCreate {workspaceId, prDeploys: false}`, `serviceCreate {projectId, source: {repo}, branch: "main"}`, `serviceInstanceUpdate {rootDirectory, dockerfilePath \| buildCommand, startCommand, healthcheckPath, watchPatterns}` and `serviceDomainCreate`. Pre-deploy command per the §11 check | services trigger only on `main`; PR environments off |
  | **GitHub Pages** | the project's GitHub App/PAT (Pages RW) | `POST /repos/{o}/{r}/pages {build_type: "workflow"}`; the deploy-engineer writes `.github/workflows/pages.yml` | n/a (only the default branch deploys) |

  Deploying, rolling back and reading logs:

  | Provider | Preview | Production | Rollback | Logs |
  | --- | --- | --- | --- | --- |
  | **Vercel** | `POST /v13/deployments` `gitSource {ref, sha}` | adopt the auto-deploy of the merge SHA, else create | `POST /v10/projects/{id}/promote/{deploymentId}` | v2 events API |
  | **Netlify** | `POST /sites/{id}/builds?branch=<version branch>&title=aide:<run>`. It builds the branch HEAD, so AIDE checks `deploy.commit_ref` = the PR head, else re-triggers. Falls back to a build hook, which ignore rules don't cancel | adopt `GET /sites/{id}/deploys?production=true` with `commit_ref` = the merge SHA, else `POST /builds` | `POST /sites/{id}/deploys/{deploy_id}/restore` (reads `locked` after) | best effort: `deploy.error_message` + `admin_url` (no REST log endpoint) |
  | **Cloudflare** | `POST /builds/triggers/{preview}/builds {branch, commit_hash}` (**SHA-pinned**); `preview_url` from `GET /builds/builds/{uuid}` | adopt the production build with `build_trigger_metadata.commit_hash` = the merge SHA, else start it with the hash | `POST /workers/scripts/{name}/deployments {strategy: percentage, versions: [{version_id, percentage: 100}]}` | `GET /builds/builds/{uuid}/logs` (cursor) |
  | **Cloudflare Pages** | `POST /pages/projects/{name}/deployments` form `branch` (HEAD; verify `deployment_trigger.metadata.commit_hash`) | adopt | `POST …/deployments/{id}/rollback` | `GET …/deployments/{id}/history/logs` |
  | **Render** | `PATCH` the preview service's `branch` to the version branch, then `POST /v1/services/{id}/deploys {commitId}` (**SHA-pinned**) | adopt the auto-deploy of the merge SHA, else `POST /deploys {commitId}` | `POST /v1/services/{id}/rollback {deployId}` | `GET /v1/logs?ownerId&resource[]&type=build&startTime&endTime&direction=backward&limit=100` (30/min) |
  | **Railway** | `environmentCreate {name: "aide-preview", sourceEnvironmentId: production, skipInitialDeploys: true}` once, then `serviceInstanceDeployV2(serviceId, previewEnvId, commitSha)` (**SHA-pinned**) | `serviceInstanceDeployV2(prodEnvId, mergeSha)`, or adopt | `deploymentRollback(id)` when `canRollback` | `buildLogs(deploymentId, limit: 100)` |
  | **GitHub Pages** | none (`Capabilities.Previews = false`) | the Actions run on the merge SHA, then `GET /repos/{o}/{r}/pages/deployments/{sha}` and `GET …/pages` (`html_url`) | none | the failed workflow job's log URL |

  State mapping:

  | Provider | queued | building | ready | error | canceled |
  | --- | --- | --- | --- | --- | --- |
  | **Vercel** | v2 | v2 | v2 | v2 | v2 |
  | **Netlify** | `new`, `pending_review`, `accepted`, `enqueued` | `building`, `uploading`, `uploaded`, `preparing`, `prepared`, `processing`, `processed`, `retrying` | `ready` | `error`, `rejected` | — |
  | **Cloudflare** | `queued`, `initializing` | `running` | `stopped` + `build_outcome` success | `stopped` + fail or terminated | `stopped` + cancelled or skipped |
  | **Cloudflare Pages** | — | any other `latest_stage` | `latest_stage` `deploy` + `success` | `failure` | `canceled`, `skipped` |
  | **Render** | `created`, `queued` | `build_in_progress`, `update_in_progress`, `pre_deploy_in_progress` | `live` | `build_failed`, `update_failed`, `pre_deploy_failed` | `canceled`, `deactivated` |
  | **Railway** | `QUEUED`, `WAITING`, `INITIALIZING` | `BUILDING`, `DEPLOYING` | `SUCCESS` | `FAILED`, `CRASHED` | `SKIPPED`, `REMOVED` |
  | **GitHub Pages** | — | `deployment_in_progress`, `syncing_files`, `updating_pages` | `succeed` | failures | — |
- **Environment variable sync:**
  - **Vercel:** as in v2.
  - **Netlify:** `POST/PATCH /accounts/{account_id}/env?site_id=`, with contexts `production` and, for preview, both `deploy-preview` and `branch-deploy` (API branch builds run as branch deploys). `is_secret` for sensitive values.
  - **Cloudflare:** build variables per trigger (`PATCH /builds/triggers/{uuid}/environment_variables`, `is_secret`), and runtime secrets with `PUT /workers/scripts/{name}/secrets`. Runtime secrets are shared by preview and production, so a preview-only secret is refused with `VALIDATION`.
  - **Render:** per service, with `PUT /v1/services/{id}/env-vars` carrying the **full merged set** (it replaces), never deploying.
  - **Railway:** `variableCollectionUpsert {projectId, environmentId, serviceId, variables, replace: false, skipDeploys: true}`.
- **Polling cadence:** v2's 10 s for 2 min, then 30 s, with a 30-minute ceiling. Exceptions:
  - **Railway:** at least 20 s (the Free plan allows 100 requests/hour)
  - **Render logs:** at most 30/min per user
  - **Netlify:** deploy creation at most 3/min and 100/day per account, enforced before calling

  All of them honour 429 `Retry-After`/reset headers without counting an attempt.
- **Plan and cost notes surfaced in the readiness checklist** (not enforced):
  - Netlify production deploys cost 15 credits on credit plans, and the Free plan pauses all sites when credits run out.
  - Render free web services sleep after 15 minutes, and free Postgres expires after 30 days.
  - Railway Free/Trial limits.
  - Workers Builds has a 20-minute build timeout.
- **Pack deviations** (D-V4-11): stack-packs `deploy-render` is Blueprint-first and `deploy-railway` is IaC/CLI-first. AIDE creates resources through the platform APIs instead. The deploy-engineer may still commit `render.yaml` or `.railway/railway.ts` that mirror the created resources, as documentation for users who later manage the project themselves. AIDE never applies them.
- **Fake provider** (v2, generalised): every provider id has a Fake mode (`Deployments:Provider=Fake` applies to all). A Fake deployment:
  - reaches `ready` after 2 polls, with URL `https://fake-<sha8>-<component>.<provider>.test`
  - fails when the branch contains `.aide-fake/fail-build`
  - fails a backend pre-deploy when the branch contains `.aide-fake/fail-migration`

### 4.21 Managed databases and migrations

- **Database states:** `provisioning → available | error`, plus `paused` (Supabase `INACTIVE`), `expiring` (Render free, within 7 days of `expiresAt`) and `removed`. Polling every 10 s, with a 10-minute ceiling (`DATABASE_PROVISIONING_TIMEOUT`).
- **Supabase** (`https://api.supabase.com/v1`, scoped PAT, 120 requests/min per user per endpoint; 429 with `X-RateLimit-Reset`):

  | Step | Call |
  | --- | --- |
  | Validate | `GET /organizations`, `GET /organizations/{slug}/projects` (401 invalid; 403 `missing_permissions` → `insufficient_scope`) |
  | Create | `POST /projects {name, organization_slug, db_pass, region_selection: {type: "specific", code} \| {type: "smartGroup", code: americas\|emea\|apac}}` (`plan`, `region` and `organization_id` are deprecated and never sent) |
  | Wait | `GET /projects/{ref}` until `ACTIVE_HEALTHY` (`INIT_FAILED` → error), then `GET /projects/{ref}/health?services=db,pooler,auth,rest`, all `ACTIVE_HEALTHY` |
  | Keys | `GET /projects/{ref}/api-keys?reveal=true` (`type` legacy/publishable/secret); `POST /projects/{ref}/api-keys {type, name}` when missing |
  | Connection | `GET /projects/{ref}/config/database/pooler` → session (`:5432`) and transaction (`:6543`) pooler host and user (the `aws-<n>-<region>` host can't be derived). Password = the AIDE-generated `db_pass` (reset with `PATCH /projects/{ref}/database/password` when linking a project whose password AIDE doesn't hold) |
  | Auth URLs | `PATCH /projects/{ref}/config/auth {site_url, uri_allow_list}` (comma-separated) |
  | Advisors | `GET /projects/{ref}/advisors/security` after each migration run |
  | Paused | `INACTIVE` → the card shows "Paused". The Owner action calls `POST /projects/{ref}/restore` |

  Required scoped-token permissions: Projects RW, Database RW, Connection Pooling R, API Keys R, **API Key Secrets R**, Auth Config RW, Data API Config RW (and Organization Projects R for the picker).
- **Render Postgres:** `POST /v1/postgres {name, ownerId, plan, version: "17", region (= the backend's), databaseName}`. Poll `GET /v1/postgres/{id}` until available. `GET /v1/postgres/{id}/connection-info` → `internalConnectionString` for services in the same region and workspace (`externalConnectionString` only when the consumer is elsewhere). The preview database is a second instance named `<name>-preview`.
- **Railway Postgres:** a Postgres service with a volume in each environment. The production environment's service is copied into `aide-preview` (see the §11 check for the creation call shape). The app references it as `DATABASE_URL = ${{Postgres.DATABASE_URL}}` (private network). The data pack's `Production notes` cover converting it for .NET and Java.
- **Migration runner (Supabase BaaS only, D-V4-12):** AIDE's own Npgsql runner, over the **session pooler**.
  - It applies the pending `supabase/migrations/<version>_<name>.sql` files of a commit in version order, each in a transaction.
  - It records `(version, name, statements)` in `supabase_migrations.schema_migrations`, the table the Supabase CLI uses, so a later `supabase db push` sees a consistent history.
  - It never edits or re-applies a recorded version. A recorded version whose file changed or disappeared is `MIGRATION_HISTORY_DIVERGED`: in production the run fails, and in preview it means the FR-148 reset.
  - Runs are recorded (`database_migration_run`) with files, durations and errors.
  - The Management API migrations endpoint isn't used (it assigns its own versions), and neither is the Beta query endpoint.
- **When migrations run:**
  - **Preview:** during preview rollout, before the backend and frontend previews (FR-147).
  - **Production, Supabase BaaS:** in the release job, **after merge readiness passes and immediately before AIDE merges** (`aide_merges`), or when the release enters `awaiting_merge` (`manual`). This is because platforms auto-deploy the default branch on merge.
    - The `migration_backward_compatible` review check (§4.18) is what makes "schema before code" safe.
    - If a PR is closed without merging after its migrations were applied, the release records "migrations applied, code not merged", and the next version must keep those files (they're on the branch).
  - **Production, host mode:** on the platform, as part of the backend's (or the serverless build's) deployment of the merge commit (FR-154).
  - **Unmerged migrations never run against a production database**, in any mode.
- **SQL Server / External:** AIDE stores and syncs the connection string only. Migrations run on the platform, as in host mode.

### 4.22 Environment inventory and derived variables

| Variable | Applies to | Value | Environments |
| --- | --- | --- | --- |
| `DATABASE_URL` | backend (or the Next.js full-stack app) | the database's URI: Render `internalConnectionString`, Railway `${{Postgres.DATABASE_URL}}`, the Supabase pooler (transaction `:6543` with `?pgbouncer=true`/`prepare=false` guidance for serverless hosts, session `:5432` otherwise), or the External value | per environment's database |
| `DIRECT_URL` | same, when the data pack's `migrate.needsDirectUrl` (Prisma; Drizzle on serverless) | the Supabase session pooler `:5432`, or the same as `DATABASE_URL` | same |
| `AIDE_RUN_MIGRATIONS` | serverless hosts in host mode | `true` where that environment has its own database, else `false` | per environment |
| `<prefix>SUPABASE_URL`, `<prefix>SUPABASE_PUBLISHABLE_KEY` | frontend (public; prefix from the frontend pack: `NEXT_PUBLIC_`, `VITE_`; Angular reads `SUPABASE_URL`/`SUPABASE_PUBLISHABLE_KEY` at build into `assets/config.json`) | `https://<ref>.supabase.co`, `sb_publishable_…` | per environment's Supabase project |
| `SUPABASE_URL`, `SUPABASE_SECRET_KEY` | server code only (Next.js server, backends), sensitive, only when the spec needs admin actions (pack rule) | `sb_secret_…` | same |
| `API_ORIGIN` | frontend, origin model `proxy` | the backend target URL | production / preview |
| `API_BASE_URL` (`VITE_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, Angular config) | frontend, `cross-origin` | the backend target URL | production / preview |
| `BACKEND_URL` | `fe-nextjs` ui mode (server-side calls) | the backend target URL | production / preview |
| `CORS_ALLOWED_ORIGINS` | backend, `cross-origin` | the frontend production URL, plus the frontend host's preview URL pattern | production / preview |

Derived values are recomputed when a target URL or database changes, and are then synced (FR-145). User variables can't override derived keys (`VALIDATION`). The Owner can change the public prefix only by changing the frontend pack.

### 4.23 Preset migration *(replaces v2 §4.12)*

- **Composition per preset:**
  - `nextjs-supabase` → `{frontend: fe-nextjs @ ".", backend: null, data: data-supabase (baas), vcs: vcs-github (from github_app, github_pat or local), deploy: {frontendHost: vercel if the project had Vercel settings, else null; databaseHost: supabase (unlinked); local: none}, originModel: same-origin}`
  - `nextjs-frontend` → the same, with `data: null` and no database host
- **Env vars:** existing v2 env vars (`NEXT_PUBLIC_SUPABASE_URL`, `NEXT_PUBLIC_SUPABASE_ANON_KEY`, `SUPABASE_SERVICE_ROLE_KEY`) stay as `user` variables. Linking a Supabase connection later adds the derived publishable/secret variables, and the UI suggests deleting the legacy ones.
- **Agent keys:** v2 agent keys → v4 specialists, for plans locked before the upgrade and for history display:

  | v2 key | v4 key |
  | --- | --- |
  | `project-scaffolder` | `scaffolder` |
  | `db-engineer` | `data-engineer` |
  | `domain-logic-engineer`, `api-engineer`, `ui-engineer` | `frontend-engineer` (Next.js full-stack owns domain, route handlers and UI) |
  | `test-engineer` | `test-engineer` |
  | `devops-engineer` | decided per step by its declared files: `ci-engineer` for `.github/**`, else `deploy-engineer`. A step with both → `deploy-engineer`, with a warning |
- **Owned paths:** owned-path checks for migrated in-flight plans use the v2 preset's globs (kept as a frozen mapping in `Stack/legacy/`) until the plan is replaced, so a locked plan doesn't suddenly produce `AGENT_SCOPE` warnings.
- One EF migration performs the data conversion, with the backfill SQL reviewed in PROGRESS.md.

### 4.24 LLM providers, model catalog and output limits

- **Enablement:** `Llm:Providers:<P>:Enabled` (bool, default `true`), with `<P>` one of `Anthropic`, `OpenAI`, `Ollama`, `Fake`. The pure rule `LlmProviderRules.Available(enabled, configured, userConnections, allowSystemKeys)` gives `usable` and `reason` for FR-158:
  - Ollama and Fake are `keyless`
  - Anthropic and OpenAI are `system_key` when a system key exists and `AllowSystemKeys` is on, `own_connection` when the user has a connection, else `needs_connection`
- **Default for a new project** *(amends `ProjectSettingsRules.DefaultForNewProject`)*: the configured `DefaultProvider` if it's enabled and usable by the creating user. Otherwise the first enabled usable provider in the order Anthropic, OpenAI, Ollama, Fake. Otherwise `LLM_PROVIDER_UNAVAILABLE`. A disabled provider is never chosen.

  | Enabled | User can use | DefaultProvider | Result |
  | --- | --- | --- | --- |
  | all four | none needing a key | anthropic | Ollama |
  | Anthropic, OpenAI (Railway) | Anthropic connection | anthropic | Anthropic |
  | Anthropic, OpenAI (Railway) | none | anthropic | `LLM_PROVIDER_UNAVAILABLE` |
  | all four (CI) | — | fake | Fake |
- **Model catalog:** `Llm:Providers:<P>:Catalog:<n>:{Id, Name, MaxOutputTokens, ContextTokens, Recommended}`, shipped in `appsettings.json`. Operators extend it per deployment through config. The shipped entries:

  | Provider | Id | MaxOutputTokens | ContextTokens | Notes |
  | --- | --- | --- | --- | --- |
  | Anthropic | `claude-opus-5-5` | 128,000 | 1,000,000 | recommended, default |
  | Anthropic | `claude-sonnet-5-5` | 128,000 | 1,000,000 | idea refinement default |
  | Anthropic | `claude-haiku-5-5` | 128,000 | 1,000,000 | |
  | Anthropic | `claude-fable-5-1` | 128,000 | 1,000,000 | |
  | Anthropic | `claude-opus-5` | 128,000 | 1,000,000 | |
  | Anthropic | `claude-sonnet-5` | 128,000 | 1,000,000 | |
  | Anthropic | `claude-haiku-4-5` | 64,000 | 200,000 | |
  | OpenAI | `gpt-6-astra` | verify at build (§11 Q5) | verify | default |
  | OpenAI | `gpt-6.1-sol` | verify at build (§11 Q5) | verify | idea refinement default |
  | Ollama | `qwen2.5-coder` | 16,000 | 32,768 | default |
  | Fake | `fake-1` | 128,000 | 1,000,000 | |
- **Effective output limit** per call, by the pure rule `LlmOutputLimits.Resolve(provider, model, catalog, discovered, ceiling, fallback)`:
  1. `limit` = the catalog entry's `MaxOutputTokens`. Else, for Anthropic only, the `max_tokens` reported by `GET /v1/models/{id}` (cached per model for 24 h; a lookup failure is ignored). Else `fallback` = `Llm:MaxOutputTokens` (kept, default 16,000; now the fallback for unlisted models).
  2. When `Llm:MaxOutputTokensCeiling` is set (optional, ≥ 1), `limit = min(limit, ceiling)`.

  | Model | Catalog | Discovered | Ceiling | Result |
  | --- | --- | --- | --- | --- |
  | `claude-opus-5-5` | 128,000 | — | — | 128,000 |
  | `claude-opus-5-5` | 128,000 | — | 32,000 | 32,000 |
  | `claude-haiku-4-5` | 64,000 | — | — | 64,000 |
  | `claude-new-x` (unlisted) | — | 96,000 | — | 96,000 |
  | `claude-new-x` (unlisted, lookup fails) | — | — | — | 16,000 |
  | `llama3.3` on Ollama (unlisted) | — | n/a | — | 16,000 |
- **Continuations** (FR-162; replaces `MaxContinuations = 4` in `LlmToolLoop`):
  - `MaxContinuations = 5`. A call stopping with `max_tokens` or `length` and non-empty text is continued.
  - The continue prompt is the existing `ContinuePrompt` (no assistant prefill).
  - Joining keeps `JoinContinuation`: drop the longest overlap of 20–2,000 characters, else concatenate.
  - **Stopping early:** before each continuation, if the estimated input tokens (original input plus the answer so far, at 4 characters per token) would exceed `ContextTokens − limit`, stop and warn `output_truncated`.

  | Stop reasons per call | Calls | `continuations` | Warning |
  | --- | --- | --- | --- |
  | `end_turn` | 1 | 0 | — |
  | `max_tokens`, `max_tokens`, `end_turn` | 3 | 2 | — |
  | `max_tokens` × 6 | 6 | 5 | `output_truncated` |
  | `max_tokens` (context nearly full) | 1 | 0 | `output_truncated` |

## 5. Data model

v1 §5 and v2 §5 apply. Changes (all snake_case, UUIDv7, UTC):

| Entity | Key fields | Constraints / indexes | Notes |
| --- | --- | --- | --- |
| release_version (changed) | + `stack_composition jsonb?`, `catalog_version?`, `catalog_hash?`; `preset_id/version/hash` kept (legacy, read-only) | | The composition is validated by the Domain on write |
| project (changed) | + `stack_packs text[]` (denormalised pack ids of the current version) | GIN index on `stack_packs` | List filter `?pack=` replaces the preset filter |
| execution_step (changed) | `agent_key` values are v4 keys; + `legacy_agent_key?` | | |
| agent_run (changed) | + `packs jsonb` (`[{id, version, sections[]}]`), `catalog_version?`; `preset_*` kept | | `maxOutputTokens` and `continuations` live in the run output JSON *(build decision 2026-10-25: no M23 migration)* |
| **git_host_instance** (new) | id, kind (`gitlab`/`bitbucket`), name, base_url (null for Bitbucket and gitlab.com), oauth_client_id, oauth_client_secret_encrypted, allow_insecure_http, enabled, version?, created_by, created_at | unique (kind, base_url) | Env config overrides rows with the same base URL |
| **user_external_login** (new) | id, user_id, provider (`github`/`gitlab`/`bitbucket`), instance_id?, external_user_id, login, created_at | unique (provider, instance_id, external_user_id) | `users.github_id` is backfilled here (kept for v2 code paths until removed) |
| **git_connection** (new) | id, user_id, provider (`gitlab`/`bitbucket`), instance_id, auth_kind (`oauth`/`token`), external_user_id, login, access_token_encrypted, access_expires_at?, refresh_token_encrypted?, token_email_encrypted? (Bitbucket API token), scopes text[], expires_at?, status (`valid`/`invalid`/`insufficient_scope`), last_validated_at, updated_at, `xmin` | unique (user_id, provider, instance_id, external_user_id) | Refresh rotation updates are concurrency-checked. `github_connection` (v2) is unchanged |
| project_git_settings (changed) | provider adds `gitlab`, `bitbucket`; + `instance_id?`, `git_connection_id?` (FK set null), `bound_by_user_id?`, `external_repo_id?`; `repo_full_name` holds `group/sub/project` or `workspace/slug` | check: gitlab/bitbucket need instance_id, external_repo_id, repo_full_name | |
| **platform_connection** (new, replaces `vercel_connections`) | id, user_id, provider (`vercel`/`netlify`/`cloudflare`/`render`/`railway`/`supabase`), label, secret_encrypted, last4, account_id?, account_name?, metadata jsonb (team/workspace/org ids, token policies), scopes text[], expires_at?, status (`valid`/`invalid`/`insufficient_scope`/`unchecked`), last_validated_at?, created_at | index (user_id, provider) | v2 rows are copied with the same id, so FKs survive. `vercel_connections` is dropped in the same migration |
| **deployment_target** (new, replaces `project_deployment_settings`) | id, project_id, component (`frontend`/`backend`), provider, connection_id? (FK set null), bound_by_user_id?, resource_id (Vercel project / Netlify site / Worker / Pages project / Render service / Railway project+service), preview_resource_id? (Render preview service / Railway environment / Workers preview trigger), name, root_directory, build_config jsonb, production_url?, preview_url?, status (`not_configured`/`configuring`/`ready`/`attention`/`error`), status_reason?, updated_at, `xmin` | unique (project_id, component) | v2 settings rows become `frontend` targets |
| **managed_database** (new) | id, project_id, environment (`production`/`preview`), provider (`supabase`/`render`/`railway`/`external`/`fake`), connection_id?, resource_id?, region?, plan?, status, connection_encrypted (URIs, keys, pooler config as JSON), db_password_encrypted?, expires_at?, created_at, updated_at, `xmin` | unique (project_id, environment) | |
| **database_migration_run** (new) | id, project_id, managed_database_id, release_id?, release_version_id, git_sha, environment, status (`running`/`succeeded`/`failed`), applied jsonb (`[{version, name, ms}]`), error_code?, error_message?, started_at, finished_at? | index (project_id, started_at) | Supabase BaaS runner only |
| deployment_env_var (changed) | + `applies_to text[]` (`frontend`/`backend`), `public bool`, `source` (`user`/`derived`), `derived_from?` | unique (project_id, key) unchanged | Derived rows are written by AIDE only |
| deployment (changed) | + `target_id`, `component`, `environment` (replaces the `target` preview/production naming; v2 values kept), `kind` (`deploy`/`rollback`/`promote`), `rollout_id?` | unique (release_id, component) where environment = production | |
| release (changed) | + `migration_run_id?`, `release_notes text` (always stored; the Bitbucket notes surface), `host_release_url?` | | `github_release_url` is renamed to `host_release_url` in the migration |

LLM settings need no schema change. `project_llm_settings` already holds provider, model, agent models and the bound credential. Provider enablement and the model catalog are configuration.

Migration notes:
- One migration per milestone: catalog/presets, GitLab+Bitbucket, platforms+targets, databases. No committed migration is edited (the latest today is `20261017171130_VercelDeployments`).
- The presets migration and the `vercel_connections`/`project_deployment_settings` replacement aren't safe for rolling updates. Kubernetes uses the Recreate strategy (v2 precedent). Railway runs one API replica and migrates in pre-deploy (v3 FR-109), so it's safe there too.

## 6. API

v1 §6 and v2 §6 apply. All new routes are under `/api`, with a ProblemDetails `errorCode`. The contract (`backend/openapi/api.json`) and the generated client are regenerated.

**Breaking-change decision (D-V4-4).** These change:
- The preset endpoints and fields are removed: `/api/presets`, `PATCH /projects/:id/preset`, `presetId` on create and on `POST /versions`, and the `?preset=` list filter.
- The Vercel connection endpoints move to `/api/me/connections/platforms`.
- `/settings/deployment` changes shape to targets + databases.
- Vercel-specific error codes are replaced by provider-neutral ones: `VERCEL_GIT_NOT_CONNECTED` → `PLATFORM_GIT_NOT_CONNECTED`, `VERCEL_PLAN_LIMIT` → `PLATFORM_PLAN_LIMIT`, `VERCEL_ERROR` → `PLATFORM_ERROR`.

The web app and CLI ship together (D-V2-11 precedent). Record the `oasdiff` result in PROGRESS.md.

| Method | Route | Body / query | Response | Errors (code → status) | Auth |
| --- | --- | --- | --- | --- | --- |
| GET | /api/stack/catalog | — | layers, options (chips, source, modes), recommendations, catalog version | — | user |
| POST | /api/stack/options | partial composition | per-layer options with `state` (`allowed`/`caveat`/`forbidden`/`invalidated`) and `reasons[]` | `VALIDATION → 400` | user |
| POST | /api/stack/validate | composition | normalised composition + derived (agents, sections, env names, origin model) | `STACK_INCOMPATIBLE → 400` | user |
| POST | /api/stack/detect | git {provider, instanceId?, repo, ref?} | suggested composition with evidence | `GIT_NOT_CONNECTED → 409`, `NOT_FOUND → 404` | user |
| GET / PUT | /api/projects/:id/stack | composition | composition (GET: any member) | `STACK_INCOMPATIBLE → 400`, `STACK_LOCKED → 409`, `VCS_MISMATCH → 400` | owner |
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
| POST | /api/projects/:id/deployments/:deploymentId/rollback (renamed from /promote; /promote kept as an alias for Vercel) · /redeploy | — | 202 deployment | `INVALID_DEPLOYMENT → 409`, `DEPLOYMENT_ACTION_UNSUPPORTED → 409` | owner |
| PATCH | /api/projects/:id/settings/git (changed) | + connectionId (rebind, GitLab/Bitbucket); mergeStrategy adds `fast_forward` | section | `CREDENTIAL_NOT_OWNER → 403`, `GIT_NOT_CONNECTED → 409`, `FORBIDDEN → 403` | owner |
| GET | /api/llm/providers (new) | — | enabled providers: `usable`, `reason`, `isDefault`, connections, catalog (id, name, maxOutputTokens, contextTokens, recommended), defaultModel | — | user |
| PATCH | /api/projects/:id/settings/llm (changed) | as v2 | section (providers filtered to enabled) | + `LLM_PROVIDER_UNAVAILABLE → 400` | owner |
| POST | /api/projects (changed) | − presetId; + stack (composition); + llm {provider, credentialId?, defaultModel, modelsByAgent?}; git {mode: github_app\|github_pat\|gitlab\|bitbucket\|local, instanceId?, connectionId?, repoId?, repoFullName? …} | 201 project | + `STACK_INCOMPATIBLE`, `VCS_MISMATCH`, `GIT_NOT_CONNECTED`, `LLM_PROVIDER_UNAVAILABLE`, `CREDENTIAL_NOT_OWNER` | user |
| POST | /api/projects/:id/versions (changed) | − presetId; + stack? (omitted = keep the base version's, re-pinned to the current catalog) | 201 | + `STACK_INCOMPATIBLE` | owner |
| GET | /api/admin/stack | — | catalog with pack sources, versions, hash, load status | — | admin |

Notes:
- **New run and job failure codes:** `GIT_NOT_CONNECTED`, `MIGRATION_FAILED`, `MIGRATION_HISTORY_DIVERGED`, `DATABASE_PROVISIONING_FAILED`, `DATABASE_PROVISIONING_TIMEOUT`, `PLATFORM_ERROR`, `PLATFORM_RATE_LIMITED`, `GITLAB_RATE_LIMITED`, `BITBUCKET_RATE_LIMITED`, `LLM_PROVIDER_UNAVAILABLE`.
- **New plan codes:** `STACK_CHANGE_UNPLANNED` (error), `UNDECLARED_GENERATED_FILE` (warning).
- **New run warning:** `output_truncated`.
- Every new error code and warning gets frontend copy in the one error map (v1 rule 4; warnings in `run-format.ts`).
- Connection and Admin integration mutations require a browser session (v2 rule).

### Error codes (new in v4)

| Code | Status | Meaning | User-facing copy |
| --- | --- | --- | --- |
| `STACK_INCOMPATIBLE` | 400 | Composition breaks a compatibility rule | "These stack choices don't work together: {reasons}." |
| `STACK_LOCKED` | 409 | Stack change after the plan is locked | "The stack can't change after the implementation plan is locked. Start a new version to change it." |
| `VCS_MISMATCH` | 400 | VCS layer differs from the repo's host | "The version control choice must match the project's repository host." |
| `GIT_HOST_NOT_ALLOWED` | 400 | GitLab base URL fails the SSRF guard | "That address isn't allowed for a GitLab instance." |
| `GIT_AUTH_FAILED` | redirect | GitLab/Bitbucket OAuth failed or was denied | "Signing in with {host} didn't complete. Try again." |
| `GIT_NOT_CONNECTED` | 409 | No usable Git connection for the project or request | "Connect your {host} account (or ask an Owner to re-bind the project) to continue." |
| `CONNECTION_SCOPE_INSUFFICIENT` | 400 | Token is missing scopes or has the wrong token type | "This token is missing permissions: {missing}." |
| `REPO_NAME_TAKEN` | 409 | Repo name/path/slug exists | "A repository with that name already exists." |
| `UPSTREAM_RATE_LIMITED` | 429 | Host or platform rate limit | "{provider} is rate limiting requests. Try again in a few minutes." |
| `PLATFORM_GIT_NOT_CONNECTED` | 409 | Platform can't access the repo (replaces `VERCEL_GIT_NOT_CONNECTED`) | "{platform} can't see this repository. Connect it in {platform}, then verify again." |
| `PLATFORM_PLAN_LIMIT` | 409 | Platform plan restriction (replaces `VERCEL_PLAN_LIMIT`) | "Your {platform} plan doesn't allow this. {detail}" |
| `PLATFORM_PERMISSION_MISSING` | 409 | GitHub App lacks Pages permission | "The AIDE GitHub App needs the Pages permission. Update it in GitHub settings." |
| `PLATFORM_ERROR` | 502 | Platform API failure (replaces `VERCEL_ERROR`) | "{platform} returned an error. Try again later." |
| `PLATFORM_RATE_LIMITED` | job code | Platform rate limit hit during a job | "{platform} is rate limiting deployments. AIDE will retry." |
| `GITLAB_RATE_LIMITED`, `BITBUCKET_RATE_LIMITED` | job code | Host rate limit during a job | "{host} is rate limiting requests. AIDE will retry." |
| `DEPLOYMENT_ACTION_UNSUPPORTED` | 409 | Provider can't roll back or promote | "{platform} doesn't support this action." |
| `PREVIEW_DATABASE_DIVERGED` | 409 | Preview DB has migrations the branch no longer has | "The preview database has migrations this branch no longer has. Reset the preview database to continue." |
| `MIGRATION_FAILED` | job code | Migration run failed | "A database migration failed: {detail}." |
| `MIGRATION_HISTORY_DIVERGED` | job code | Recorded migration changed or disappeared | "The database's migration history doesn't match the repository." |
| `DATABASE_PROVISIONING_FAILED` | job code | Provisioning failed | "Creating the database failed: {detail}." |
| `DATABASE_PROVISIONING_TIMEOUT` | job code | Not available within 10 minutes | "The database took too long to become available." |
| `STACK_CHANGE_UNPLANNED` | plan error | Stack change has no step for the new layer's specialist | "The plan must include work for the {layer} change." |
| `UNDECLARED_GENERATED_FILE` | plan warning | Generated file not declared | "{file} is generated from this change but no step declares it." |
| `LLM_PROVIDER_UNAVAILABLE` | 400 / run code | Provider disabled on this server or not usable by the user | "This AI provider isn't available here. Choose another provider or add a connection." |

## 7. Pages and UI

v2 §7 (design system, shell, routes) and the v3 Next.js mapping apply. Changes:

| Route | Page | Key content / states |
| --- | --- | --- |
| /projects/new | New project wizard *(amends)* | Source → Idea/Request → **Stack** (five sub-steps: Frontend, Backend, Data, VCS, Deployment; option cards with chips and a recommended badge; a collapsed "Not available with your choices (n)" list with reasons; caveat banners; a live summary card with the origin model and specialists) → **Repository** (connections of the chosen VCS: GitHub App/PAT, GitLab instance, Bitbucket; picker or create; Local) → Version → **Configuration** (approval and limits, plus the **AI model** section: provider, connection, model with max output tokens, Advanced per-agent models) → **Deployment** (bind connections per target and database; create or link; readiness preview) → Review & create. Import: Source → Repository (any host) → Stack (pre-filled by detection, VCS locked, "detected" chips) → … |
| /projects/:id/settings/stack | Stack *(new; replaces the preset section)* | The current composition per layer, with pack versions. "Change stack" (the same sub-steps) while unlocked. A locked state with the reason. A stack-change warning for iterations. The legacy preset note for migrated projects |
| /projects/:id/settings/git | Git *(amends)* | Host, instance, repo, mode. For GitLab/Bitbucket, the binding ("connected by Ana") and Rebind. Merge strategy options per host |
| /projects/:id/settings/llm | LLM *(amends)* | Providers limited to enabled ones. A "provider turned off on this server" state. The default model picker uses the catalog (with max output tokens) plus "Other model id…" |
| /projects/:id/settings/deployment | Deployment *(replaces)* | An origin model card. Target cards per component (provider logo, connection, resource link, URLs, status, readiness checklist with fix links). Database cards per environment (status, region, plan, expiry/pause warnings, Reset preview). The environment inventory table, with source badges (user / derived / required-missing) and component and environment columns |
| /projects/:id/review | Review *(amends)* | A preview rollout panel: one row per component (and the preview migration run), with state, URL, log tail and redeploy |
| /projects/:id/release | Release *(amends)* | The production rollout timeline (migration run → backend → frontend). The host release link (GitHub/GitLab), or the annotated tag with notes (Bitbucket) |
| /projects/:id/deployments | Deployments *(amends)* | Component and environment filters. Rollback per provider capability (hidden, with the reason, when unsupported) |
| /projects/:id/migrations | Migrations *(new; in the project nav when a Supabase BaaS database exists)* | Runs with environment, SHA, files, duration, status and error |
| /projects/:id/runs/:runId | Run details *(amends)* | The `output_truncated` warning copy (FR-162), the number of continuations, and the max output tokens used |
| /connections | Connections *(amends)* | Sections: LLM providers (enabled only) · Source control (GitHub, GitLab per instance, Bitbucket) · Deployment platforms (Vercel, Netlify, Cloudflare, Render, Railway) · Databases (Supabase). Each connection dialog shows exactly what to create on the provider (token type, scopes or permissions, expiry advice), with a link to the provider's token page |
| /login | Auth *(amends)* | "Continue with GitHub / GitLab (per enabled instance) / Bitbucket" |
| /admin/integrations | Admin *(amends)* | GitHub (v2) · GitLab instances (add/edit/disable; the callback URL and scopes to register; version and reachability) · Bitbucket consumer · LLM (enabled providers only) · **Stack catalog** (layers, packs, sources, versions, hash, load status; replaces the presets list) |

UI rules (in addition to v1–v3):
- Forbidden options are never selectable, and their reason is always visible. There are no hover-only reasons, for touch and screen readers.
- The wizard keeps no compatibility logic of its own. It renders `/api/stack/options` (D-V4-19).
- Provider logos are inline SVG components (no external image hosts; the v3 CSP stays `img-src 'self' data:`).
- Every target and database card shows whose connection it uses ("connected by …").
- The wizard never preselects a provider the user can't use. "Add a connection" opens Connections in the same tab and returns to the wizard with its state kept (session storage).
- Devices and accessibility are unchanged: desktop-first, usable at 375 px, WCAG 2.2 AA.

## 8. Non-functional requirements

v1–v3 §8 apply, plus:
- **Security:**
  - **New secrets** are encrypted with Data Protection (a purpose per secret kind), never returned, never logged, never put in LLM prompts, and masked in build-log tails (the v2 rule, extended to every provider). That covers GitLab/Bitbucket OAuth client secrets and user tokens, platform tokens, the Supabase PAT, database passwords and connection strings, and API keys. On Railway, the `/data` volume backup note (v3 FR-112) now also covers these.
  - **SSRF:**
    - Only Admin-registered GitLab instances are configurable hosts.
    - Base URLs must be `https` (unless `allow_insecure_http`).
    - The resolved addresses are checked **on every connection** (DNS-rebinding guard). Always refused: loopback, link-local (169.254.0.0/16, fe80::/10), metadata (`fd00:ec2::254`) and `0.0.0.0`.
    - Private ranges (10/8, 172.16/12, 192.168/16, fc00::/7, and `*.railway.internal`) are allowed only when `GitLab:AllowPrivateNetworks=true`. It's on for Docker Desktop Kubernetes and the dev loop (self-managed GitLab often lives there), and off on Railway, where private addresses are AIDE's own services.
    - Cross-host redirects aren't followed.
    - Git runs with `protocol.allow=never` and `protocol.https.allow=always` (plus `http` only for insecure instances).
    - Tokens are bound to the instance that issued them and never sent to another host.
  - **OAuth flows** (GitLab, Bitbucket) use a signed, single-use `state` (the v2 manifest rule), and PKCE where supported (GitLab).
  - **Netlify links:** deploy keys AIDE adds for Netlify are read-only (`can_push: false`), and the webhooks point only at `https://api.netlify.com/hooks/<provider>`.
  - **Supabase keys:** secret keys, database URLs and passwords are never assigned to public variables (FR-145 rule). `<prefix>SUPABASE_PUBLISHABLE_KEY` is the only Supabase key a frontend gets.
  - **LLM providers:** a disabled provider can't be reached through any API path (FR-157). The Fake LLM provider is off on Railway.
- **Reliability:**
  - Setup, provisioning, rollout, migration and poll work are jobs in the v1 job table (outbox, leases, retries), and a restart resumes them.
  - Per-provider rate budgets (§4.19–§4.21) are enforced in one shared limiter keyed by connection. Rate-limit waits never count as failed attempts.
  - Migration runs are idempotent by `(managed_database_id, version)`. A crashed run resumes from the last committed file.
  - Streaming LLM calls hold one connection per running agent for up to `Llm:RequestTimeout`. The existing worker concurrency limits apply.
- **Performance:**
  - `POST /api/stack/options` p95 < 50 ms (pure, in memory).
  - The projects list p95 stays < 300 ms locally (< 500 ms on Railway, v3) with the `stack_packs` filter.
  - The deployment settings page p95 < 300 ms with two targets, two databases and 50 variables.
  - `GET /api/llm/providers` p95 < 100 ms (config plus one query).
- **Cost:** per-call output limits rise up to 8×. The existing run token budgets (`tokenBudget`) and cost recording still apply before every call, including continuations.
- **Accessibility:** the Stack step is keyboard navigable. Option cards are radio groups with `aria-describedby` reasons, and disabled options stay focusable so their reasons can be read. The axe checks cover the Stack step, the AI model section, Settings → Stack, Settings → Deployment and Connections.
- **Testing:**
  - Every rule in §4.17 has unit tests, including all the worked examples. An exhaustive test checks that every composition `/options` reports as allowed passes `/validate`, and vice versa, over the enumerated composition space.
  - Detection, skill resolution (including the heading-suffix matching), ownership expansion, composed plan rules, spec sections, review checks (each rule id: a hit and a near miss), env derivation, preset migration, the LLM availability rules, the output-limit rules and the continuation table all have unit tests.
  - GitLab, Bitbucket, Netlify, Cloudflare, Render, Railway, GitHub Pages and Supabase are tested with stub HTTP handlers per operation, including 401, 403-scope, 429 with reset, transient statuses (GitLab `checking`, the Bitbucket 202 merge task, 555) and provider state sequences.
  - LLM streaming is tested with stub SSE/NDJSON streams per provider: text, tool calls, usage, `max_tokens`/`length` stops, a stall (idle timeout), and an early end.
  - **No test calls a real provider API** (CLAUDE.md rule 10).
  - The migration runner is integration-tested against Testcontainers PostgreSQL 17 with a `supabase_migrations` schema: ordering, history rows, idempotent re-runs, divergence detection.
  - **E2E** (Fake LLM + Local Git + Fake deployments and databases):
    - create a **React + ASP.NET + EF Core** composition with Netlify/Render/Render Postgres hosts → preview rollout with both components → release → production rollout
    - a **Vue + Supabase BaaS** composition with a Fake migration run
    - the Stack step's filtering: pick ASP.NET, and see Prisma disabled with its reason
    - a migrated v2 preset project releasing a version
    - the wizard's AI model section choosing Fake and `fake-1`
  - CI runs E2E (gate per CLAUDE.md "Commit gate").
  - A backend integration test with `Llm__Providers__Fake__Enabled=false` asserts that Fake is absent from `/api/llm/providers` and refused on create.

## 9. Environments

| Environment | Where | Web URL | v4 notes |
| --- | --- | --- | --- |
| Dev loop | your machine | http://localhost:3000 | All LLM providers enabled. `GitLab:AllowPrivateNetworks=true`. Seed on |
| Local Kubernetes | Docker Desktop (`docker-desktop`) | http://localhost:8080 | As the dev loop. `deploy-local.sh --e2e` sets Fake LLM, deployments and databases |
| CI (GitHub Actions) | runners | — | Defaults: all providers enabled, `Llm__DefaultProvider: fake`, `Deployments__Provider: Fake`, `Databases__Provider: Fake`, `Deployments__AllowFake: "true"` |
| Railway `preview` / `production` | Railway | generated `*.up.railway.app` | `Llm__Providers__Ollama__Enabled=false`, `Llm__Providers__Fake__Enabled=false`, `GitLab__AllowPrivateNetworks=false`. Never any Fake deployment or database provider |

- **Optional secrets** (`secrets.env` locally; Railway variables remotely, listed in `deploy/railway/variables.md`): `GitLab__Instances__0__Name`, `__BaseUrl` (empty = gitlab.com), `__ClientId`, `__ClientSecret`; `Bitbucket__OAuth__ClientId`, `Bitbucket__OAuth__ClientSecret`. These override DB-registered entries with the same host.
- **New config:** `Llm__Providers__<P>__Enabled`, `Llm__Providers__<P>__Catalog__<n>__*`, `Llm__MaxOutputTokensCeiling`, `Llm__StreamIdleTimeout`, `Llm__RequestTimeout` (new default 30 min), `Databases__Provider`, `GitLab__AllowPrivateNetworks`. Add them to `secrets.env.example`, the README configuration table, `.railway/railway.ts` and `deploy/railway/variables.md`.
- **Callbacks** use `App:PublicBaseUrl` (v2): `<base>/api/auth/gitlab/callback`, `<base>/api/auth/bitbucket/callback`. GitLab treats `http://localhost` redirect URIs as "development" but accepts them (§11 check). On Railway they use the environment's web URL.
- **Egress:** the API reaches `gitlab.com`, Admin-registered GitLab hosts, `api.bitbucket.org`, `bitbucket.org`, `api.netlify.com`, `api.cloudflare.com`, `api.render.com`, `backboard.railway.com`, `api.supabase.com`, and the Supabase pooler hosts (`*.pooler.supabase.com:5432`) for migrations. Docker Desktop and Railway allow this by default, so nothing changes.
- **Fake providers:** `Deployments:Provider=Fake` (v2) applies to every deployment provider. `Databases:Provider=Fake` fakes provisioning and the migration runner (it records files without a database). Both need `Deployments:AllowFake=true` outside Development/Testing (v2 rule). The E2E overlay sets both.
- **Seed (dev):**
  - The released demo project (v2) gets the composition `fe-react-vite` + `be-aspnet` + `data-efcore-postgres` + `vcs-github` with Fake hosts, so "Start new version" shows the stack.
  - The v2 preset demo project ("nextjs-frontend in planning") is migrated like any preset project (§4.23), showing the retired-preset note.
  - A third demo project uses `fe-vue` + `data-supabase` (BaaS) with a Fake Supabase database, in `specification`.

## 10. Decisions and assumptions

v1 D1–D21, v2 D-V2-1 to D-V2-15 and v3 D-V3-1 to D-V3-13 stand, except where amended below.

| # | Decision / assumption | Source | Status |
| --- | --- | --- | --- |
| D-V4-1 | **Stack composition from stack-packs replaces presets.** One option per layer from a catalog adapted from stack-packs (5 frontends, 5 backends, 7 data packs, 3 VCS, 6 deploy targets), with compatibility rules from `compose-stack`. Why: any supported combination works with the same agents, and adding a stack means adding a pack folder. Presets are a subset of compositions, so no capability is lost | Ben (draft, 2026-10-18; v4 brief) | agreed |
| D-V4-2 | **Deployment is one layer with per-component hosts.** The stack-packs model allows split deploys, and a host's compatibility depends on the component it runs. So the Deployment step picks a frontend, backend and database host, plus the Compose dev loop (`local-docker-desktop`, as a skill only). The order stays Frontend → Backend → Data → VCS → Deployment | Ben (draft) | agreed |
| D-V4-3 | **Generic specialists plus pack skills, mirroring stack-core.** Domain logic moves to the backend engineer (or the frontend engineer for Next.js full-stack and BaaS apps). `stack-reviewer`/`stack-auditor` map to AIDE's review agent. Content is adapted, not copied (§4.16), as in D-V2-12. Section headings are matched after stripping a trailing parenthetical, because the current packs suffix some headings | Ben (draft); headings: stack-packs 2026-10-25 | agreed |
| D-V4-4 | **Presets removed with a data migration and a breaking API change.** Existing projects are converted to equivalent compositions, locked plans keep running through an agent-key map, and history keeps the preset labels. The preset endpoints and the Vercel-specific connection and error names go. The web app and CLI ship together | Ben (draft) | agreed |
| D-V4-5 | **Still no build or generator runner.** Generated files are hand-written by the owning specialist, or produced/checked by CI, and CI results feed review on every Git host (FR-127). A sandboxed runner remains an open question (§11) | Ben (draft) | agreed |
| D-V4-6 | **GitLab through an Admin-registered OAuth application per instance, with a PAT fallback. Background work runs as the binding Owner.** GitLab has no App manifest or installation tokens. Refresh tokens rotate. The `api` scope is unavoidable. Self-managed instances are allowed only from an Admin allowlist with SSRF checks | Ben (draft) | agreed |
| D-V4-7 | **Bitbucket Cloud through an OAuth consumer, with a scoped API-token fallback. No app passwords. Annotated tags instead of Releases.** The 1,000 requests/hour/user budget drives the per-connection limiter and the single mergeability-checks call | Ben (draft) | agreed |
| D-V4-8 | **Pasted, scoped tokens for every deployment platform and Supabase.** OAuth would need each self-hoster to register an app with every provider. This follows the v2 Vercel pattern: validated, encrypted, never returned, scope-checked where the API allows | Ben (draft) | agreed |
| D-V4-9 | **A platform must build from the repo's Git host. No local builds, uploads or CI-run CLI deploys.** Railway builds only from GitHub. Cloudflare doesn't build Bitbucket or self-managed GitLab. GitHub Pages needs GitHub. Only Netlify builds from self-managed GitLab. These combinations are incompatible (R9). stack-packs now documents CLI deploys from any VCS's CI (`compose-stack` line 67). That path was considered for v4 and deferred (§11) | Ben, v4 interview 2026-10-25 | agreed |
| D-V4-10 | **One persistent preview environment per project, with its own preview database**, rolled out in dependency order. Why: AIDE develops one version at a time on one branch. Render and Railway native PR previews copy the production database URL. **Migrations from unmerged code must never touch production** | Ben (draft) | agreed |
| D-V4-11 | **Platform APIs over repo config-as-code for resources.** Railway IaC is applied only by its CLI, legacy `railway.json` stops working on 2026-12-01, and Render Blueprints can't be created or synced through the API. So AIDE uses the APIs. Repo config files are used only where the platform reads them during its own build. `render.yaml` and `.railway/railway.ts` may be committed as documentation (the packs are IaC-first, and this is a deliberate deviation) | Ben (draft); updated for stack-packs 2026-10-25 | agreed |
| D-V4-12 | **Supabase becomes a connection and a managed database. BaaS migrations run through AIDE's own Npgsql runner** over the session pooler, recording CLI-compatible history. The new `sb_publishable_`/`sb_secret_` keys are used, because `anon`/`service_role` are being retired. Replaces D-V2-10 | Ben (draft) | agreed |
| D-V4-13 | **ORM migrations always run on the platform, gated per environment**: in pre-deploy (Render paid, Railway), at container start (Render free), or in the production build on serverless hosts when `AIDE_RUN_MIGRATIONS=true` | Ben (draft) | agreed |
| D-V4-14 | **Supabase BaaS production migrations run immediately before the merge**, with expand/contract enforced by `migration_backward_compatible` | Ben (draft) | agreed |
| D-V4-15 | **Cloudflare defaults to Workers with static assets and Workers Builds. Pages is only for existing projects.** Workers Builds builds are SHA-pinned and promote the exact previewed version. It needs a user token | Ben (draft); matches `deploy-cloudflare` | agreed |
| D-V4-16 | **Netlify with GitLab/Bitbucket is linked by AIDE with a read-only deploy key and a push webhook to Netlify** | Ben (draft) | agreed |
| D-V4-17 | **GitHub Pages is a production-only frontend host using the GitHub App**, with the Pages permission added to the manifest | Ben (draft) | agreed |
| D-V4-18 | **Sign-in with GitLab and Bitbucket.** Accounts link by provider identity (`user_external_login`), never silently by email | Ben (draft) | agreed |
| D-V4-19 | **Compatibility lives only in the Domain and is served to the UI.** One function powers the wizard, settings, API validation and the CLI | Ben (draft) | agreed |
| D-V4-20 | **A static SPA on Vercel is cross-origin only.** `vercel.json` rewrites have fixed destinations, so they can't point preview and production at different backend URLs. Netlify generates `_redirects` at build time instead. The `deploy-vercel` pack documents an `/api` rewrite for a single backend URL, which doesn't fit AIDE's preview environment | build (2026-10-25) | assumed |
| D-V4-21 | **LLM providers are enabled per deployment by configuration.** Railway disables Ollama (unreachable there) and Fake (testing only). Local runs and CI keep all four. A disabled provider is never a silent default | Ben, v4 brief 2026-10-25 | agreed |
| D-V4-22 | **The LLM provider, connection and model are chosen in the wizard's Configuration step**, not a new step. `POST /api/projects` gains an optional `llm` object, so API and CLI callers keep working | Ben, v4 brief; placement: build | assumed (placement) |
| D-V4-23 | **The model catalog is configuration.** It lists the known models with their max output and context tokens. Unlisted models: Anthropic limits come from the Models API, otherwise the 16,000 fallback. Live model listing is out of scope | build (2026-10-25) | assumed |
| D-V4-24 | **LLM calls stream.** 128,000-token outputs need minutes, and SDK guidance requires streaming at that size. The whole-call timeout goes to 30 minutes, with a 2-minute idle timeout. Replaces the v2 note "the cap stays at 16,000 because calls don't stream" | build (2026-10-25) | assumed |
| D-V4-25 | **Up to 5 continuations (6 calls) per agent answer, then `output_truncated`.** The corrective retry gets its own allowance. Ben may revisit this in a later version | Ben, v4 interview 2026-10-25 | agreed |

## 11. Open questions

| # | Question | Who can answer | Blocks |
| --- | --- | --- | --- |
| Q1 | A sandboxed build/generator runner, and CI-run platform CLI deploys (`railway up`, `wrangler deploy` from GitLab CI or Bitbucket Pipelines with a platform token stored as a CI secret)? Either would remove the R9 gaps and the hand-written generated files. Proposed: a later version | Ben | nothing in v4 |
| Q2 | Supabase branching (Pro plan) as the preview database for BaaS projects, instead of a second Supabase project? Proposed: later | Ben | nothing in v4 |
| Q3 | OAuth for Netlify, Cloudflare, Railway and Supabase, as "bring your own client" options? Vercel OAuth REST permissions (carried from v2)? Proposed: later | Ben | nothing in v4 |
| Q4 | Abandoning a version in development, and the v3 Q1 first-Admin guard on public deploys (carried). Proposed: still later | Ben | nothing in v4 |
| Q5 | Build-time check: the OpenAI output and context limits of `gpt-6-astra` and `gpt-6.1-sol` from OpenAI's model docs, and whether OpenAI streaming reports usage in the final chunk (`stream_options.include_usage`). Until verified, use 16,000 output tokens | build agents | FR-160 values only |
| Q6 | Refresh the catalog from a newer stack-packs snapshot each AIDE version, and add packs (Svelte, Go, Fly.io) through `author-pack`? Proposed: refresh per version | Ben | nothing in v4 |
| Q7 | Build-time checks against live accounts or docs (not questions for Ben; record the results in PROGRESS.md). See the list below | build agents | the named FRs |

**Q7 checks:**
- **Netlify:**
  - whether `POST /sites/{id}/builds?branch=aide/…` runs when the branch isn't in `allowed_branches` or an `ignore` rule matches (else use the build hook)
  - the API field that turns off Deploy Previews
  - the PAT expiry options
  - build-log access with a PAT
  - whether GitLab/Bitbucket linking by deploy key + webhook builds reliably
- **Cloudflare:**
  - bootstrapping the Worker before the first build
  - whether trigger branch filters apply to API-started builds
  - GitLab repo connections, end to end, through the API
  - the currently recommended Next.js adapter (`deploy-cloudflare` line 10)
- **Railway:**
  - `preDeployCommand` and `watchPatterns` in `ServiceInstanceUpdateInput`
  - whether `commitSha` must be on the connected branch
  - whether a copied environment gets a fresh Postgres and volume
  - the Postgres creation call
  - the full `DeploymentStatus` enum
- **Render:**
  - the Postgres status values after `creating`
  - the service URL field
  - whether a service can be created on a private repo before the Git provider is connected
- **Supabase:**
  - the `supabase_migrations.schema_migrations` columns
  - whether the pooler `connection_string` contains a placeholder
  - provisioning time
  - whether new projects still get legacy keys
- **GitLab:**
  - `http://localhost` OAuth redirect URIs on gitlab.com
  - whether fast-forward merges leave `merge_commit_sha` null
- **Bitbucket:**
  - whether `draft: false` through `PUT` marks a PR ready
  - refresh-token rotation
  - the consumer permission name for webhooks
  - the app-password removal date (the pack says 9 June 2026; the draft said 2026-07-28; neither changes behaviour)
- **GitHub Pages:** whether `POST /pages` with `build_type: workflow` needs Administration as well as Pages.
- **LLM streaming:**
  - the Anthropic `message_delta` usage fields
  - the OpenAI streamed tool-call deltas
  - Ollama `/api/chat` streaming with tools

## 12. Milestones

**M0 Baseline:**
- The repo, CI, Railway and the v3 app already exist. **Do not re-scaffold.**
- The v3 `SPEC.md` and `PROGRESS.md` are already in `docs/v3/`. Add the v4 row to CLAUDE.md's Versions table and work on `build/aide-v4`.
- Confirm CI is green on the baseline commit, and record the run URL in a new root `PROGRESS.md`. The gate runs on GitHub Actions (CLAUDE.md "Commit gate").

1. **M23 LLM providers and long outputs** (independent; first, because it's small and unblocks real use on Railway):
   - provider enablement and the Railway config
   - `GET /api/llm/providers`, the model catalog and output limits
   - streaming for all three HTTP providers
   - 5 continuations and the `output_truncated` constant and copy
   - the wizard AI model section, `llm` on create, and the CLI flags
   - FR-157–FR-162.
2. **M24 Stack catalog + compatibility:**
   - the catalog format and loader
   - the adapted pack and agent content from stack-packs (the 27 layer packs in scope, i.e. all but `local-docker-desktop-k8s`, plus the stack-core skills and agents)
   - `StackCompatibility` with the §4.17 rules and tests
   - `/stack/catalog`, `/stack/options`, `/stack/validate`, Admin → Stack catalog, the readiness check
   - FR-113, FR-116, FR-122.
3. **M25 Composition in projects + preset retirement:**
   - the composition on release versions, Settings → Stack, the wizard Stack step, `STACK_LOCKED`, stack changes, layer paths
   - the detector and import pre-fill
   - preset migration and removal, and the seeds
   - FR-114, FR-115, FR-117–FR-119, FR-129. Depends on M24.
4. **M26 Specialists across the lifecycle:**
   - skill resolution, ownership, composed spec sections, plan rules, Guardian protected paths
   - review checklists and deterministic checks, the generated-file policy
   - Fake scripted outputs per specialist
   - FR-120, FR-121, FR-123–FR-128. Depends on M25.
5. **M27 GitLab:**
   - instances (Admin, SSRF guard), OAuth + PAT connections, sign-in, picker and create
   - provider operations (push, MR, readiness, pipelines, merge, Releases, tags), import
   - FR-130–FR-132, and the GitLab parts of FR-136–FR-141. Depends on M25.
6. **M28 Bitbucket:**
   - the consumer, OAuth + API-token connections, sign-in, picker and create with an initial commit
   - provider operations (PR, mergeability checks, statuses, merge tasks, annotated tags), the rate limiter, import
   - FR-133–FR-135, and the Bitbucket parts of FR-136–FR-141. Depends on M27 (shared provider-neutral work).
7. **M29 Platforms I: connections, targets, frontends:**
   - generic platform connections (Vercel migrated), the deployment target model and migration
   - env inventory and derivation, the origin model, the readiness checklist
   - Netlify, Cloudflare (Workers Builds + Pages adopt), GitHub Pages, Vercel on the new model
   - FR-142–FR-146, and the frontend parts of FR-150 and FR-151. Depends on M25.
8. **M30 Platforms II: backends, databases, environments:**
   - Render, Railway, managed databases (Supabase, Render Postgres, Railway Postgres, External)
   - the Supabase BaaS migration runner and advisors, host-mode migration wiring
   - preview environment rollout, preview reset, production rollout
   - FR-144, FR-147–FR-154. Depends on M29.
9. **M31 Integrate & harden:**
   - events and notifications (FR-155), the CLI (FR-156), the E2E suites (§8), axe, perf
   - the README, `deploy/railway/variables.md`, STACK.md and SPEC sync
   - the spec-verifier over **v1, v2, v3 and v4 requirements**
   - Docker Desktop redeploy and smoke: manual checks for Ben

## Revision notes

- 2026-10-25: v4 spec created from `docs/v4/DRAFT_SPEC.md` (2026-10-18) and Ben's v4 brief.
  - **Renumbering:** draft FR-94–FR-137 → FR-113–FR-156, D-V3-1–19 → D-V4-1–19, milestones M17–M24 → M24–M31.
  - **Updated against stack-packs as of 2026-10-25:**
    - section headings per layer, and heading-suffix matching
    - the scaffolder, test-engineer and ci-engineer skill resolution (data packs have no `Gate`)
    - `.railway/**` and `CODEOWNERS` ownership
    - detection of `next.config.*`, the Pages workflow and legacy Railway config
    - the Cloudflare Next.js caveat text
    - the `no_committed_secrets` check
    - the origin-model vocabulary mapping
    - the Render/Railway IaC deviation (D-V4-11)
    - CLI-deploy deferral (D-V4-9)
  - **Hosting:** the draft assumed AIDE ran only on Docker Desktop. v4 adds the Railway settings (`GitLab:AllowPrivateNetworks`, provider enablement).
  - **New:** §3.12 and §4.24 (LLM enablement, wizard choice, per-model output limits, streaming, 5 continuations).
  - **Confirmed with Ben:** keep CLI deploys out (D-V4-9), keep the whole draft scope, and 5 continuations (D-V4-25).
