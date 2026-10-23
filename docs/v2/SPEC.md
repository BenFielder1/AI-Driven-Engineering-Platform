# AIDE Platform — Specification (v2)

2026-10-15 · Ben Fielder · **v2**, built on the v1 codebase in this repo. v1: [`docs/v1/SPEC.md`](docs/v1/SPEC.md), build log [`docs/v1/PROGRESS.md`](docs/v1/PROGRESS.md)

> **How this spec relates to v1.** v2 is an increment on the running v1 app, not a rewrite. **Everything in `docs/v1/SPEC.md` stays in force unless this document amends or replaces it.** That covers FR-1 to FR-52, §4 domain rules, §5 data model, §6 API, §8 NFRs and §9 deployment. Amended v1 items are listed in §3.0 and marked *(amends FR-n)* where they change. New requirements continue the numbering from **FR-53**, so every ID is unique across versions. v1 tests stay green unless an amendment changes the behaviour they assert; in that case, update the test and name the amendment in the test.

## 1. Overview

AIDE (AI-Driven Engineering Framework) turns an idea into released software through a governed six-phase lifecycle: **Refinement → Specification → Planning → Execution → Review → Release**. AI agents produce versioned artifacts, humans approve them at gates, and every state change is deterministic, logged and reversible.

v1 stops at the first release. **v2 makes AIDE a tool for the whole life of a product:**
- A released project can be **iterated**: start a new version (e.g. 1.2.0) and go round the lifecycle again on top of the existing code.
- **Existing GitHub repos can be imported**, so AIDE builds on software it didn't create.
- **Tech stack presets** bring specialised development agents into planning and execution, plus stack knowledge into specification and review. The two presets mirror Ben's `claude-kits` plugins.
- **Integrations are per-user:** each user connects their own GitHub (through a GitHub App), their own LLM provider keys and their own Vercel account.
- **The UI is redesigned** as a SaaS app with a side navigation, modelled on Space Worm Analytics. Each lifecycle stage gets its own page, and full execution and review history is visible.

Success means:
- A user can import a repo tagged `v2.3.0`, start `v2.4.0` with a feature request, and approve it through all six stages with a `nextjs-frontend` preset's specialist agents doing the work.
- They see a Vercel preview during review, release to a merged `v2.4.0` tag with a production deployment, and then start `v2.5.0`.

**In scope (v2):**
- UI redesign: design system, side-navigation app shell, AIDE logo, per-stage pages, execution-run and review-cycle history, artifact restore (§3.1)
- Release versions: semver versions with internal checkpoints; start a new version after release; release badges (§3.2)
- Repository context for agents: a Repository Analyst agent and read-only repo tools (§3.3)
- Import an existing GitHub repo as a project (§3.4)
- Tech stack presets: `nextjs-supabase` and `nextjs-frontend`, specialist agents per plan step (§3.5)
- User LLM connections: per-user provider keys bound to projects (§3.6)
- GitHub App integration: one-click registration, sign-in, repo picker, repo creation, installation tokens, merge readiness, GitHub Releases; the PAT stays as a fallback (§3.7)
- Vercel integration: per-user connection, project linking, preview deployments in review, production deployments on release (§3.8)
- CLI additions (§3.9)

**Out of scope (v2):**
- Agents with shell access, or agents running `gh`/`git` themselves (see D-V2-9).
- Provisioning Supabase or any other backend service. Supabase values are entered as project environment variables.
- Running the generated software's tests inside AIDE. Vercel preview builds and GitHub check runs are used as external signals instead.
- More presets (planned for later versions), GitLab, GitHub Enterprise Server, Sign in with Vercel (its API permissions are still in private beta; see §11), multi-tenant organisations, billing.
- Hosting AIDE itself anywhere other than Docker Desktop Kubernetes.

## 2. Users and permissions

The v1 roles are unchanged (`docs/v1/SPEC.md` §2). v2 adds:

| Role | Can (new in v2) | Cannot |
| --- | --- | --- |
| Admin | Register the AIDE GitHub App (manifest flow) and see its status; allow or forbid system LLM keys (`Llm:AllowSystemKeys`); view presets | See any user's credentials (they are never returned to anyone) |
| User | Manage their own **connections**: LLM provider keys, GitHub, Vercel. Pick or create GitHub repos they have access to. | Use another user's connection directly |
| Project Owner | Start a new version; import (as creator); choose the preset; bind **one of their own** LLM/Vercel connections to the project; edit deployment env vars; promote a production deployment; restore an artifact version (as a Writer) | Bind another user's connection (`CREDENTIAL_NOT_OWNER → 403`) |
| Project Contributor | Restore an older artifact version as a new draft (same rules as editing, FR-18); trigger a preview deployment | Start versions, bind connections, change the preset, edit env vars, promote deployments (403) |
| Project Viewer | View versions, deployments and the preset | Any mutation (403) |

Auth: v1 cookie auth, API tokens and antiforgery are unchanged. **GitHub sign-in moves to the AIDE GitHub App's user authorization flow** (PKCE) once an App is registered. The v1 OAuth App configuration still works for sign-in only, and only while no App is registered (D-V2-7). Connections are personal: a project uses an Owner's bound connection on that Owner's behalf, and other members see only "connected by <name>".

## 3. Functional requirements

### 3.0 Amended v1 requirements (summary)

| v1 | What changes | Where |
| --- | --- | --- |
| FR-2 | GitHub sign-in uses the GitHub App user flow when an App is registered | FR-80 |
| FR-5 | The wizard gains Source (new / import), Stack (preset), Version and Deployment steps; the repo can be picked or created | FR-73, FR-86 |
| FR-9 | Project LLM settings bind a user connection in place of a per-project key; the Git provider gains `github_app`; new deployment settings | FR-79, FR-82, FR-88 |
| FR-11, §4.1 | "Project version" is replaced by **release version (semver) + checkpoint** | §4.10 |
| FR-21 | The artifact detail page gains "Restore this version" | FR-58 |
| FR-28 | Spec sections depend on preset and iteration; requirement IDs are mandatory | FR-75, §4.13 |
| FR-29 | Plan steps carry `agent` and `covers` | FR-76 |
| FR-31 | The code generator is the step's specialist agent and may use repo tools | FR-71, FR-76 |
| FR-35/36 | The branch is per release version; the GitHub provider uses installation tokens | FR-82, §4.14 |
| FR-41 | The review page shows every cycle | FR-56 |
| FR-42/43 | The release tags the user's semver, creates a GitHub Release, and may wait for a manual merge | FR-64, FR-65 |
| FR-44/45 | Rollback is limited to checkpoints of the version in development | FR-63, §4.10 |
| FR-52 | New CLI commands | FR-93 |

### 3.1 UI redesign and lifecycle navigation

1. **FR-53** The app uses the v2 design system and app shell (§7.1): a collapsible side navigation, a sticky header with breadcrumbs, the ⌘K command palette, a notifications bell, the theme menu and the user menu. The AIDE logo comes from `docs/v2/brand/`. The v1 top-bar shell is removed.
   - Given a signed-in user at 1440 px, when any page loads, then the sidebar shows the AIDE lockup, the search trigger and the workspace nav, and the header shows breadcrumbs matching the route.
   - Given a 375 px viewport, when the user opens the menu, then the navigation opens as a left drawer and no horizontal scroll appears.
2. **FR-54** Inside a project, the sidebar switches to **project mode**:
   - The project switcher sits at the top. It shows the name and both version badges, and lists the user's projects.
   - A **Lifecycle** section has one item per stage. Each item shows its state (done ✓, current with a pulsing dot, upcoming muted, needs-action count for pending gates or drafts awaiting approval).
   - A **Project** section has Overview, Artifacts, Agent runs, Activity, Deployments (only when a deployment target is set) and Settings.
   - "← All projects" returns to the workspace nav.
   - The project header has a horizontal **stage stepper** with the same states, plus "Previous stage" and "Next stage" buttons. The primary action button ("Approve spec", "Start execution", …) shows the next action the user can take.
   - Given a project in `planning` with a pending `plan_approval` gate, when any project page loads, then Refinement and Specification show ✓, Planning shows the current dot and a "1" badge, and later stages are muted but still navigable.
3. **FR-55** There is **one page per stage**. Each shows the stage's artifact in full (rendered Markdown, with the latest and the current version marked), its status, approval actions, the stage's agent runs, gates and the stage-specific panels:

   | Stage | Route | Content beyond the artifact |
   | --- | --- | --- |
   | Refinement | `/projects/:id/refinement` | Request text, clarifying-question rounds (v1 FR-27), the Repository Analyst summary for iterations (FR-70) |
   | Specification | `/projects/:id/specification` | Section outline navigator; for iterations, a "Changes in this version" filter (new / changed / removed requirements, §4.13); preset spec guidance |
   | Planning | `/projects/:id/planning` | Step table: number, title, specialist agent, declared files, dependencies, covered requirement IDs; a dependency view; coverage warnings (FR-76) |
   | Execution | `/projects/:id/execution` | Execution runs (FR-57) |
   | Review | `/projects/:id/review` | Review cycles (FR-56), preview deployment (FR-90) |
   | Release | `/projects/:id/release` | Pre-release gate, merge readiness (FR-84), release attempts, tag, GitHub Release link, production deployment, "Start new version" (FR-60) |

   - A stage that hasn't started shows an empty state explaining what produces it and what has to happen first.
   - Every stage page accepts `?release=<semver>`, which shows that release version's history read-only (FR-62). *(decision 2026-10-15: `?release=`, not `?version=`, because artifact pages already use `?version=N` for the artifact version. The selection is kept across project navigation.)*
   - Given a project in `execution`, when the user opens `/projects/:id/specification`, then they see the locked spec with its version, approver and the run that produced it, and no approve or edit actions.
4. **FR-56** The Review page lists **every review cycle** of the selected release version, newest first. Each row shows cycle number, score, threshold, drift, decision and date.
   - Selecting a cycle shows its category breakdown (agent score, penalty, final, weight), violations with evidence, missing features, drift areas, deterministic findings, escalation reason, its optimisation plan and the execution run that plan produced.
   - The **compliance trend chart** stays: score per cycle against the threshold line. Clicking a point selects that cycle.
   - The default selection is the latest cycle. The selection is kept in the URL (`?cycle=N`).
   - Given three cycles, when the user selects cycle 1, then the breakdown shows cycle 1's category scores and the trend chart highlights point 1.
5. **FR-57** The Execution page lists **every execution run** of the selected release version: the **development run** (the implementation plan's steps) and one **review-fix run** per review cycle that re-entered execution (that cycle's optimisation plan's steps). Each run is a collapsible group.
   - **Group header:** plan type and version, cycle, status, progress (n / m completed), started/completed time, commits and tokens.
   - **Each step shows:** number, title, specialist agent, status, attempt count, declared vs produced files, commit link, and an expandable **attempt history**. Each attempt shows its agent run (model, tokens, duration), outcome, Guardian violations and warnings, and repo tool calls (FR-71).
   - The active run is expanded and updates live. Start/pause/resume (FR-37) applies to the active run.
   - Runs of invalidated plans stay listed under "Superseded" with the reason.
   - Given a development run of 8 steps and two review cycles with 3 and 2 fix steps, when the Execution page loads, then three runs are listed (8, 3 and 2 steps), each with its full attempt history.
6. **FR-58** On an artifact version older than the type's latest, a Writer can **Restore this version**. It creates a new `draft` with the old version's content (new version number, parent = the latest version, `restored_from_version` = the chosen version), which then follows the normal approval rules (FR-17). Restore is allowed only while:
   - the project is in the phase that produces that type (§4.12),
   - in the release version currently in development,
   - the project is not `releasing`,
   - the chosen version belongs to that release version and is not `corrupted`.
   
   Otherwise: `RESTORE_NOT_ALLOWED → 409` with a reason (`stage_completed`, `stage_not_reached`, `other_release_version`, `corrupted`, `releasing`). *(decision 2026-10-16: `stage_not_reached` means the project is before the producing phase, e.g. after a rollback.)* Restoring the current latest is `VALIDATION`. `ARTIFACT_RESTORED` is recorded.
   - Given spec v2 (locked) and v3 (locked, current), with the project still in `specification`, when a Writer restores v2, then spec v4 `draft` exists with v2's content and hash, parent 3, `restored_from_version` 2.
   - Given the project has advanced to `planning`, when anyone restores spec v2, then `RESTORE_NOT_ALLOWED` (`stage_completed`). The UI hides the action and explains: "Specification is complete. Roll back the project to change it."
7. **FR-59** The Artifacts page stays. It shows a card per type (latest version, status, integrity) for the selected release version, plus a version filter. Artifact detail (v1 FR-21) keeps history, diff and edit, and adds restore (FR-58) and the release version each version belongs to. Diff works across release versions (e.g. spec at 1.1.0 vs 1.2.0).
   - Given a project at 1.2.0 with specs from 1.1.0, when the user diffs spec v3 (1.1.0) against v6 (1.2.0), then the diff shows the changes between versions.

### 3.2 Release versions and iteration

8. **FR-60** When a project is `released` and its latest release is **merged** (FR-65), an Owner can **Start new version**. A dialog asks for:
   - the **new version** (semver, prefilled with the next minor),
   - the **request**: new features, improvements or fixes, as free text,
   - optionally a different preset (FR-74).
   
   On "Start development":
   - A release version row is created (`in_development`, base = the released tag and its merge commit).
   - The project goes to `refinement` / `active` at checkpoint 1 with the request as its idea.
   - `VERSION_STARTED` is recorded, and the Repository Analyst job is queued (FR-70).
   
   The button is disabled with a tooltip giving the reason when the project isn't released, its release is awaiting merge, or a version is already in development.
   - Given a project released as 1.1.0 (merged), when an Owner starts 1.2.0 with "Add CSV export", then the phase is `refinement`, the badges read "Released 1.1.0" and "In development 1.2.0", and a Repository Analyst run is queued.
   - Given the 1.1.0 release is `awaiting_merge`, when an Owner starts a version, then `RELEASE_NOT_MERGED → 409`.
9. **FR-61** Version validation (§4.10):
   - Invalid semver gives `VERSION_INVALID → 400`.
   - A version not greater than the latest released version gives `VERSION_NOT_GREATER → 409`.
   - A version whose tag already exists in the repo gives `TAG_EXISTS → 409`.
   - Starting while a version is in development gives `VERSION_IN_PROGRESS → 409`.
   - Given released 1.1.0, when an Owner enters `1.0.5`, then `VERSION_NOT_GREATER`. When they enter `v1.2.0`, then it is accepted and normalised to `1.2.0`.
10. **FR-62** Each project shows **two version badges** wherever the status badge appears (project header, sidebar project switcher, project list rows and cards, overview):
    - **Released** `<latest released version>` ("Not released" when none),
    - **In development** `<version>` (hidden when nothing is in development).
    
    The project list can filter on "has a version in development". A version switcher in the project header lists all release versions (in development, released, imported baseline). Picking a past version opens its stage pages, artifacts, runs and reviews read-only.
    - Given a project released at 1.1.0 and developing 1.2.0, when the projects list loads, then the row shows phase, status, "Released 1.1.0" and "In development 1.2.0".
11. **FR-63** The lifecycle **checkpoint** (v1's "project version") still increments on every forward phase transition within a release version. It is shown as "1.2.0 · checkpoint 4" in rollback, activity and run details. It does **not** increment on release completion. Rollback (v1 FR-44/45) targets only checkpoints of the release version in development. **A released version is closed:** no rollback into it, and the way forward is a new version (D-V2-3).
    - Given 1.2.0 at checkpoint 5 in `review`, when an Owner opens rollback, then the targets are checkpoints 1–4 of 1.2.0 only.
    - Given a released project with nothing in development, when an Owner calls `POST /rollback`, then `ROLLBACK_BLOCKED` with `reason: version_released`.
12. **FR-64** Release *(amends FR-42)* merges the version's PR (or the branch, for Local). It then:
    - creates an annotated tag `<tagPrefix><version>` (default prefix `v`, so `v1.2.0`) on the merge commit,
    - for GitHub, creates a **GitHub Release** with that tag and generated notes (the spec's "Changes in this version" plus the plan step list),
    - sets the release version to `released`, the project status to `released`, and the project's released version to this version.
    
    The v1 `v<N>-aide.<k>` suffix scheme is gone: if the tag exists on another commit, the release fails with `TAG_EXISTS` and nothing is merged. Idempotency is kept: a tag that already points at the merge commit is reused.
    - Given 1.2.0 approved for release, when the release job succeeds, then the PR is merged, `v1.2.0` and a GitHub Release exist, and the badges read "Released 1.2.0" with no in-development badge.
13. **FR-65** **Merge strategy** per project (`release.mergeStrategy`):
    - **`aide_merges`** (default): the release job merges as in FR-64.
    - **`manual`**: AIDE never merges. The release job marks the PR ready for review.
    
    In both cases, if the merge is refused (branch protection, required reviews or checks, missing permission), the release attempt becomes **`awaiting_merge`** and the project status `awaiting_merge`. Owners are notified with the PR link and the reasons from merge readiness (FR-84).
    - While awaiting, a **merge poll** job checks the PR (§4.14).
    - When the PR shows as merged, AIDE tags the merge commit, creates the GitHub Release and completes as in FR-64.
    - If the PR is closed without merging, the release fails with `PR_CLOSED`. The project stays in `release`, and an Owner can reopen the PR on GitHub and retry with `POST /release`.
    - Given branch protection requiring one approving review, when the release job tries to merge, then the release is `awaiting_merge`. After a reviewer approves and merges on GitHub, within one poll interval the tag exists and the status is `released`.
14. **FR-66** A new project created from scratch asks for an **initial version** (semver, default `0.1.0`). The released version is empty, and the first release tags it.
    - Given a new project with initial version 1.0.0, when it releases, then the tag `v1.0.0` exists and "Released 1.0.0" shows.
15. **FR-67** v1 projects are migrated (§4.10 "Migration"):
    - The i-th succeeded v1 release (i = 1…k, in order) becomes a `released` release version `0.i.0`, with its `v<N>-aide` tag recorded as-is.
    - A project that isn't currently `released` also gets an `in_development` version `0.(k+1).0`, which holds its current lifecycle state.
    - Checkpoints keep the v1 project version numbers.
    - Given a v1 project released once as `v7-aide`, when migrated, then it shows "Released 0.1.0 (tag v7-aide)" and can start a new version.

### 3.3 Repository context for agents

*(FR-68 and FR-69 are reserved and unused.)*

16. **FR-70** A **Repository Analyst** agent runs at the start of every release version that has existing code: every new version, and the first version of an imported project.
    - It reads the repo at the base commit through the repo tools (FR-71) and produces a `codebase_summary` artifact. Required sections: Overview, Stack & Dependencies, Structure, Key Modules, Data & APIs, Conventions, Tests & CI, Notable Risks.
    - The summary is agent-authored and **auto-locked** (no approval gate). It is an input to refinement, spec, plan, code generation and review, and the Refinement page shows it.
    - Refinement may start before the summary exists. If it does, the refinement run waits up to 10 minutes for the summary, then continues without it (the warning is recorded).
    - Given an imported repo, when version 2.4.0 starts, then a `codebase_summary` v1 `locked` exists before the first refinement round, and the refinement run lists it in its input artifacts.
17. **FR-71** Agents can call **read-only repo tools** through the provider's tool-use support:
    - `list_files(path, depth≤3)`
    - `read_file(path, startLine?, endLine?)` (≤ 400 lines or 64 KB per call)
    - `search_code(query, pathGlob?)` (plain-text or regex search, ≤ 50 matches)
    
    Rules:
    - The tools run against a **credential-free snapshot** of the working copy at the version's base commit. During execution and review they run against the project branch's working tree.
    - Limits: 25 tool calls and 400 KB read per run.
    - Path safety (v1 §4.6) applies. `.git/` and `.aide/` are hidden. Lines flagged by the secret scanner are redacted before they reach the model.
    - Every call is logged on the agent run (tool, arguments, bytes, truncated?).
    - Available to Repository Analyst, Refinement, Spec, Plan, Code generator and Code review.
    - Providers without tool support (an Ollama model that doesn't advertise it) fall back to context-only prompts, and a warning is recorded. The Fake provider scripts tool calls deterministically.
    - Given a refinement run on an imported repo, when the agent calls `read_file("../../etc/passwd")`, then the tool returns `UNSAFE_PATH` and nothing is read.
    - Given the 26th tool call in a run, then the tool returns `TOOL_LIMIT`, and the agent must answer with what it has.
18. **FR-72** The refinement agent of an iteration gets the request, the codebase summary, the previous released version's locked spec and project request, and the repo tools. Its clarifying questions should cover how the change fits the existing system: affected modules, compatibility and migrations.
    - Given an iteration request "add CSV export", when refinement runs, then the agent run's input artifacts include `codebase_summary` and the previous `technical_specification`.

### 3.4 Import an existing repository

19. **FR-73** The new-project wizard starts with **Source**:
    - **Start from scratch** (v1 flow plus FR-66), or
    - **Import an existing repository**, which asks for:
      - the repo, from the GitHub picker (FR-81) or a URL plus PAT (fallback, FR-83),
      - the default branch (prefilled from GitHub),
      - the **currently released version**: pick from the repo's GitHub Releases and semver tags (latest release preselected), or choose "Never released",
      - the **version to develop** (semver, greater than the released one, prefilled with the next minor),
      - the request: what to build in this version.
    
    On create:
    - The project starts with a release version row `imported` for the released tag (tag and commit SHA, no AIDE artifacts), plus one `in_development`.
    - It is in `refinement` at checkpoint 1, and the Repository Analyst runs (FR-70).
    - The first spec is **cumulative** (§4.13): reverse-engineered from the code and summary, plus the request.
    
    Errors:
    - The App isn't installed on the repo: `GITHUB_APP_NOT_INSTALLED → 409`, with the install link.
    - Tag not found: `TAG_NOT_FOUND → 400`.
    - Bad semver: `VERSION_INVALID`.
    - Version not greater: `VERSION_NOT_GREATER`.
    - Given a repo with releases `v2.2.0` and `v2.3.0`, when the user imports it, then `v2.3.0` is preselected, the version to develop defaults to `2.4.0`, and after creation the badges read "Released 2.3.0" and "In development 2.4.0".
    - Given an imported project, when execution prepares the working copy, then the branch `aide/<slug>/v2.4.0` is created from the default branch head.

### 3.5 Tech stack presets

20. **FR-74** A **preset** is a versioned, data-defined bundle shipped in the repo (§4.11): stack description, spec section guidance, plan rules, a catalogue of **specialist agents** (each with a prompt, the paths it owns and its skills), a review checklist, extra protected paths, required environment variables and supported deployment targets. v2 ships **`nextjs-supabase`** and **`nextjs-frontend`** (§4.12).
    - The project wizard has a **Stack** step: preset cards (name, description, stack chips, deployment targets) plus "No preset (general)". The preset is optional, and with no preset every v1 behaviour is unchanged.
    - The preset and its version are pinned on the release version. An Owner can change it until the version's first implementation plan is locked (`PRESET_LOCKED → 409` after that), and again when starting a new version.
    - `GET /api/presets` lists presets for the wizard and the admin page.
    - Given a project created with `nextjs-frontend`, when any agent runs, then the run records `preset: nextjs-frontend@<version>`.
    - Given the implementation plan is locked, when an Owner changes the preset, then `PRESET_LOCKED`.
21. **FR-75** With a preset, the **Spec Generator** gets the preset's stack description, its required extra sections and their guidance, and its conventions. Spec validation then also requires those sections (§4.13). Every spec, with or without a preset, must give each functional requirement an ID `FR-n` and at least one Given/When/Then acceptance criterion. A missing ID or criterion is a `MISSING_SECTIONS`-style failure (`MISSING_REQUIREMENT_IDS`) and gets the one corrective retry.
    - Given the `nextjs-supabase` preset, when the spec lacks "Data Model & RLS Policies", then the run is retried once with that section named.
22. **FR-76** The **Plan Generator** gets the preset's agent catalogue and plan rules. Each step carries:
    - `agent`: a catalogue key, or `generalist` with no preset,
    - `covers`: requirement IDs.
    
    Plan validation (§4.12, additive to v1 §4.6):
    - An unknown agent key or a missing `agent` is `INVALID_PLAN`.
    - A step whose declared files fall outside the agent's owned paths gets an `AGENT_SCOPE` **warning** (shown, not blocking).
    - Preset ordering rules are errors, e.g. `nextjs-supabase`: scaffold first on an empty repo; migrations before the API that uses them; a `db-engineer` step never shares a step with UI files.
    - New or changed requirements that no step covers are listed as an `UNCOVERED_REQUIREMENTS` warning.
    
    During execution, each step runs as its **specialist agent**: the base code-generator contract (v1 FR-31, structured output) plus that agent's prompt and skills. The Guardian adds the preset's protected paths. Agent runs record `agent_key`.
    - Given a `nextjs-supabase` plan whose step 3 is assigned `db-engineer` and declares `supabase/migrations/0002_tasks.sql`, when step 3 runs, then the agent run's agent is `code_generator` with `agent_key = db-engineer`, and its prompt contains the `supabase-migration` skill.
    - Given a plan step with `agent: "ml-engineer"` under `nextjs-frontend`, when the plan is validated, then `INVALID_PLAN` names the unknown agent.
23. **FR-77** With a preset, the **Code Review** agent also scores against the preset's review checklist. Each item maps to a v1 category (§4.7), and checklist findings are agent violations without penalties.
    - Deterministic checks gain preset rules, all language-independent text checks:
      - `nextjs-supabase`: every `create table` in `supabase/migrations/` has a matching `enable row level security`; a missing one is a critical security finding.
      - Both presets: `SUPABASE_SERVICE_ROLE_KEY` or any non-`NEXT_PUBLIC_` secret env name referenced in a `"use client"` file is a critical security finding.
    - Given a migration creating `tasks` without enabling RLS, when review runs, then a critical deterministic finding applies −0.20 to security.

### 3.6 User LLM connections

24. **FR-78** In **Account → Connections → LLM providers**, a user adds provider keys (Anthropic, OpenAI) with a label.
    - Keys are validated on save with a minimal authenticated call (the provider's list-models endpoint).
    - Keys are encrypted at rest and never returned: the API shows provider, label, last 4 characters, status (`valid`, `invalid`, `unchecked`) and last validation time.
    - The user can re-test, rename or delete a key.
    - A key the provider rejects with 401 during a run is marked `invalid`, the run fails with `CREDENTIAL_INVALID`, and its owner is notified.
    - Ollama (system base URL) and Fake need no key and are always listed as available.
    - Given a user adds an Anthropic key that the provider rejects, then `CREDENTIAL_INVALID → 400` and nothing is saved.
25. **FR-79** In project **Settings → LLM**, an Owner picks the provider and models (v1) and **binds one of their own connections** for that provider.
    - All of the project's runs use the bound key, whoever triggered them (D-V2-5).
    - Other members see "Anthropic · connected by Ana (••••a1b2)" and can't bind or unbind it.
    - If the binding Owner loses the Owner role, leaves the project or deletes the connection, the binding is cleared. Runs then fail with `PROVIDER_NOT_CONFIGURED`, and Owners are notified.
    - System keys from configuration are used only when `Llm:AllowSystemKeys=true` (default `false`) and no connection is bound.
    - v1 per-project keys migrate to a connection owned by the project's earliest Owner and stay bound.
    - Given an Owner binds their OpenAI key and a Contributor starts execution, then the runs use the Owner's key.
    - Given a Contributor calls `PATCH /settings/llm` with a `credentialId`, then `403`.
    - Given an Owner sends another user's `credentialId`, then `CREDENTIAL_NOT_OWNER → 403`.

### 3.7 GitHub integration

26. **FR-80** An Admin registers the **AIDE GitHub App** from **Admin → Integrations → GitHub** using the GitHub App **manifest flow**:
    - The Admin chooses a personal account or an organisation as the App's owner.
    - AIDE posts the manifest: name `AIDE (<host>)`, permissions per §4.14, callback `<public base URL>/api/auth/github/callback`, setup URL, webhook **inactive**.
    - AIDE exchanges the returned code (`POST /app-manifests/{code}/conversions`) and stores the App id, slug, client id/secret and private key **encrypted in the database**.
    
    The page then shows the App's status and its install link. Alternatively, the App can be configured from `secrets.env` (`GitHub__App__*`), which takes precedence. Once an App exists, "Continue with GitHub" uses its user authorization flow (PKCE). Accounts are matched by GitHub user id, so v1 linked accounts keep working. The v1 OAuth App settings apply only when no App is registered.
    - Given no App, when an Admin completes the manifest flow, then the App status is `configured` and the sign-in page shows "Continue with GitHub".
    - Given a v1 user linked to GitHub id 42, when they sign in through the App, then they reach the same account.
27. **FR-81** In **Account → Connections → GitHub**, a user connects GitHub. Signing in with GitHub connects automatically.
    - AIDE stores the user access token and refresh token encrypted, and refreshes the token before its 8-hour expiry.
    - With a connection, the **repo picker** lists the repos the App is installed on that the user can access (`GET /user/installations` → repositories), with search. It offers "Install AIDE on more repositories" (the App's install URL), and **Create repository**:
      - owner: the user, or an org where the App is installed,
      - name, description, visibility,
      - initialised with a README so the default branch exists.
    - Creating requires the App's Administration permission.
    - Name collision: `REPO_NAME_TAKEN → 409`. App not installed on the target owner: `GITHUB_APP_NOT_INSTALLED → 409`.
    - Given a connected user, when they create `acme/todo-app` (private), then the repo exists with `main`, the App can access it, and the wizard selects it.
28. **FR-82** A project's Git provider is one of:
    - **`github_app`** (default when an App exists): stores installation id, repo id and full name. Background Git work (clone, fetch, push, PR, merge, tag, release, polling) uses short-lived **installation tokens** minted per job (cached under 1 hour), never a user token.
    - **`github_pat`**: the v1 behaviour, still supported (FR-83).
    - **`local`**: v1.
    
    Branch per release version: `aide/<slug>/v<version>`. The v1 `aide/<slug>-<short-id>` branch name is kept for migrated in-flight versions.
    - Given an App project, when step 1 commits, then the push authenticates with an installation token and the commit is attributed to the App bot. The PR is opened by the App, and its body links back to the AIDE project.
29. **FR-83** The per-project **PAT fallback** stays for repos the App can't be installed on. It needs Contents and Pull requests read/write, plus Administration only for repo creation, which is unavailable with a PAT. Settings → Git shows which mode is in use, and "Switch to GitHub App" when the App is installed on the repo.
    - Given a PAT project, when settings load, then `mode: github_pat`, `hasToken: true` and no token value are returned.
30. **FR-84** **Merge readiness** is checked when the pre-release gate is created and on demand. It reports:
    - whether AIDE's token may merge (repository permissions and allowed merge methods),
    - branch rules (`GET /repos/{o}/{r}/rules/branches/{default}`): required reviews and status checks,
    - the PR's `mergeStateStatus` and `reviewDecision` (GraphQL),
    - required checks that are failing.
    
    The Release page and the gate show "AIDE will merge automatically", or "A merge on GitHub will be needed" with the reasons. The merge endpoint's own 405/409 remains authoritative (FR-65).
    - Given a ruleset requiring 1 approval and no approvals yet, when readiness is checked, then `canMerge: false` with reason `review_required`.
31. **FR-85** **GitHub check runs and commit statuses** on the PR head are a deterministic review input when available (Checks: read permission):
    - A failed required check is a major `testing` finding. Its evidence is the check name and the details URL.
    - Pending checks are waited for up to 15 minutes before review scoring. After that the review proceeds, and a warning is recorded.
    - Given a failing required `ci / test` check, when review runs, then a major finding "required check ci / test failed" applies −0.10 to testing.
32. **FR-86** The wizard's **Repository** step offers:
    - pick an existing repo or create one (`github_app`),
    - repo URL plus PAT (`github_pat`),
    - Local.
    
    A picked repo that isn't empty is allowed only for **import** (FR-73). In "start from scratch" it gets a warning, and AIDE then treats it as an import with "Never released".
    - Given a user picks a non-empty repo in "start from scratch", then the wizard shows "This repository has code. AIDE will build on it", and the Repository Analyst runs.

### 3.8 Vercel integration

33. **FR-87** In **Account → Connections → Vercel**, a user pastes a Vercel access token (team- or account-scoped, ideally with an expiry).
    - AIDE validates it (`GET /v2/user` and, for team tokens, the team), stores it encrypted, and shows account, team, scope and expiry.
    - Tokens are never returned. A token Vercel rejects marks the connection `invalid` and notifies the user.
    - Given a valid team token, when it is saved, then the connection shows the team slug and `valid`.
34. **FR-88** Projects whose preset supports Vercel (both v2 presets), or any project an Owner opts in, can set **Deployment → Vercel** in the wizard or in Settings → Deployment:
    - bind one of the Owner's Vercel connections,
    - create a new Vercel project linked to the GitHub repo (`POST /v11/projects` with `gitRepository`, framework `nextjs`, root directory) or link an existing one,
    - edit **environment variables** (key, value, targets `preview` / `production`, sensitive). Values are encrypted in AIDE, pushed to Vercel on save, and never returned.
    
    Failure cases:
    - The preset's required env vars are listed, and any that are missing are flagged.
    - Vercel's GitHub App lacks access to the repo: `VERCEL_GIT_NOT_CONNECTED → 409`, with Vercel's install link.
    - Hobby plan with an org-owned repo: `VERCEL_PLAN_LIMIT → 409`.
    - Requires a GitHub-backed project. Local provider: `VALIDATION`.
    - Given a `nextjs-supabase` project, when Deployment settings load, then `NEXT_PUBLIC_SUPABASE_URL`, `NEXT_PUBLIC_SUPABASE_ANON_KEY` and `SUPABASE_SERVICE_ROLE_KEY` are listed as required, and any that are missing are flagged.
35. **FR-89** The generated repo of a Vercel project stops Vercel's automatic deploys of AIDE branches: the preset scaffolder writes `vercel.json` `git.deploymentEnabled` for `aide/**`. The syntax built is `{"git":{"deploymentEnabled":{"aide/**":false}}}`; confirm it against a live Vercel project (§11).
    - *(decision 2026-10-18)* Review check, real Vercel provider only (never Fake): a major `architecture` finding when `<RootDirectory>/vercel.json` is missing or does not disable `aide/**`. It needs a Vercel settings row. The review reads the file directly, not limited by the tree cap.
    - *(decision 2026-10-18)* The plan warning `MISSING_VERCEL_CONFIG` is preset-keyed, because `vercel.json` is scaffolded before deployment is bound. It appears only when the base files are known to lack `<RootDirectory>/vercel.json` (root `vercel.json` when no root directory is set; in practice new repos).
    - Both look for `<RootDirectory>/vercel.json` with one case-sensitive match. AIDE then triggers deployments **explicitly**, so it knows the deployment id. Production auto-deploys from the default branch are left on.
36. **FR-90** **Preview deployments.** When execution of a run completes (the PR opens or updates), AIDE creates a preview deployment of the branch head (`POST /v13/deployments`, `gitSource` ref and sha) and polls it (§4.15).
    - The Review page shows its URL, state and build-log tail. A Contributor can redeploy.
    - The review cycle waits for `READY`/`ERROR` (up to 20 minutes) before scoring.
    - `ERROR` is a **critical** `architecture` deterministic finding, with the build-log tail as evidence. The optimisation plan therefore has to fix the build.
    - Given a preview whose build fails on a type error, when review runs, then a critical finding "Vercel preview build failed" with the log excerpt is in the report, and the decision can't be `release_eligible` unless the score still clears the threshold.
37. **FR-91** **Production deployments.** After the release merge (FR-64/65), AIDE creates a production deployment of the merge commit, or adopts the one Vercel auto-created for that SHA. It polls it and records it on the release (deployment id, URL, state).
    - A failed production deployment doesn't undo the release. It notifies Owners (`DEPLOYMENT_FAILED`) and shows on the Release page with "Redeploy".
    - Owners can **promote** an earlier `READY` production deployment (`POST /v10/projects/{id}/promote/{deploymentId}`), as a production rollback that leaves Git alone.
    - The **Deployments** page lists all deployments: target, version, SHA, state, URL, time, who triggered it.
    - Given release 1.2.0 merged, when the production deployment becomes `READY`, then the Release page shows its URL and "Released 1.2.0 · live on Vercel".

### 3.9 CLI, activity and notifications

38. **FR-92** New events (live, activity feed, admin): `VERSION_STARTED`, `CODEBASE_SUMMARY_CREATED`, `ARTIFACT_RESTORED`, `RELEASE_AWAITING_MERGE`, `RELEASE_MERGED_EXTERNALLY`, `DEPLOYMENT_CREATED`, `DEPLOYMENT_UPDATED`, `CONNECTION_INVALID`, `PRESET_CHANGED`.
    - New notification types:
      - release awaiting merge (Owners),
      - deployment failed (Owners),
      - preview ready (all members),
      - connection invalid (the connection's owner),
      - LLM binding cleared (Owners).
    - The approval-link set (v1 FR-48) is unchanged.
    - Given a preview becomes `READY`, when members are viewing the Review page, then it updates within 2 s and members get a "preview ready" notification.
39. **FR-93** CLI additions:
    - `aide version start <id> <semver> --request <text|@file> [--preset <id>]`
    - `aide versions <id>`
    - `aide deployments <id>`
    - `aide artifact restore <id> <type> <version>`
    - `status` includes released and in-development versions, checkpoint, preset and the latest deployment.
    - Exit codes follow v1 (`VERSION_*`/`TAG_EXISTS`/`RELEASE_NOT_MERGED` → 4).
    - Given a released project, when `aide version start <id> 1.3.0 --request @req.md` runs, then it exits 0 and prints the new version.

## 4. Domain rules

v1 §4.1–4.9 apply, with these amendments and additions.

### 4.10 Release versions and checkpoints *(amends §4.1, §4.8)*
- **Release version:**
  - Statuses: `in_development → awaiting_merge → released`, `in_development → released` (direct merge), plus `imported` (a baseline from import or migration; never developed in AIDE).
  - At most one `in_development` or `awaiting_merge` version per project (partial unique index).
  - The project's **released version** is the highest `released` or `imported` version. Its **development version** is the one `in_development` or `awaiting_merge`.
- **Semver:** `MAJOR.MINOR.PATCH[-prerelease]`, with no build metadata. A leading `v` is stripped on input. Comparison follows SemVer 2.0.0 precedence. A new version must be strictly greater than the project's highest released or imported version. Tag = `tagPrefix` (default `v`, per project, `^[A-Za-z0-9._/-]{0,20}$`) + version.
  - Worked: released `1.1.0` → `1.2.0` ✓, `1.1.1` ✓, `2.0.0-beta.1` ✓, `1.1.0` ✗, `1.0.9` ✗, `1.2.0-rc.1` then `1.2.0` ✓ (separate versions), `01.2.0` ✗ (invalid).
- **Checkpoint:** an integer per release version, starting at 1 at version start. It increments on every forward phase transition, exactly where v1 incremented the project version, *except* release completion, which no longer increments. It doesn't increment on `review → execution`, invalidation fallback or no-ops. Artifacts, steps, gates, reports and transitions record `(release_version_id, checkpoint)`. Rollback sets the checkpoint to the target, and the next forward transition makes it target + 1.
- **Phase state per version:** starting a version applies the transition `release → refinement` with kind `new_version`. It is allowed only from status `released` with no in-development version, and it resets `review_cycle`, `extra_review_cycles` and `execution_paused`.
  - The v1 rule "forward transitions go to the next phase only" gains this one exception.
  - Every allowed/forbidden pair, including `new_version` from every other phase and status, has a unit test.
- **"Current" artifact** of a type (v1 §4.3) is scoped to the release version in development. The previous released version's locked artifacts are **baseline inputs**: read-only, never invalidated by the cascade, and never approvable.
- **Rollback** *(amends §4.8)*:
  - Targets are checkpoints `< current` of the release version in development, and never one whose phase is `release`.
  - When the project is `released` (nothing in development) or `awaiting_merge`: `ROLLBACK_BLOCKED` with reason `version_released` or `release_partially_applied`.
  - All other v1 rollback rules (rate limit, in-progress blocks, the Git branch is not rewritten) apply.
- **Migration from v1:**
  - Each project gets release versions as in FR-67. Columns `project_version` become `checkpoint`, plus a `release_version_id` FK on project, artifact, execution_step, approval_gate, state_transition, compliance_report, rollback_event and release.
  - Git branches are kept: `release_versions.branch_name` stores the v1 name.
  - Must be one EF migration with data backfill SQL reviewed in PROGRESS.md.

### 4.11 Presets
- A preset is a folder `backend/src/Aide.Infrastructure/Agents/Presets/<id>/` (embedded resources), content-hashed, with `version` in `preset.json`:
  - `preset.json`:

    ```
    id, name, version, description, stack[] (chips), deployTargets[] ("vercel"),
    requiredEnv[] {key, targets[], sensitive, description},
    specSections[] {title, guidance}, specConventions (markdown),
    planRules {scaffoldFirstWhenEmpty, order[] {before, after}, exclusive[] {agent, notWithGlobs[]}},
    agents[] {key, name, description, owns[] (globs), skills[], prompt (file)},
    review {checklist[] {category, item, severity}, deterministic[] (rule ids)},
    protectedPaths[]
    ```
  - `agents/<key>.md`: the specialist's prompt.
  - `skills/<name>.md`: condensed, AIDE-adapted skill rules.
- Agent prompts are **adapted, not copied**, from `claude-kits` (`BenFielder1/claude-kits` at commit `14ada86`). Kit agents use tools, shell and multi-file phases. AIDE specialists produce one plan step's files as structured output. Drop instructions about running commands, Git, PRs and reviews. Keep each agent's ownership, rules, checklist and "done when" as output requirements.
- Rendering order for a specialist step: base `code_generator.md` → preset `specConventions` → agent prompt → each listed skill → step input. A missing referenced file fails preset loading at startup (a health check fails, and a unit test asserts every preset loads).
- Plan validation with a preset, after the v1 checks:
  - `agent` must be a catalogue key (`INVALID_PLAN`).
  - `planRules.order` (a step whose files match `before` globs must come before any step matching `after` globs) is an error.
  - `exclusive` (a step assigned `agent` must not declare files matching `notWithGlobs`) is an error.
  - `scaffoldFirstWhenEmpty` (on an empty repo, step 1 must be the scaffolder) is an error.
  - Owned-path mismatch (`AGENT_SCOPE`) is a warning.
  - Uncovered new or changed FR IDs give `UNCOVERED_REQUIREMENTS` (warning).
  - Without a preset, `agent` must be `generalist`, or it is defaulted to it when omitted (backward compatible with v1 plans).
- Preset `protectedPaths` add to the Guardian's protected list (v1 §4.6 rule 5), with the same "unless declared in this step" exception.

### 4.12 The two v2 presets

Source: `claude-kits/plugins/nextjs-supabase-kit` (agents, skills `supabase-migration`, `nextjs-api-route`, `nextjs-ui`, `domain-logic`, `github-vercel-delivery`). There is no frontend-only kit; `nextjs-frontend` is derived from the same kit minus Supabase.

**Stack chips:**
- `nextjs-supabase`: Next.js (App Router), TypeScript strict, Tailwind, Supabase (Postgres, Auth, RLS, Realtime), `@supabase/ssr`, Zod, Vitest, Testing Library, Playwright, GitHub Actions, Vercel.
- `nextjs-frontend`: the same without Supabase, plus data from external APIs or mocks.

| Agent key | nextjs-supabase | nextjs-frontend | Owns (globs) | Core rules (from the kit) |
| --- | --- | --- | --- | --- |
| `project-scaffolder` | ✓ | ✓ | `package.json`, config files, `app/layout.tsx`, `lib/supabase/**` (supabase), `middleware.ts`/`proxy.ts`, `.nvmrc`, `vercel.json` | Current stable versions with pinned Node; strict TS with `noUncheckedIndexedAccess`; scripts `dev build lint typecheck test test:e2e` (+ `db:types`) |
| `db-engineer` | ✓ | — | `supabase/migrations/**`, `supabase/seed.sql`, `supabase/config.toml`, `lib/database.types.ts` | RLS on every table; `is_member`-style security definer helpers; RPCs set `search_path` and re-check permissions; seed covers every role and state; committed migrations are never edited |
| `domain-logic-engineer` | ✓ | ✓ | `lib/<domain>/**` | Pure, deterministic, total functions; tests first and citing spec IDs; one source of truth for constants |
| `api-engineer` | ✓ | ✓ (BFF routes only) | `app/api/**`, `lib/api/**` | Zod first; `requireUser` via `getUser()`; privileged writes through RPCs; thin handlers; one error-code → status map |
| `ui-engineer` | ✓ | ✓ | `app/**` (not `app/api`), `components/**`, `lib/ui/**`, styles | Server Components first; loading/empty/error/forbidden states; pure `can*` permission helpers; one error-copy map; 44 px tap targets; WCAG AA |
| `test-engineer` | ✓ | ✓ | `e2e/**`, `playwright.config.ts`, `**/*.test.ts(x)` outside owned dirs | Happy path with several users; permission boundaries (hidden control + API 403); validation limits; mobile layout |
| `devops-engineer` | ✓ | ✓ | `.github/**`, `vercel.json`, `.env.example` | CI `checks` (typecheck, lint, test, build) + `e2e`; env inventory; secret scan step; never commit secrets |

**Plan rules:**
- Both presets: scaffold first when the repo is empty; `lib/<domain>` before `app/**` steps that import it (soft: a warning).
- `nextjs-supabase`: `supabase/migrations/**` before `app/api/**` and before `app/**` that reads the tables; `db-engineer` steps must not declare `app/**` or `components/**`; one migration file per step.

**Extra spec sections** (with guidance from the kit's expected spec shape):
- `nextjs-supabase`:
  - Roles & Permissions Matrix
  - Data Model & RLS Policies (per table, per role)
  - Auth Flows (email/OAuth/anonymous; redirect URLs)
  - Realtime & Background Work
  - Environment Variables
- `nextjs-frontend`:
  - Routes & Rendering Strategy (server/client, caching)
  - Data Sources & Consumed APIs (contracts, error mapping)
  - Environment Variables

**Review checklist highlights** (the full list lives in `preset.json`):
- RLS on every table (security, critical)
- service-role key only server-side (security, critical)
- route handlers validate with Zod (api_contracts, major)
- every page has loading/error states (feature_coverage, minor)
- CI runs typecheck, lint, test and build (testing, major)

**Protected paths:**
- `nextjs-supabase`: `supabase/migrations/**` files that already exist on the base commit (immutable migrations), `lib/database.types.ts` (generated)
- both: `.env*` except `.env.example`

**Producing phase per artifact type** (for FR-58 and v1 §4.3):

| Type | Phase |
| --- | --- |
| `codebase_summary` | refinement (auto) |
| `project_request` | refinement |
| `technical_specification` | specification |
| `implementation_plan` | planning |
| `optimization_plan` | review |

### 4.13 Specs for iterations *(amends FR-28)*
- Required sections:
  - with no preset and no iteration: v1's eleven sections, unchanged;
  - with a preset: + the preset's sections;
  - **iteration** (a new version, or an import): + **"Changes in this version"**.
- **Iteration specs are cumulative:** one full spec of the system as it will be after this version.
  - Each functional requirement is tagged on its heading line with `[new]`, `[changed]`, `[unchanged]` or `[removed]`.
  - For an import's first version, existing behaviour is reverse-engineered from the codebase summary and the repo tools, and tagged `[unchanged]`.
  - Validation: every `FR-n` has exactly one tag. At least one `[new]` or `[changed]` exists. Every `[changed]` or `[removed]` ID existed in the baseline spec, except on the first version of an import.
- **Review scope** *(amends §4.7)*:
  - In iterations, *feature coverage* is scored over `[new]` and `[changed]` requirements only.
  - `[unchanged]` requirements found broken are reported as **regressions**: an agent violation in the relevant category, plus a drift area with severity `major`. They have no deterministic penalty.
  - Weights, the decision table (§4.5) and drift levels are unchanged.
- **Plan scope:** iteration plans cover `[new]` and `[changed]` requirements (`covers`). Steps may modify existing files. The v1 rule "no file declared by another step" applies within the plan only.

### 4.14 GitHub
- **App permissions (manifest):**
  - Contents RW, Pull requests RW, Metadata R, Administration RW (repo creation; Admins may untick it in the manifest preview, which disables "Create repository"), Checks R, Commit statuses R, Workflows RW (so agents can write `.github/workflows/**` when a step declares it).
  - User permissions: email R.
  - Webhook inactive.
- **Tokens:**
  - User tokens are used only for the picker, repo creation and identity. Installation tokens are used for all background work; minted from an RS256 JWT and cached ≤ 55 min.
  - A token is never written to disk, never put in a remote URL, never logged and never given to an LLM. v1's `extraHeader` env mechanism is kept.
  - A failed refresh marks the GitHub connection `invalid` and notifies the user.
- **Polling (no webhooks; AIDE is on localhost):**
  - Merge poll: every 2 min for the first hour, then every 10 min, for up to 14 days. After that the attempt fails with `MERGE_TIMEOUT`, and an Owner can retry.
  - Check runs: every 30 s while waiting (FR-85).
  - Polls use conditional requests (ETag). A `403`/`429` rate limit backs off until the reset time.
- **Merge detection:** `GET /pulls/{n}` with `merged = true` gives `merge_commit_sha`, which is the tag target. If the default branch was updated by a squash or rebase, the PR's `merge_commit_sha` is still authoritative.
- **Create repo:**
  - User owner: `POST /user/repos` (user token).
  - Org: `POST /orgs/{org}/repos`.
  - Both use `auto_init: true`. The App's access to the new repo is verified before the wizard continues; a user-scoped installation set to "selected repositories" may need the user to add it, so the install link is shown.
- **Release notes:** generated by AIDE (not GitHub's auto-notes). Contents: the version, the spec's "Changes in this version", the plan step titles with commit links, the review score and a link back to AIDE.

### 4.15 Deployments (Vercel)
- **Deployment states:** `queued → building → ready | error | canceled`, mapped from Vercel `readyState`.
- **Polling:** every 10 s for 2 min, then every 30 s, with a 30-minute ceiling (then `error` with `DEPLOYMENT_TIMEOUT`).
- **Previews:** at most one active preview poll per project; a newer preview of the same branch cancels polling of the older one (the older one is still recorded).
- **Review waiting:**
  - Review waits for the latest preview of the PR head SHA. If there is none (Vercel not configured), no wait.
  - Waiting counts toward nothing; it is not a failed attempt.
- **Production:** created once per release (idempotent by `(release_id, target)`). Redeploy creates a new row.
- **Env sync:**
  - Upserts by `(key, target)`. Deleting in AIDE deletes in Vercel.
  - Values are never logged.
  - Vercel rate limits (120 env writes/min) are respected by batching.
- **Provider abstraction:** `IDeploymentProvider` with `Vercel` and `Fake` (deterministic: `READY` after 2 polls with URL `https://fake-<sha8>.vercel.test`; `ERROR` when the branch contains a file `.aide-fake/fail-build`). E2E and integration tests use Fake or a stub HTTP handler, never the real API.

## 5. Data model

v1 §5 applies. Changes (all snake_case, UUIDv7, UTC):

| Entity | Key fields | Constraints / indexes | Notes |
| --- | --- | --- | --- |
| **release_version** (new) | id, project_id, version (text), status (in_development/awaiting_merge/released/imported), tag?, tag_prefix, base_tag?, base_commit_sha?, merge_commit_sha?, branch_name?, pull_request_url?, pull_request_number?, request (text), preset_id?, preset_version?, preset_hash?, started_by?, created_at, released_at? | unique (project_id, version); partial unique (project_id) where status in (in_development, awaiting_merge) | PR fields move here from project_git_settings |
| project (changed) | − `project_version`; + `checkpoint`, `current_release_version_id` (FK, nullable when only released), `released_version` (text, denormalised), `development_version` (text, denormalised), `merge_strategy` (aide_merges/manual), `tag_prefix` | `xmin` | denormalised versions keep list queries cheap; written in the same transaction as release_version changes |
| artifact, execution_step, approval_gate, state_transition, compliance_report, rollback_event, release (changed) | `project_version*` → `checkpoint*`; + `release_version_id` FK | add (project_id, release_version_id) indexes | artifact also + `restored_from_version?`; type adds `codebase_summary` |
| execution_step (changed) | + `agent_key`, `covers text[]` | | |
| **execution_step_attempt** (new) | id, step_id, attempt, agent_run_id, outcome (accepted/rejected/error), violations jsonb, warnings jsonb, produced_files jsonb, created_at | unique (step_id, attempt) | attempt history (FR-57); v1 rows are backfilled from agent_runs where possible |
| agent_run (changed) | + `agent_key?`, `preset_id?`, `preset_version?`, `credential_id?` (FK set null), `tool_calls jsonb`, `release_version_id` | | `credential_id` records which connection paid; never the key |
| compliance_report (changed) | + `regressions jsonb`, `external_checks jsonb` (check runs, preview deployment) | | |
| release (changed) | + `release_version_id`, `github_release_url?`, `awaiting_merge_since?`, `merge_reasons jsonb?`; status adds `awaiting_merge` | | tag is now the user's semver tag |
| **llm_credential** (new) | id, user_id, provider (anthropic/openai), label, secret_encrypted, last4, status (valid/invalid/unchecked), last_validated_at?, created_at | index (user_id) | |
| project_llm_settings (changed) | − `api_key_encrypted`; + `credential_id?` (FK, set null), `bound_by_user_id?` | | |
| **github_app** (new, single row) | id, app_id, slug, owner_login, owner_type, client_id, client_secret_encrypted, private_key_encrypted, permissions jsonb, created_at | at most one row | env config overrides it |
| **github_connection** (new) | user_id (PK), github_user_id, login, access_token_encrypted, access_expires_at, refresh_token_encrypted, refresh_expires_at, status, updated_at | unique github_user_id | |
| project_git_settings (changed) | provider adds `github_app`; + `installation_id?`, `repo_id?`, `repo_full_name?`; PR fields move to release_version | | `token_encrypted` only for `github_pat` |
| **vercel_connection** (new) | id, user_id, label, token_encrypted, last4, vercel_user_id, team_id?, team_slug?, scope, expires_at?, status, last_validated_at?, created_at | index (user_id) | |
| **project_deployment_settings** (new) | project_id (PK), provider (vercel), connection_id? (FK set null), bound_by_user_id?, vercel_project_id?, vercel_project_name?, root_directory, framework, production_url? | | |
| **deployment_env_var** (new) | id, project_id, key, targets text[], value_encrypted, sensitive, updated_at, synced_at?, sync_error? | unique (project_id, key) | |
| **deployment** (new) | id, project_id, release_version_id, release_id?, provider, target (preview/production), provider_deployment_id, url?, git_ref, git_sha, status, error_code?, error_message?, build_log_tail?, triggered_by, created_at, ready_at? | index (project_id, created_at); unique (release_id, target) where target = production | |

Migration notes:
- One migration for versions/checkpoints (backfill per FR-67). Separate migrations per milestone for connections, presets and deployments.
- No committed migration is edited.
- `rollback_events.restored_phase NOT NULL` (v1 known issue) is folded into the versions migration.

## 6. API

v1 §6 applies. All new routes are under `/api`, with ProblemDetails `errorCode`.

**Breaking-change decision (D-V2-11):** `projectVersion` fields become `checkpoint`, and `releasedVersion`, `developmentVersion` and `releaseVersionId` are added. v2 ships the web app and CLI together, so the old field isn't kept. Record it under Decisions and in the `oasdiff` result.

| Method | Route | Body / query | Response | Errors (code → status) | Auth |
| --- | --- | --- | --- | --- | --- |
| GET | /api/presets · /presets/:id | — | preset summaries / detail (agents, sections, env, targets) | `NOT_FOUND → 404` | user |
| GET | /api/projects/kpis | same filters as the list | KPI counts: active, in review, awaiting gates, released *(decision 2026-10-18)* | — | user |
| GET | /api/projects/:id/versions | — | release versions (status, tag, checkpoints, dates, preset) | — | member |
| POST | /api/projects/:id/versions | version, request, presetId? | 201 release version + state | `VERSION_INVALID → 400`, `VERSION_NOT_GREATER → 409`, `TAG_EXISTS → 409`, `VERSION_IN_PROGRESS → 409`, `RELEASE_NOT_MERGED → 409`, `NOT_RELEASED → 409` | owner |
| GET | /api/projects/:id/checkpoints | releaseVersionId? | checkpoints with phase (replaces `/versions` from v1, D-V2-11) | — | member |
| PATCH | /api/projects/:id/preset | presetId \| null | project | `PRESET_LOCKED → 409`, `PRESET_NOT_FOUND → 400` | owner |
| POST | /api/projects/:id/artifacts/:type/versions/:v/restore | — | 201 draft | `RESTORE_NOT_ALLOWED → 409`, `VALIDATION → 400` | writer |
| GET | /api/projects/:id/execution/runs | releaseVersionId? | runs: plan type/version, cycle, status, counts, superseded? | — | member |
| GET | /api/projects/:id/execution/steps/:stepId/attempts | — | attempts with run summary, violations, tool calls | `NOT_FOUND → 404` | member |
| GET | /api/projects/:id/compliance/reports · /reports/:cycle | releaseVersionId? | report summaries / full report for that cycle | `NOT_FOUND → 404` | member |
| GET | /api/projects/:id/git/merge-readiness | — | canMerge, reasons[], checks[] | `WRONG_PHASE → 409` | member |
| GET/PATCH | /api/projects/:id/settings/deployment | provider, connectionId, vercelProjectId \| create {name, rootDirectory} | settings (secrets as flags) | `VERCEL_GIT_NOT_CONNECTED → 409`, `VERCEL_PLAN_LIMIT → 409`, `CREDENTIAL_NOT_OWNER → 403`, `UPSTREAM_ERROR → 502` | owner (GET: member) |
| GET/PUT/DELETE | /api/projects/:id/settings/deployment/env[/:key] | key, value, targets, sensitive | env list (`hasValue`, no values) | `VALIDATION → 400`, `UPSTREAM_ERROR → 502` | owner |
| GET/POST | /api/projects/:id/deployments · /deployments/preview | target? / — | deployments / 202 deployment | `DEPLOYMENT_NOT_CONFIGURED → 409` | member / writer |
| POST | /api/projects/:id/deployments/:deploymentId/promote · /redeploy | — | 202 deployment | `INVALID_DEPLOYMENT → 409` (not ready/production) | owner |
| GET/POST/PATCH/DELETE | /api/me/connections/llm[/:id] · /:id/test | provider, label, apiKey | credentials (last4, status) | `CREDENTIAL_INVALID → 400`, `NOT_FOUND → 404` | user (browser session) |
| GET/DELETE | /api/me/connections/github | — | login, status, installations | — | user |
| GET | /api/me/github/repos · /owners · /repos/:owner/:repo/refs | search, page / — / — | repos the App can access / owners / releases + semver tags + default branch | `GITHUB_NOT_CONNECTED → 409`, `GITHUB_APP_NOT_INSTALLED → 409` | user |
| POST | /api/me/github/repos | owner, name, description?, private | 201 repo | `REPO_NAME_TAKEN → 409`, `GITHUB_APP_NOT_INSTALLED → 409`, `FORBIDDEN → 403` | user |
| GET/POST/PATCH/DELETE | /api/me/connections/vercel[/:id] · /:id/test | label, token | connections | `CREDENTIAL_INVALID → 400` | user (browser session) |
| GET | /api/admin/integrations/github | — | App status, slug, install URL, permissions | — | admin |
| POST | /api/admin/integrations/github/manifest | ownerType, org? | manifest + GitHub URL to post to | `CONFLICT → 409` (already configured) | admin (browser session) |
| GET | /api/admin/integrations/github/callback | code, state | redirect to admin page | `GITHUB_AUTH_FAILED` redirect | admin (browser session) |
| POST | /api/projects (changed) | + source (`scratch`/`import`), initialVersion \| {releasedTag?, developmentVersion}, presetId?, git {mode, installationId?, repoFullName? \| repoUrl+token}, deployment? | 201 project | + `VERSION_*`, `TAG_NOT_FOUND → 400`, `GITHUB_APP_NOT_INSTALLED → 409` | user |
| PATCH | /api/projects/:id/settings/llm (changed) | provider, models, credentialId? | section | `CREDENTIAL_NOT_OWNER → 403`, `NOT_FOUND → 404` | owner |

Notes:
- Existing endpoints with history (artifacts, runs, steps, compliance, releases, activity) accept `releaseVersionId` and default to the version in development, or to the latest released when nothing is in development.
- Connection mutation endpoints require a browser session; API tokens get `FORBIDDEN`, as for v1 token management.
- New run and job failure codes:
  - `CREDENTIAL_INVALID`
  - `TOOL_LIMIT`
  - `UNSAFE_PATH` (tool)
  - `MISSING_REQUIREMENT_IDS`
  - `MERGE_TIMEOUT`
  - `PR_CLOSED`
  - `TAG_EXISTS`
  - `DEPLOYMENT_TIMEOUT`
  - `VERCEL_ERROR`
  - `GITHUB_RATE_LIMITED`
- Every new error code gets frontend copy in the one error map.

## 7. Pages and UI

### 7.1 Design system and shell (replaces v1 §7 UI shell; v1 UI rules still apply)

Reference implementation: `../../Space Worm Analytics` (read-only), especially:
- `app/globals.css`, `lib/ui/tokens.ts`
- `components/shell/{app-shell,sidebar,nav-config,nav-list,header,mobile-drawer}.tsx`
- `components/shared/{panel,metric-card,chart-card,data-table,status-badge,empty-state,page-header}.tsx`
- `components/charts/chart-theme.ts`

Port the *design*, not the Next.js code. AIDE stays React + Vite + TanStack Router.

- **Libraries (new):**
  - shadcn/ui ("new-york", CSS variables) on `radix-ui`
  - `lucide-react` icons
  - `recharts` (replaces the hand-written SVG trend chart)
  - `cmdk` command palette
  - `sonner` toasts
  - `class-variance-authority`, `tailwind-merge`, `clsx`
  - fonts **self-hosted via `@fontsource`** (DM Sans body, Outfit 600 headings, Geist Mono numbers and code). No Google Fonts CDN; this keeps the v1 CSP.
- **Tokens:**
  - OKLCH CSS variables on `:root` (dark) and `.light`/system light, values as Space Worm's `globals.css`: background, foreground, card, popover, primary (cyan 195), secondary, muted, accent, destructive, success, warning, border, input, ring, chart-1..5, sidebar*.
  - Mapped through Tailwind 4 `@theme inline`.
  - Mirrored in `src/lib/ui/tokens.ts`, with a unit test checking WCAG AA contrast for text pairs in both themes.
  - The theme stays system / light / dark (v1), with dark first-paint.
  - Radius `0.75rem`. Surfaces `rounded-xl`, controls `rounded-lg`, pills `rounded-full`.
  - Ambient blurred background orbs, frosted sidebar and header (`backdrop-blur`).
  - Primary glow on primary CTAs.
  - A `prefers-reduced-motion` kill switch.
- **Logo:**
  - `docs/v2/brand/aide-mark.svg` (mark) and `aide-logo.svg` (lockup) are the source.
  - The app renders them as React components (`BrandMark`, `BrandLockup`) with gradient stops bound to `--chart-1`/`--chart-2`.
  - `frontend/public/favicon.svg` is replaced by the mark.
  - The lockup is in the sidebar (mark only when collapsed), on auth pages and in the mobile drawer.
  - The mark's six segments are the lifecycle stages. Keep that meaning in any marketing copy.
- **Sidebar:**
  - Fixed and frosted. Hidden below `lg` (drawer), icons-only 64 px from `lg` to `xl`, expanded 256 px from `xl`. A user toggle to pin collapsed or expanded is persisted in localStorage.
  - Top: lockup and a search button (⌘K).
  - **Workspace nav:** Projects, Notifications (unread count), Connections, Admin (admins: Overview, Users, Events, Agent runs, Dead letters, Integrations).
  - **Project nav:** FR-54.
  - Footer: user menu (avatar initials, name, email, Account, Connections, Sign out) and the theme menu.
  - Active item: `bg-sidebar-accent`, a primary icon, a pulsing primary dot. Section labels are 11 px uppercase muted.
  - One `nav-config.ts` drives the sidebar, drawer, command palette and breadcrumbs.
- **Header:**
  - Sticky, frosted, 64 px.
  - Contents: hamburger (< lg), breadcrumbs, then on project pages the live indicator and version switcher, then the notifications bell.
  - Icon buttons have 44 px targets.
- **Shared components:**
  - `Panel` (frosted card), `PageHeader` (title, description, actions)
  - `MetricCard` (label, mono value, delta, optional sparkline)
  - `ChartCard` with a "view as table" toggle (accessibility)
  - `DataTable` (sortable, paginated, stacked cards below `md`)
  - `StatusBadge` (tones success/warning/destructive/info/neutral, dot plus tinted pill)
  - `VersionBadge` (released, in development)
  - `PhaseBadge`, `StageStepper`
  - `EmptyState` (no data, not started, not connected, forbidden, error)
  - `Skeleton`s per layout, shadcn `Dialog`/`Sheet`/`DropdownMenu`/`Tabs`, sonner toasts for async results
- **Charts:**
  - recharts themed from CSS variables: no axis lines, dashed horizontal grid, frosted tooltip, 260 px height.
  - Compliance trend: line of scores, a threshold reference line, points coloured by decision.
  - Category breakdown: horizontal bars of agent score with the penalty as an overlay.

### 7.2 Routes

| Route | Page | Key content / states |
| --- | --- | --- |
| /login, /register | Auth | Centred frosted card with lockup; "Continue with GitHub" (App or OAuth); error · loading |
| /projects | Projects | KPI row (active, in review, awaiting gates, released) from `GET /api/projects/kpis` *(decision 2026-10-18: same filters as the list; awaiting gates = pending gates across the filtered projects)*; table/cards with name, preset chip, phase, status, **released + in-development badges**, updated; search, filters (phase, status, has development version, preset), sort, paging; empty · loading · error |
| /projects/new | New project wizard | Stepper: **Source** (scratch / import) → Idea or Request → **Repository** (picker / create / PAT / Local; release-tag picker for import) → **Version** → **Stack** (preset cards) → Configuration (v1) → **Deployment** (optional Vercel) → Review & create; per-step validation |
| /projects/:id | Overview | Stage stepper, next-action card, KPI tiles (checkpoint, latest compliance, steps done, tokens used / budget), pending gates, latest deployment, activity preview |
| /projects/:id/{refinement,specification,planning,execution,review,release} | Stage pages | FR-55–FR-57, FR-90, FR-91; not started · running · awaiting approval · done · read-only (past version) |
| /projects/:id/artifacts · /artifacts/:type | Artifacts | FR-59, FR-58; version filter, diff, restore, integrity |
| /projects/:id/runs | Agent runs | DataTable with agent, specialist, model, credential label, tokens, tool calls, duration, status; detail sheet |
| /projects/:id/activity | Activity | Full feed (v1 FR-50), filters |
| /projects/:id/deployments | Deployments | FR-91 list, promote, redeploy; not configured |
| /projects/:id/settings/{general,members,git,llm,deployment,notifications,danger} | Settings | Secondary left nav (Space Worm settings pattern; tabs below `lg`); Git shows mode/App status/merge strategy/tag prefix; LLM binding (FR-79); Deployment + env vars (FR-88); Danger: rollback (checkpoints), delete |
| /connections (alias /account/connections) | Connections | LLM providers, GitHub (connect/disconnect, installations, install link), Vercel; status badges, test, delete |
| /account | Account | Profile, linked GitHub, API tokens (v1) |
| /notifications, /approvals/:token | v1 pages | restyled |
| /admin/{index,users,events,runs,dead-letters,integrations} | Admin | v1 + Integrations (GitHub App registration and status, presets list, system-key policy) |

**Dialogs:**
- **Start new version:** version input with live validation (format, greater than released, tag free), request textarea, preset select, a summary of what happens.
- **Restore version:** a diff preview against the latest, then confirm.
- **Rollback:** checkpoints of the current version, each with its phase.
- **Promote deployment.**

UI rules (in addition to v1):
- Every list and detail view shows which release version it belongs to.
- Read-only past-version views show a banner "Viewing 1.1.0 (released). Read-only".
- Actions the user can't take are hidden, or disabled with the reason, and the API enforces them anyway.
- Long-running external work (merge poll, deployment) shows a live status pill.

## 8. Non-functional requirements

v1 §8 applies, plus:
- **Security:**
  - All third-party secrets (LLM keys, GitHub tokens and App private key, Vercel tokens, env var values) are encrypted with Data Protection (purpose per secret kind), never returned, never logged, and never in LLM prompts.
  - Repo tool output passes through the secret scanner.
  - Installation tokens are scoped to the project's repo (`repository_ids`) when minted.
  - The App private key is never sent to the browser.
  - The manifest flow uses a signed, single-use `state`.
  - Vercel and GitHub API hosts are fixed (no user-supplied base URLs), which avoids SSRF.
- **Reliability:**
  - Merge and deployment polls are jobs in the v1 job table (outbox, leases, retries).
  - A restart resumes polling.
  - External rate limits back off without counting as failures.
- **Performance:** project list p95 < 300 ms with badges (denormalised versions). The execution-runs and review-cycles endpoints are p95 < 300 ms for 10 cycles × 30 steps.
- **Accessibility:**
  - WCAG 2.2 AA in both themes (the token contrast test).
  - Sidebar and stepper are keyboard navigable with `aria-current`.
  - Charts have table alternatives.
  - axe checks in E2E for the new shell, stage pages and wizard.
- **Testing:**
  - Every new domain rule (§4.10–4.15) has unit tests with the worked examples: semver order, checkpoint transitions including `new_version`, restore conditions, preset plan validation, spec iteration validation, review regression scoring, merge-poll state machine, deployment states.
  - GitHub and Vercel are tested with stub HTTP handlers; no test calls the real APIs.
  - E2E runs the full **two-version** flow with Fake LLM + Local Git + Fake deployments: create 0.1.0 → release → start 0.2.0 → release.
  - Separately, E2E imports a Local "existing" repo, plus the preset path with `nextjs-frontend` and the Fake provider's scripted specialist outputs.

## 9. Local deployment

v1 §9 applies, plus:
- **Public base URL** (`App:PublicBaseUrl`, default `http://localhost:8080`) is used for the GitHub App manifest callback, setup URLs and notification links. The dev loop uses `http://localhost:5173` (proxied).
- **Optional secrets** (`secrets.env`): `GitHub__App__AppId`, `GitHub__App__ClientId`, `GitHub__App__ClientSecret`, `GitHub__App__PrivateKey` (PEM, with `\n` escapes), `GitHub__App__Slug`. These override the DB-registered App. `Llm__AllowSystemKeys` (default false).
- **Egress:** the API pod reaches `api.github.com`, `github.com` and `api.vercel.com` (Docker Desktop default networking; no change).
- **Seed (dev):**
  - The demo project is released at `0.1.0` (Fake + Local) so "Start new version" can be tried at once.
  - A second demo project uses the `nextjs-frontend` preset in `planning`.
- **Fake deployment provider** is enabled when `Deployments:Provider=Fake` (Development and E2E). Outside Development and Testing it also needs `Deployments:AllowFake=true`, or the API refuses to start; it logs a warning on every boot. *(decision 2026-10-17)*
- **E2E mode on Kubernetes** *(decision 2026-10-17)*: `./deploy/scripts/deploy-local.sh --e2e` (the only switch) applies `deploy/k8s/overlays/local-e2e`: `Deployments:Provider=Fake`, `Deployments:AllowFake=true`, `Git:Local:ImportRoot=/data/import` and `Llm:DefaultProvider=fake`. It seeds `/data/import/existing-app` (tags `v0.1.0`, `v0.2.0`). A plain `deploy-local.sh` switches back (Vercel, no Local import).
- **Seed upgrade** *(decision 2026-10-18)*: a v1-seeded "Demo: Todo API" is upgraded to a released `0.1.0` on startup only if it is strictly untouched (initial checkpoint, the seed admin as only Owner, no artifacts, runs, gates, steps, reports, releases, rollbacks or deployments, and only the `PROJECT_CREATED` event). The marker event is `DEMO_PROJECT_RELEASE_UPGRADED`.

## 10. Decisions

v1 decisions D1–D21 (`docs/v1/SPEC.md` §10) stand. v2 decisions:

- **D-V2-1 — UI rebuilt on shadcn/Radix with the Space Worm design language.** Old (v1): hand-rolled components, top bar plus project tabs, SVG chart. Why: a consistent SaaS feel, accessible primitives, less custom UI code, and charts that scale to cycle history. The Vite/TanStack stack is unchanged.
- **D-V2-2 — Semver release versions + per-version checkpoints.** Old: one integer "project version" that incremented per stage and on release, shown to users as the version. New: users see the semver they chose, which changes only when a new version starts. Checkpoints keep rollback precise. Release doesn't create a checkpoint. Tags are the user's version (`v1.2.0`) instead of `v<N>-aide`.
- **D-V2-3 — Released versions are closed.** Old: released projects could roll back and re-release with suffixed tags. New: once released (merged and tagged), a version's checkpoints are read-only history, and fixes ship as a new version (e.g. 1.2.1). Vercel production can still be rolled back by promotion. Why: the merged tag is a fact on the default branch, and rolling AIDE's state back behind it would desync AIDE from Git. *Ben confirmed 2026-10-15.*
- **D-V2-4 — Cumulative iteration specs with requirement tags.** One current description of the system. Review scores new and changed requirements and flags regressions on unchanged ones. *Ben confirmed 2026-10-15.*
- **D-V2-5 — LLM keys are per user, bound to a project by an Owner.** Old: per-project key or system env key. New: a run always uses the bound Owner's key, whoever triggered it. System keys are off by default (`Llm:AllowSystemKeys`). v1 project keys migrate to connections, so no capability is lost. *Ben confirmed 2026-10-15.*
- **D-V2-6 — GitHub App, self-registered through the manifest flow, with PAT fallback.** Why: fine-grained, per-repo permissions instead of the broad `repo` scope; 1-hour installation tokens for background work; repo listing and creation; works on localhost with webhooks disabled. *Ben confirmed 2026-10-15.*
- **D-V2-7 — GitHub sign-in moves to the App's user flow.** Accounts are keyed by GitHub user id, so existing links survive. The OAuth App config remains as a fallback only when no App is registered, so nothing breaks for v1 installs.
- **D-V2-8 — Polling instead of webhooks** for merges, checks and deployments, because AIDE runs on localhost and isn't reachable from GitHub or Vercel. ETags and backoff keep rate-limit use low.
- **D-V2-9 — No `gh`/shell for agents; read-only repo tools instead.** Investigated: `gh` works non-interactively with `GH_TOKEN` in a container. However, an LLM with a shell and a token can do anything the token allows (arbitrary `gh api`), tokens leak via env and process listings, and `gh` has had token-exposure advisories. AIDE keeps all Git and GitHub writes in its own provider code, where the Guardian, path rules, audit and tests apply. Agents get credential-free, read-only, audited tools (FR-71). A sandboxed agent runner is a candidate for a later version.
- **D-V2-10 — Vercel via a user-pasted scoped token; Supabase stays manual.** "Sign in with Vercel" API permissions are in private beta, and a Vercel Integration (OAuth) would need each self-hoster to register one. AIDE triggers deployments explicitly, so it has deterministic ids. Preview build failure is review evidence. A production deploy failure doesn't undo a release. *Ben confirmed 2026-10-15.*
- **D-V2-11 — Breaking API change for versions.** `projectVersion` → `checkpoint`, and `/projects/:id/versions` now lists release versions (checkpoints move to `/checkpoints`). The web app and CLI ship together; `oasdiff` will report this and it is accepted here.
- **D-V2-12 — Presets are data in the repo, not a plugin system.** They are versioned and content-hashed, recorded on runs, and adapted from `claude-kits@14ada86`. More stacks are added by adding a folder plus tests, with no schema change.
- **D-V2-13 — Requirement IDs and acceptance criteria are mandatory in generated specs,** and plans cite them (`covers`). This brings the claude-kits spec discipline to every project, preset or not, and gives review a traceable coverage base.
- **D-V2-14 — Execution attempt history is stored** (`execution_step_attempt`). Old: only the latest violations were kept on the step.
- **D-V2-15 — Artifact restore creates a new version.** History is never rewritten (CLAUDE.md rule 10). "Rollback an artifact" is a new draft with the old content, approved as usual, and is only allowed while its stage is still open.

### 10.1 Build-time amendments (v2)

Behaviour that differs from, or adds to, the sections above. Each item is a decision logged in `PROGRESS.md`. Items already inline above (marked "decision") are not repeated.

**Versions and data (§3.2, §4.10, §5, §6)**
- *(2026-10-15, FR-67)* The migration assigns v1 rows to release versions by `created_at` window (version i = after release i-1, up to release i), not by checkpoint range, because rolled-back projects have overlapping ranges. Live state at or before the first post-release rollback target moves to the in-development version.
- *(2026-10-15, §5)* `projects.current_release_version_id` points at the latest released version when nothing is in development, so every version-scoped read has a default.
- *(2026-10-15, §5)* Release uniqueness is `(release_version_id, checkpoint, attempt)`, because checkpoints restart per version.
- *(2026-10-15, §4.10)* The `version_released` check runs before `INVALID_TARGET_VERSION`, so callers get the useful reason. The two v1 re-release-after-rollback behaviours are gone (D-V2-3).
- *(2026-10-15, §6)* `POST /versions` returns `UPSTREAM_ERROR → 502` when the repo's tags cannot be checked.
- *(2026-10-15, §6, D-V2-11)* The renames also cover rollback and gate fields: `targetVersion` becomes `targetCheckpoint`, and `fromVersion`/`toVersion` become `fromCheckpoint`/`toCheckpoint`.
- *(2026-10-15, §6)* The activity `releaseVersionId` filter is a time window (version start to the next version start), because events have no version column.
- *(2026-10-15, §6)* Artifact history accepts `allReleaseVersions=true` (additive) for the cross-version diff (FR-59).
- *(2026-10-16, FR-56)* Compliance reports carry an `id`. `GET .../reports/:cycle` also accepts `reportId`, and runs carry `complianceReportId`, because cycle numbers restart after a rollback. For the version in development, pre-rollback reports stay hidden (v1 §4.8).
- *(2026-10-15, FR-82)* The branch `aide/<slug>/v<version>` is stored on `release_versions.branch_name` when the working copy is first prepared. `VERSION_STARTED` records the intended name.
- *(2026-10-18, FR-55)* `RefinementDto` gains `rounds` (newest first, latest 50 per release version).
- *(2026-10-17, §7.2)* The projects list accepts `?preset=none` for projects without a preset.

**UI (§3.1, §7)**
- *(2026-10-15, FR-62)* Settings pages stay editable while a past version is shown, because settings are not versioned.
- *(2026-10-15, FR-92)* `ARTIFACT_RESTORED` also triggers the "artifact ready" notification, like `ARTIFACT_CREATED`.
- *(2026-10-15 and 2026-10-18, §7.1, NFR-A11Y2)* Token values were adjusted to pass WCAG AA: dark `muted-foreground` is L 0.63, dark `destructive-foreground` is near-black, light `success` is 0.50/0.15, and light `primary`, `ring` and `sidebar-primary` are oklch(0.44 0.15 195). A `field-border` token gives form fields a 3:1 border. The contrast test also checks tinted pairs. Reasons are in `frontend/src/lib/ui/tokens.ts`.
- *(2026-10-18, FR-46, FR-55)* v1 FR-46 live updates: the first project-hub join refetches the project's queries that are older than page open, restarting in-flight ones, so nothing is lost between page open and the join. A rejoin refetches all, and a failed rejoin still refetches. `RUN_COMPLETED` restarts artifact fetches. Stage empty states poll only while a producing run is active or a draft is expected.

**LLM connections (§3.6)**
- *(2026-10-16, FR-79)* `GET /api/projects/:id/settings/llm` lets every member see the binding ("connected by ..."). `GET /settings` stays Owner-only.
- *(2026-10-16, FR-79)* An Owner may unbind the connection. Only choosing a keyed provider with no usable key is `VALIDATION`. An undecryptable bound key gives `PROVIDER_NOT_CONFIGURED`, with no silent system-key fallback.
- *(2026-10-16, FR-92)* New event `LLM_BINDING_CLEARED {reason: owner_role_lost | member_removed | connection_deleted}`.
- *(2026-10-16, §2)* Admins see the system-key policy read-only at `GET /api/admin/integrations/llm`. It is set by `Llm:AllowSystemKeys`, not from the UI.

**GitHub (§3.7, §4.14)**
- *(2026-10-16, §5)* `github_connection` refresh fields are nullable (no refresh token when token expiry is off). A `github_app` project must have installation id, repo id and repo full name (check constraint).
- *(2026-10-16, FR-80, §8)* The manifest `state` is Data Protection time-limited (10 minutes) plus a single-use nonce bound to the Admin, held in memory. A restart means the Admin restarts the flow.
- *(2026-10-16, FR-86)* "Repo has code" is stored as the in-development version's `base_commit_sha` (the head) with no base tag ("Never released"), plus `repoHasCode` in `PROJECT_CREATED`. A repo with only GitHub starter files (README, LICENSE, .gitignore) counts as empty.
- *(2026-10-16, NFR-REL2)* On a GitHub rate limit, request paths return `429 GITHUB_RATE_LIMITED` with `Retry-After`. Jobs are re-queued at the reset without counting an attempt (capped at 1 hour), for PAT projects too.
- *(2026-10-16, FR-82)* Push access (push, maintain or admin, with the caller's own token) is checked when a project is bound to a repo (create, switch to App). It is not re-checked, so members keep working via the App on the binding Owner's behalf.
- *(2026-10-16, FR-84)* A `merge_readiness` job runs when the pre-release gate is created, and the result is cached 5 minutes (`MERGE_READINESS_CHECKED`). Without GraphQL access, `mergeStateStatus` and `reviewDecision` are null and required approvals count as missing.

**Iteration, import and repo tools (§3.3, §3.4, §4.13)**
- *(2026-10-16, §4.13)* `[removed]` requirements need no Given/When/Then. Regressions reported on `[new]`/`[changed]` IDs count as missing features; on removed or unknown IDs they are dropped. Import also rejects a development tag that already exists (`TAG_EXISTS`). For import, the project `idea` field carries the version request.
- *(2026-10-16, §4.13)* Plan warnings (`UNCOVERED_REQUIREMENTS`) are computed on read against the version's latest locked spec and returned as `ArtifactDto.warnings` for `implementation_plan`.
- *(2026-10-16, §8)* Local import (E2E and dev only): `git.mode=local` with `git.localSourcePath` resolved under `Git:Local:ImportRoot`; empty disables it. It clones with `--no-hardlinks` and is link-safe.
- *(2026-10-16, FR-73)* PAT import lists tags through the REST API with the token, not `git ls-remote`.
- *(2026-10-16, FR-71)* Every tool's output counts toward the 400 KB run cap. After `TOOL_LIMIT` (or 28 turns) the next turn is forced to answer (`tool_choice: none`; tools omitted for Ollama). `.aide/` reads as `NOT_FOUND` and `.git` as `UNSAFE_PATH`. Search runs on redacted text, and `search_code` has an optional `regex` flag. The no-tools fallback appends a depth-2 file listing. Snapshots live under `{temp}/aide-snapshots` (at most 8 per project and commit).
- *(2026-10-16, FR-70, FR-71)* Run warnings (`tools_unsupported`, `snapshot_unavailable`, `summary_wait_timeout`, `summary_unavailable`) are stored in the run output and the `RUN_COMPLETED` payload, and shown on `AgentRunDetailDto`.

**Release and merge (FR-64, FR-65)**
- *(2026-10-16, FR-65)* Changing `tagPrefix` returns `409 CONFLICT` while a version is `awaiting_merge` or a release attempt or job is in progress. A new prefix also applies to the in-development version. The release DTO `mergeStrategy` is the project's current strategy.
- *(2026-10-16, FR-65)* The merge poll is one self-deferring `merge_poll` job with the ETag and wait state in its payload. `PR_CLOSED` and `MERGE_TIMEOUT` return the project to status `releasing` in phase `release`, so an Owner can retry `POST /release`.
- *(2026-10-16, FR-65)* Local manual-merge detection: the merge commit is the oldest first-parent commit on the default branch that contains the branch head (the head itself after a fast-forward).
- *(2026-10-16, FR-64)* If GitHub refuses to create the GitHub Release (403, 404, 422), it is logged and the release completes without a URL. 5xx errors retry.

**Presets (§3.5, §4.11, §4.12)**
- *(2026-10-17, FR-74)* `POST /versions` without `presetId` keeps the base version's preset, re-pinned at the shipped version and hash. An explicit `null` means no preset. "Plan locked" for `PRESET_LOCKED` means any implementation plan of the version is locked or approved.
- *(2026-10-17, §4.11)* Format extensions: `planRules.maxFilesPerStep[] {glob, max}` (with a `oneMigrationPerStep` shorthand), `soft` and `reason` on order and exclusive rules, and protected paths as `{glob, existingOnBase, except, reason}`. Unknown properties are a load error. Globs without `/` match a file name at any depth.
- *(2026-10-17, §4.11)* With a preset, a step may declare a v1-protected path (`.github/workflows/**`, `.env.example`) or a preset-protected path only when its agent owns it (for example `devops-engineer`, or `db-engineer` for `lib/database.types.ts`). Lockfiles stay non-declarable. Without a preset the v1 rules are unchanged.
- *(2026-10-17 and 2026-10-18, §4.11, §4.12)* Migrations already on the version's base commit are immutable for everyone: agent plans, Owner-edited plans and the Guardian reject them. Preset paths match case-insensitively. Plans are validated against the base commit's file list (fetched if not local). If it cannot be listed, a generated plan may not create any migration. Optimisation plans accept catalogue keys or `generalist`, enforce protected paths and skip ordering rules.
- *(2026-10-17, §4.12)* Scaffolder steps are exempt from preset order rules. Soft order violations are the warning `PLAN_ORDER`.
- *(2026-10-17, §4.12)* Generated repos commit no lockfile (agents cannot run npm). Preset CI uses `npm ci` when a lockfile exists, else `npm install`. `db-engineer` writes `lib/database.types.ts` by hand in the generator's shape.
- *(2026-10-17, FR-77)* `no_secret_env_in_client` ignores `NODE_ENV`. Preset checklist items are numbered `[Cn]` in the review prompt, and a violation tagged `[Cn]` takes that item's category and severity with no penalty. Preset guidance sits in a `<stack_preset>` block.

**Vercel (§3.8, §4.15, §8)**
- *(2026-10-17, FR-90, §6)* A preview redeploy needs a writer (a Contributor can redeploy). A production redeploy and promote need an Owner.
- *(2026-10-17, FR-88)* An env var `PUT` saves locally and returns `502 UPSTREAM_ERROR` with `syncError` when the Vercel push fails; it is retried on the next save or deployment. `DELETE` is all-or-nothing (502, nothing deleted).
- *(2026-10-17, FR-88)* Local Git projects may use deployments only when `Deployments:Provider=Fake`. Vercel needs a GitHub-backed project.
- *(2026-10-17, FR-88)* Changing the Vercel provider or project needs the binding Owner or the caller's own connection (which rebinds to them). Other Owner actions act on the binder's behalf (§2).
- *(2026-10-17, §4.15)* A superseded preview row is marked `canceled` with a reason. Review waits for the newest preview row, created by the PR job from the PR head. Adopting Vercel's own production deployment of the merge SHA (within 30 seconds) happens only with the Vercel provider.
- *(2026-10-17, §4.15)* Deployment creation is idempotent: the AIDE row id goes in Vercel `meta.aideDeploymentId`. Retries adopt an existing deployment, and builds for rows stopped mid-create are cancelled.
- *(2026-10-17, NFR-SEC2)* Build-log tails are masked (project env values, the token and secret-shaped text) before storage and display. Only a 401, or a 403 `invalidToken`, invalidates a Vercel connection.
- *(2026-10-17, §11)* The Vercel API details (env create v10 upsert and edit/delete v9, promote without a rebuild, `/v5/user/tokens/current`, the `/v6/deployments` sha filter, the `/v3/deployments/{id}/events` shape, error-message matching for git-not-connected and plan limit) come from API knowledge and are not verified against a real account.

**Notifications and CLI (§3.9)**
- *(2026-10-17, FR-92)* Channels subscribed to `release_failed` or `review_completed` do not receive awaiting-merge, deployment-failed or preview-ready messages unless they opt into the new events. `connection_invalid` is personal (in-app and account email, never project channels; muting stops both).
- *(2026-10-17, FR-93)* The CLI disables System.CommandLine `@response-file` expansion, so `--request @file` reads the file (`@-` is stdin).

## 11. Open questions

- [ ] Sign in with Vercel: switch from pasted tokens once its API-request permissions leave private beta? (Proposed: revisit in v3.) — Ben
- [ ] Vercel Deployment Protection: previews behind Vercel Authentication need reviewers signed in to Vercel. Should AIDE use a "Protection Bypass for Automation" secret per project to fetch preview status pages? (Proposed: not needed in v2; AIDE only reads the deployment API.) — Ben
- [ ] Abandon a version in development (e.g. scrap 1.2.0 and start 1.2.1 instead)? It isn't requested for v2. Rollback to checkpoint 1 covers most cases. (Proposed: v3.) — Ben
- [ ] Should `nextjs-supabase` later provision Supabase (Management API) and run `supabase db push` on release? Out of scope for v2 by decision D-V2-10. — Ben
- [ ] Build-time checks to confirm against current vendor docs (not questions for Ben):
  - `vercel.json` `git.deploymentEnabled` glob syntax (built as `{"aide/**": false}`; not yet confirmed live)
  - `POST /v10/projects/{id}/env` vs `/v9`
  - promote without a rebuild
  - GraphQL `mergeStateStatus`/`reviewDecision`/`viewerCanMergeAsAdmin` availability for App installation tokens
  - whether a GitHub App installation token can create repos on a personal account (the spec uses the user token for this)

## 12. Milestones

**M0 Baseline (replaces the kit's scaffold phase):**
- The repo, CI, deploy scripts and the v1 app already exist. **Do not re-scaffold.**
- Branch `build/aide-v2` from `main`. Run the full gate and `deploy-local.sh`, and record the baseline in a new root `PROGRESS.md` (v1's is in `docs/v1/`).

1. **M9 Design system + shell:**
   - Tokens, fonts, shadcn primitives, logo components, favicon, sidebar/header/drawer, command palette, nav config, restyle of all v1 pages onto the new primitives, project-mode sidebar and stage stepper (FR-53, FR-54).
   - Includes the token contrast test and the axe pass.
2. **M10 Versions + checkpoints:**
   - Migration and backfill, domain rules §4.10, checkpoint API change, release-version endpoints, badges, version switcher and read-only past views (FR-60–FR-63, FR-66, FR-67).
   - Release semver tagging (FR-64, Local first).
3. **M11 Stage pages + history:**
   - Per-stage pages, execution runs with attempt history, review cycles with the cycle picker and recharts trend, artifacts page and restore (FR-55–FR-59).
4. **M12 Connections:**
   - LLM credentials and binding (FR-78, FR-79).
   - GitHub App registration, sign-in, user connection, repo picker and creation, installation tokens, PAT fallback, `github_app` provider (FR-80–FR-83, FR-86).
5. **M13 Iteration + import:**
   - Repository Analyst, repo tools across providers (Anthropic/OpenAI tool use, Fake scripted, Ollama fallback), iteration refinement, cumulative spec validation, regression scoring, import wizard (FR-70–FR-73, §4.13).
   - Merge strategies, awaiting merge, merge poll, GitHub Releases, merge readiness, check runs (FR-64 GitHub part, FR-65, FR-84, FR-85).
6. **M14 Presets:**
   - Preset loader and validation, the `nextjs-supabase` and `nextjs-frontend` content (adapted from claude-kits), wizard Stack step, spec/plan/code/review integration, preset deterministic checks (FR-74–FR-77).
7. **M15 Vercel:**
   - Connection, deployment settings and env sync, preview and production deployments, promote, Deployments page, Fake provider (FR-87–FR-91).
8. **M16 Integrate & harden:**
   - Events and notifications (FR-92), CLI (FR-93), the two-version E2E, import E2E and preset E2E, axe, perf, README and SPEC sync, spec-verifier over **v1 and v2 requirements**.

## Revision notes

- 2026-10-15: v2 spec created. The v1 spec and build log moved to `docs/v1/`. Decisions D-V2-3, -4, -5, -6 and -10 were confirmed with Ben in the spec interview. Research sources: GitHub Apps docs (permissions, user and installation tokens, manifest flow, rules API), Vercel REST API (projects, deployments, env, promote, access tokens, limits), `BenFielder1/claude-kits@14ada86`, and Space Worm Analytics UI.
