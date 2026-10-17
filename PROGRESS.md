# Build progress (v2)

Spec: `SPEC.md` (v2; v1 requirements FR-1–FR-52 from `docs/v1/SPEC.md` stay in force unless amended, §3.0) · Branch: `build/aide-v2` · Started: 2026-10-15 · v1 log: `docs/v1/PROGRESS.md`

## Environment
- .NET SDK: 10.0.401 · Node: v24.21.0 · Docker: 29.8.2 (Docker Desktop) · gh: installed
- Kubernetes: `docker-desktop` context present + reachable (node Ready, v1.36.1)
- Optional tools: kubeconform, oasdiff, actionlint — not installed (CI runs kubeconform; actionlint via Docker if needed)
- Local app URL: http://localhost:8080 · Dev loop: API :5080, Vite :5173, DB :5432

## Requirements
Spec IDs are used directly (FR-n). NFR rows use the spec section. v1 rows live in `docs/v1/PROGRESS.md`; amended v1 IDs are listed here when their amendment lands.

| ID | Summary | Spec § | Area | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-53 | Design system + app shell (sidebar, header, ⌘K, logo) | 3.1, 7.1 | frontend | 1 | done | tokens + contrast test, shell/nav-config, restyled pages; FR53_* tests; e2e/a11y.spec |
| FR-54 | Project-mode sidebar, stage stepper, next action | 3.1 | frontend | 1 | done (badges wired in phase 2) | stage-states.ts, StageStepper, next-action.ts; FR54_* tests |
| FR-55 | One page per stage, `?version=` read-only | 3.1 | both | 3 | done | six stage pages, in-place approvals, read-only past versions; FR55_* tests |
| FR-56 | Review page: every cycle, cycle picker, recharts trend | 3.1 | both | 3 | done | cycles table + ?cycle/&report, category bars, trend select; FR56_* tests |
| FR-57 | Execution page: every run, attempt history | 3.1 | both | 3 | done | ExecutionRunReader, attempt history; FR57_*/D_V2_14_* tests incl. 10×30 perf |
| FR-58 | Restore artifact version as new draft | 3.1 | both | 3 | done | RestoreRules + dialog; FR58_* tests |
| FR-59 | Artifacts page per release version; cross-version diff | 3.1 | both | 3 | done | artifacts per release version, allReleaseVersions diff; FR59_* tests |
| FR-60 | Start new version after merged release | 3.2 | both | 2 | done | POST /versions, start dialog; FR60_* tests |
| FR-61 | Version validation errors | 3.2, 4.10 | backend | 2 | done | SemVer + VERSION_* errors; FR61_* tests |
| FR-62 | Released + in-development badges, version switcher, read-only past | 3.2 | both | 2 | done | badges, switcher, ?release= read-only, list filter; FR62_* tests |
| FR-63 | Checkpoints per release version; rollback scoped; released closed | 3.2, 4.10 | both | 2 | done | checkpoints, scoped rollback, version_released; FR63_*/FR44_amended_* tests |
| FR-64 | Release tags user semver; GitHub Release (GitHub part phase 7) | 3.2 | backend | 2, 7 | done | DecideTag, GitHub Release + ReleaseNotes; FR64_*/FR42_amended_by_FR64_* tests |
| FR-65 | Merge strategy, awaiting_merge, merge poll | 3.2, 4.14 | both | 7 | done (stub-verified) | merge strategies, awaiting_merge, merge_poll job; FR65_*/S4_14_* tests |
| FR-66 | Initial version for new projects | 3.2 | both | 2 | done | initialVersion + wizard Version step; FR66_* tests |
| FR-67 | v1 project migration to release versions | 3.2, 4.10 | backend | 2 | done | migration ReleaseVersions backfill; FR67_* migration tests; applied on k8s |
| FR-70 | Repository Analyst → codebase_summary (auto-locked) | 3.3 | backend | 6 | done | Repository Analyst job → locked codebase_summary; FR70_* tests |
| FR-71 | Read-only repo tools (tool use, limits, path safety, redaction) | 3.3 | backend | 6 | done | RepoToolSession + LlmToolLoop + snapshots; FR71_* tests |
| FR-72 | Iteration refinement inputs | 3.3 | backend | 6 | done | iteration refinement inputs; FR72_* tests |
| FR-73 | Import existing repo wizard | 3.4 | both | 6 | done | import (App, PAT, Local) + wizard; FR73_* tests |
| FR-74 | Presets: data bundles, wizard Stack step, pinned per version | 3.5, 4.11 | both | 8 | done | preset catalog + API + pinning + run stamping; FR74_* tests |
| FR-75 | Preset spec sections; mandatory FR IDs + GWT | 3.5, 4.13 | backend | 6, 8 | done | SpecValidator + preset sections retry; FR75_* tests |
| FR-76 | Plan steps carry agent + covers; preset plan validation | 3.5, 4.11 | both | 8 | done | plan rules + specialist execution; FR76_* tests |
| FR-77 | Preset review checklist + deterministic checks | 3.5 | backend | 8 | done | checklist + rls_on_every_table + no_secret_env_in_client; FR77_* tests |
| FR-78 | User LLM connections | 3.6 | both | 4 | done | me/connections/llm CRUD + test, list-models validation; FR78_* tests |
| FR-79 | Bind Owner's LLM connection to project | 3.6 | both | 4 | done | binding + clearing rules under project lock, runs use bound key, Llm:AllowSystemKeys; FR79_* tests |
| FR-80 | GitHub App manifest registration + App sign-in | 3.7 | both | 5 | done (stub-verified) | manifest flow + App sign-in PKCE (stub HTTP); FR80_* tests |
| FR-81 | User GitHub connection, repo picker, create repo | 3.7 | both | 5 | done (stub-verified) | connection + refresh, picker, create repo; FR81_* tests |
| FR-82 | `github_app` provider, installation tokens, per-version branch | 3.7 | backend | 5 | done (stub-verified) | github_app provider, scoped installation tokens ≤55 min; FR82_*/S4_14_* tests |
| FR-83 | PAT fallback | 3.7 | both | 5 | done (stub-verified) | PAT mode + switch-to-app with push check; FR83_* tests |
| FR-84 | Merge readiness | 3.7 | both | 7 | done (stub-verified) | MergeReadinessService (branch rules + GraphQL, fallback); FR84_* tests |
| FR-85 | Check runs as review input | 3.7 | backend | 7 | done (stub-verified) | required check runs → testing finding, 15-min wait; FR85_* tests |
| FR-86 | Wizard Repository step | 3.7 | frontend | 5 | done (stub-verified) | wizard Repository step, has-code warning; FR86_* tests |
| FR-87 | Vercel connection | 3.8 | both | 9 | todo | — |
| FR-88 | Deployment settings + env vars | 3.8 | both | 9 | todo | — |
| FR-89 | vercel.json disables aide/** auto deploys | 3.8 | backend | 9 | todo | — |
| FR-90 | Preview deployments in review | 3.8 | both | 9 | todo | — |
| FR-91 | Production deployments, promote, Deployments page | 3.8 | both | 9 | todo | — |
| FR-92 | New events + notification types | 3.9 | both | 10 | todo | — |
| FR-93 | CLI additions | 3.9 | backend | 10 | todo | — |
| §4.10 | Release versions, semver, checkpoints, rollback scope, migration | 4.10 | backend | 2 | done | SemVer, ReleaseVersionRules, NewVersion transition matrix; S4_10_* tests |
| §4.11–4.12 | Preset format and the two presets | 4.11 | backend | 8 | done | Presets/{nextjs-supabase,nextjs-frontend}, PresetLoader, PresetPathRules; S4_11_*/S4_12_* tests |
| §4.13 | Iteration specs, review scope, plan scope | 4.13 | backend | 6 | done | SpecValidator tags, IterationReviewScope, PlanScope; S4_13_* tests |
| §4.14 | GitHub permissions, tokens, polling, merge detection | 4.14 | backend | 5, 7 | todo | — |
| §4.15 | Deployments (Vercel) states, polling, Fake provider | 4.15 | backend | 9 | todo | — |
| NFR-SEC2 | Secret encryption, scoped tokens, fixed hosts, signed state | 8 | backend | 4–9 | todo | — |
| NFR-REL2 | Polls as jobs, restart resume, rate-limit backoff | 8 | backend | 7, 9 | todo | — |
| NFR-PERF2 | List p95 < 300 ms with badges; runs/cycles p95 < 300 ms | 8 | backend | 10 | todo | — |
| NFR-A11Y2 | WCAG AA both themes (token test), keyboard nav, chart tables, axe | 8 | frontend | 1, 10 | todo | — |
| NFR-TEST2 | Domain worked-example tests, stub HTTP, two-version/import/preset E2E | 8 | both | all, 10 | todo | — |
| DEP-V2 | Public base URL, optional App secrets, dev seed, Fake deployments | 9 | deploy | 2, 5, 9, 10 | todo | — |

## Phases
- [x] 0 M0 Baseline (base: af23fae) — gate green (backend 2193 tests, frontend 335 tests, manifests OK) · deploy-local + smoke ✓ on docker-desktop
- [x] 1 M9 Design system + shell (base: 8d174d0) — IDs: FR-53, FR-54, NFR-A11Y2 (tokens)
  - [x] vite-react-engineer (tokens, fonts, shadcn primitives, logo, shell, nav config, ⌘K — 427 frontend tests; placeholder routes connections, admin/integrations, specification, planning, release, activity)
  - [x] vite-react-engineer (restyle v1 pages, stage stepper, next-action helper, settings sub-routes, recharts trend, axe e2e specs — 453 frontend tests)
  - [x] gate (frontend ✓) · review (0 Critical, 1 Major, 6 Minor — all fixed or logged) · commit — redeploy deferred to end of phase 2 (backend M10 in flight in the same tree)
- [x] 2 M10 Versions + checkpoints (base: e8a957e) — IDs: FR-60–FR-64 (Local), FR-66, FR-67, §4.10
  - [x] ef-data-engineer (migration ReleaseVersions + FR-67 backfill; SQL reviewed — see Decisions; 4 FR67_* migration tests; checked on a scratch copy of the k8s DB, 172 projects)
  - [x] aspnet-engineer (SemVer + ReleaseVersionRules, NewVersion transition, scoped rollback, /versions + /checkpoints, D-V2-11 renames incl. rollback/gate targetCheckpoint, Local+GitHub tag lookup, seed released at 0.1.0 — 2441 backend tests)
  - [x] vite-react-engineer (client regen, D-V2-11 renames, badges, switcher + read-only, start-version dialog, checkpoint rollback, wizard Version step — 479 frontend tests)
  - [x] gate (backend 2444, frontend 481, manifests, contract fresh) · review (0 Critical, 0 Major, 2 Minor — both fixed) · redeploy (migration ReleaseVersions applied on k8s: 56 released + 152 in-development versions, every project has a current version; smoke ✓) · commit
- [x] 3 M11 Stage pages + history (base: 4c68a68) — IDs: FR-55–FR-59
  - [x] aspnet-engineer (contract stubs: RestoreArtifactVersion, ListExecutionRuns, ListStepAttempts, ListComplianceReports, GetComplianceReport; oasdiff additive only)
  - [x] aspnet-engineer (restore rules, attempt recording, runs/reports readers with fixed query count + <300 ms test, history `allReleaseVersions` — 2503 backend tests)
  - [x] vite-react-engineer (stage pages, runs/attempts, review cycles, restore, artifacts across versions — first agent interrupted by session end, finished by a fresh agent; 511 frontend tests)
  - [x] gate (backend 2507, frontend 511, manifests, contract fresh + additive) · review (0 Critical, 1 Major, 4 Minor — all fixed) · redeploy (smoke ✓) · commit
- [x] 4 M12a LLM connections (base: 816ff13) — IDs: FR-78, FR-79
  - [x] ef-data-engineer (migration LlmCredentials: llm_credentials, binding columns, agent_runs FK, v1 keys → earliest Owner's connection; last4 startup backfill; FR79_* migration tests)
  - [x] aspnet-engineer (connections API, list-models validation, binding + clearing rules, run credential, Llm:AllowSystemKeys, admin LLM policy, GateDto.releaseVersionId — 2579 backend tests; oasdiff: only apiKey/clearApiKey removed from PATCH /settings/llm, accepted by FR-79)
  - [x] vite-react-engineer (Connections page LLM section, Settings → LLM binding, admin system-key panel — 527 frontend tests)
  - [x] gate (backend 2584, frontend 527, manifests, contract fresh) · review (0 Critical, 0 Major, 4 Minor — all fixed/logged: binding lock, 15 s key-check timeout `Llm:KeyCheckTimeout`, key cleared from MutationCache) · redeploy (migration LlmCredentials applied; k8s DB had no v1 keys; smoke ✓) · commit
- [x] 5 M12b GitHub App (base: de44304) — IDs: FR-80–FR-83, FR-86, §4.14 (tokens)
  - [x] ef-data-engineer (migration GitHubApp: github_apps singleton, github_connections, git settings installation/repo ids, provider github → github_pat; new secret purposes)
  - [x] aspnet-engineer (contract stubs; oasdiff: only the accepted GitProvider rename)
  - [x] aspnet-engineer (manifest flow, App sign-in PKCE, user connection + refresh, repo picker/create, installation tokens, github_app provider, rate-limit deferral — 2724 backend tests) ‖ vite-react-engineer (admin GitHub, connections GitHub, wizard Repository step, settings Git — 555 frontend tests)
  - [x] gate (backend 2729, frontend 561, manifests, contract fresh) · review (1 Critical, 2 Major, 3 Minor — all fixed, Critical/Major re-verified: switch-to-app/create now require the caller's own push access; refresh serialised) · redeploy (migration GitHubApp applied; smoke ✓) · commit
- [x] 6 M13a Iteration + import (base: 784fc18) — IDs: FR-70–FR-73, §4.13, FR-75 (FR IDs part)
  - [x] aspnet-engineer A (§4.13 + FR-75 domain rules, import contract; oasdiff additive) ‖ aspnet-engineer B (repo tools, snapshots, LlmToolLoop for Anthropic/OpenAI/Ollama fallback, Fake scripts — ~98 FR71_* tests)
  - [x] aspnet-engineer C (Repository Analyst job, refinement wait + iteration inputs, spec/review/plan integration, import implementation — 2938 backend tests) ‖ vite-react-engineer (import wizard, codebase summary, tool calls, e2e wizard selectors — 573 frontend tests)
  - [x] gate (backend 2945, frontend 579, manifests, contract fresh, no vulnerable packages) · review (0 Critical, 3 Major, 4 Minor — all fixed: linear-time regex + per-line budget, tool-loop tokens counted + mid-loop budget stop, exact tag lookups + paginated picker, link-safe Local import, snapshot cleanup, /tmp sizeLimit 6Gi, tool error copy in errors.ts) · redeploy (smoke ✓) · commit
- [x] 7 M13b Merge strategies + GitHub release (base: 77ddc9a) — IDs: FR-64 (GitHub), FR-65, FR-84, FR-85
  - [x] aspnet-engineer (contract: GetMergeReadiness stub, git settings mergeStrategy/tagPrefix implemented, release fields, ExternalChecksDto, ProjectStatus awaiting_merge)
  - [x] aspnet-engineer (merge strategies, awaiting_merge, merge poll, GitHub Release + notes, merge readiness, check runs in review — 3162 backend tests) ‖ vite-react-engineer (settings Git, Release page readiness/awaiting/GitHub Release, review external checks — 601 frontend tests)
  - [x] gate (backend 3165, frontend 619, manifests) · review (0 Critical, 1 Major, 4 Minor — all fixed: rate-limited merge defers, tag reuse only at merge commit + annotated-tag commit resolution, merged-externally flag, readiness refresh throttle, tag-prefix client rule; backend 3165, frontend 619) · redeploy (smoke ✓; first attempt 2026-10-16 was stopped for low system memory, rerun 2026-10-17) · commit
- [x] 8 M14 Presets (base: c4a8565) — IDs: FR-74–FR-77, §4.11–4.12
  - [x] aspnet-engineer A (preset model, loader, hash, readiness check, plan rules, preset API — 3248 backend tests; oasdiff additive) ‖ aspnet-engineer B (preset content from claude-kits@14ada86: 7/6 specialist prompts, skills, conventions)
  - [x] aspnet-engineer C (spec/plan/code/review integration, deterministic preset checks, Guardian protected paths, Fake specialist scripts, "Demo: Recipe Finder" seed — 3300 backend tests; resumed once after a usage-limit stop) ‖ vite-react-engineer (wizard Stack step, preset chips + filter, change preset, start-version preset, planning agents/warnings, admin presets — 635 frontend tests)
  - [x] gate (backend 3306, frontend 635, manifests, contract fresh, oasdiff additive) · review (0 Critical, 2 Major, 3 Minor — all fixed: base-commit immutable migrations enforced in plan + Guardian + Owner edits, case-insensitive preset paths, prompt wording, decision log) · redeploy (readiness: presets 2 loaded; smoke ✓) · commit
- [ ] 9 M15 Vercel — IDs: FR-87–FR-91, §4.15
- [ ] 10 M16 Integrate & harden — IDs: FR-92, FR-93, NFR-*2, E2E
- [ ] Final — spec-verifier (v1 + v2) → fixes → github-ci-engineer → readme-writer

## Decisions
- 2026-10-15 — M12 and M13 are each split into two phases (connections/GitHub App; iteration/merge) — keeps phases at 2–5 tasks — SPEC updated: no
- 2026-10-15 — M10 migration `20261015174144_ReleaseVersions`: v1 rows are assigned to release versions by `created_at` window (version i = (released_at[i-1], released_at[i]]) instead of checkpoint range, because projects rolled back after a release have overlapping checkpoint ranges; live state at checkpoint ≤ the first post-release rollback target moves to the in-development version — FR-67 — SPEC updated: no
- 2026-10-15 — `projects.current_release_version_id` points at the latest released version when nothing is in development (spec said nullable when only released) — lets every version-scoped read resolve a default version — SPEC updated: no
- 2026-10-15 — FK `projects.current_release_version_id` is DEFERRABLE INITIALLY DEFERRED and created in SQL (not mapped in EF): project and its first version reference each other and are inserted in one save — SPEC updated: no
- 2026-10-15 — Releases uniqueness is (release_version_id, checkpoint, attempt), was (project_id, version, attempt): checkpoints restart per version — SPEC updated: no
- 2026-10-15 — Branch/PR fields moved from project_git_settings to release_versions (per §5); `ReleaseVersionStampingInterceptor` stamps missing release_version_id with the project's current version as a safety net — to be removed once all writers set it explicitly — SPEC updated: no
- 2026-10-15 — ReleaseVersions migration is not rolling-update safe (renames/drops); fine with the Recreate strategy, but the migrate Job runs while v1 pods are up — expect transient errors until the rollout — SPEC updated: no
- 2026-10-15 — Three Space Worm token values were adjusted to pass WCAG AA (dark muted-foreground L 0.57→0.59, dark destructive-foreground near-black, light success 0.50/0.15) and a `field-border` token added for 3:1 form-field borders; reasons in `frontend/src/lib/ui/tokens.ts` — NFR-A11Y2 — SPEC updated: no
- 2026-10-15 — Theme class is set before first paint by same-origin `public/theme-init.js` (no inline script) so the v1 CSP is unchanged — SPEC updated: no
- 2026-10-15 — Projects-list "Awaiting gates" KPI shows "—": the list API returns no gate counts; add a count to the list DTO if wanted — §7.2 — SPEC updated: no
- 2026-10-15 — D-V2-11 renames also cover rollback/gate fields (`targetVersion`→`targetCheckpoint`, `fromVersion/toVersion`→`fromCheckpoint/toCheckpoint`); oasdiff (Docker): 58 errors + 3 warnings, all from D-V2-11 renames, `/versions` meaning change and new enum values (`codebase_summary`, `awaiting_merge`) — accepted per D-V2-11 — SPEC updated: no
- 2026-10-15 — `repository_analysis` job is a placeholder (logs and completes) until FR-70 lands in phase 6; non-null `presetId` → PRESET_NOT_FOUND until presets land in phase 8 — SPEC updated: no
- 2026-10-15 — Activity `releaseVersionId` filter is a time window (version start → next version start); events have no version column — SPEC updated: no
- 2026-10-15 — Closed-version rollback checks (`version_released`) run before INVALID_TARGET_VERSION so callers get the useful reason; the two v1 re-release-after-rollback tests were removed (behaviour removed by D-V2-3) — SPEC updated: no
- 2026-10-15 — UPSTREAM_ERROR 502 on POST /versions when the repo's tags can't be checked — SPEC updated: no
- 2026-10-15 — Per-version branch `aide/<slug>/v<version>` (all new versions incl. first) is stored on release_versions.branch_name when the working copy is first prepared, not at version start: a stored branch name is the lifecycle's "working copy prepared" fact; VERSION_STARTED records the intended name — FR-82 — SPEC updated: no
- 2026-10-15 — Past-version selection uses `?release=<semver>` (spec FR-55 says `?version=`) because artifact pages already use `?version=N` for the artifact version; kept across project navigation — SPEC updated: no (sync in Final)
- 2026-10-15 — Settings pages stay editable while viewing a past version (settings aren't versioned) — SPEC updated: no
- 2026-10-15 — AgentRunDto.checkpoint is derived on read (produced artifact → compliance report → null; step runs null) — no migration — SPEC updated: no
- 2026-10-15 — Execution run status: a started, unpaused run with no queued job shows `paused` (PlanRunStatus has no idle value); a cycle's optimisation plan is matched by optimization_plan_artifact_id, else the latest report at or before the plan — SPEC updated: no
- 2026-10-15 — ARTIFACT_RESTORED also triggers the "artifact ready" notification (same as ARTIFACT_CREATED) — SPEC updated: no
- 2026-10-15 — Artifact history gains optional `allReleaseVersions=true` (additive) for FR-59 cross-version diff — SPEC updated: no
- 2026-10-16 — Restore reason `stage_not_reached` added (project before the producing phase, e.g. after a rollback); spec listed only stage_completed — FR-58 — SPEC updated: no
- 2026-10-16 — Compliance reports carry `id`; GetComplianceReport accepts `reportId` and runs carry `complianceReportId`, because cycle numbers restart after a rollback within one release version. For the version in development, pre-rollback reports stay hidden (v1 §4.8 rule kept); duplicates show only in past-version history — FR-56 — SPEC updated: no
- 2026-10-16 — Migrated v1 project LLM keys keep the shared v1 Data Protection purpose `Aide.Secrets.v1` (now `SecretPurposes.LlmApiKey`) so ciphertext is reused without re-encryption; new secret kinds (GitHub tokens, App key, Vercel tokens, env values) get their own purposes per §8 — SPEC updated: no
- 2026-10-16 — Migrated credentials get last4 `????` in SQL, filled by an idempotent startup backfill (LlmCredentialLast4Backfill) that decrypts in-process — SPEC updated: no
- 2026-10-16 — `GET /api/projects/:id/settings/llm` added so all members can see the binding ("connected by …") while `GET /settings` stays Owner-only — FR-79 — SPEC updated: no
- 2026-10-16 — An Owner may unbind the project's LLM connection; only choosing a keyed provider with no usable key is VALIDATION. An undecryptable bound key → PROVIDER_NOT_CONFIGURED (no silent system-key fallback) — FR-79 — SPEC updated: no
- 2026-10-16 — New event `LLM_BINDING_CLEARED {reason: owner_role_lost|member_removed|connection_deleted}` (not in FR-92's list); "LLM binding cleared" / "connection invalid" notification types deferred to phase 10 (FR-92) — until then a rejected key reaches Owners via RUN_FAILED — SPEC updated: no
- 2026-10-16 — Admins see the system-key policy read-only (`GET /api/admin/integrations/llm`); it is set by config `Llm:AllowSystemKeys`, not from the UI — §2 Admin — SPEC updated: no
- 2026-10-16 — LlmCredentials migration drops `project_llm_settings.api_key_encrypted` (not rolling-update safe): old pods error on LLM settings reads and agent jobs until the Recreate rollout finishes — acceptable locally, same as ReleaseVersions — SPEC updated: no
- 2026-10-16 — Git provider wire value `github` renamed to `github_pat` (spec FR-82 name), migrated in SQL; breaking enum change accepted in the D-V2-11 spirit (web + CLI ship together); CLI flag `--git-provider github` still maps to `github_pat`. Migration GitHubApp is not rolling-update safe (old pods can't parse `github_pat`) — SPEC updated: no
- 2026-10-16 — github_connections refresh fields are nullable (no refresh token when the App has token expiration off); check constraint: a `github_app` project must have installation_id, repo_id and repo_full_name — SPEC updated: no
- 2026-10-16 — v1 GitHub links live in `users.github_id`; App sign-in matches on the same column (FR-80) — SPEC updated: no
- 2026-10-16 — Manifest `state` = Data Protection time-limited (10 min) + single-use nonce bound to the Admin, held in memory (a restart means the Admin restarts the flow) — FR-80, §8 — SPEC updated: no
- 2026-10-16 — FR-86 "repo has code" is stored as the in-development release version's `base_commit_sha` (head) with no base tag (= "Never released"), plus `repoHasCode` in PROJECT_CREATED; a repo with only GitHub starter files (README/LICENSE/.gitignore) counts as empty so `auto_init` repos aren't flagged — SPEC updated: no
- 2026-10-16 — GitHub rate limits: request paths → 429 GITHUB_RATE_LIMITED + Retry-After; jobs are re-queued at the reset without counting an attempt (capped at 1 h), also for PAT projects — NFR-REL2 — SPEC updated: no
- 2026-10-16 — Repo creation uses the user token for both user and org owners (installation tokens can't create repos on personal accounts; §11 check, from GitHub docs, not fetched live) — SPEC updated: no
- 2026-10-16 — Repo push access (push/maintain/admin with the caller's own token) is checked when a project is bound to a repo (create, switch-to-app); it isn't re-checked later, so members keep working via the App on the binding Owner's behalf (§2) even if that Owner later loses GitHub access — SPEC updated: no
- 2026-10-16 — Security: NU1903 advisory on Microsoft.AspNetCore.SignalR.Common 10.0.12 (published mid-build) → ASP.NET Core packages bumped to 10.0.13 (OpenApi, Identity.EFCore, Mvc.Testing, SignalR.Client); Docker images use the floating `10.0` tags so the runtime is patched on rebuild — SPEC updated: no
- 2026-10-16 — §4.13 rule details: `[removed]` requirements need no Given/When/Then; regressions reported on [new]/[changed] IDs count as missing features, on removed/unknown IDs are dropped; import also rejects a development tag that already exists (TAG_EXISTS); for import the project `idea` field carries the version request (no separate `request` field) — SPEC updated: no
- 2026-10-16 — Local import (E2E/dev only): `git.mode=local` + `git.localSourcePath` resolved under `Git:Local:ImportRoot` — §8 Testing — SPEC updated: no
- 2026-10-16 — Repo tools details: every tool's output counts toward the 400 KB run cap; after TOOL_LIMIT (or 28 turns) the next turn is forced to answer (`tool_choice: none`; tools omitted for Ollama); `.aide/` reads as NOT_FOUND and `.git` as UNSAFE_PATH (v1 path rules); search runs on redacted text; `search_code` has an optional `regex` flag (plain text case-insensitive); the no-tools fallback appends a depth-2 file listing; snapshots under `{temp}/aide-snapshots` (≤8 per project/commit) — FR-71 — SPEC updated: no
- 2026-10-16 — Run warnings (tools_unsupported, snapshot_unavailable, summary_wait_timeout, summary_unavailable) are stored in the run output + RUN_COMPLETED payload and exposed on AgentRunDetailDto (no column, no migration) — FR-70/71 — SPEC updated: no
- 2026-10-16 — Refinement waits for the codebase summary by re-queuing its job (JobDeferredException, attempt not counted) up to 10 min from version start — FR-70 — SPEC updated: no
- 2026-10-16 — Plan warnings (UNCOVERED_REQUIREMENTS) are computed on read against the version's latest locked spec and returned as ArtifactDto.warnings for implementation_plan — §4.13 — SPEC updated: no
- 2026-10-16 — PAT import lists tags via the REST API with the token (not git ls-remote); Local import clones `--no-hardlinks` from under `Git:Local:ImportRoot` (empty = disabled) — FR-73 — SPEC updated: no
- 2026-10-16 — AgentKind enum gains `repository_analyst` (response enum value added; oasdiff flags it — accepted, same precedent as D-V2-11) — SPEC updated: no
- 2026-10-16 — ProjectStatus enum gains `awaiting_merge` and ComplianceReportDetailDto.externalChecks becomes typed ExternalChecksDto (oasdiff: enum-value-added + one-of-added, accepted — FR-65/FR-85) — SPEC updated: no
- 2026-10-16 — Changing tagPrefix is blocked (409 CONFLICT) while a version is awaiting_merge or a release attempt/job is in progress; a new prefix also applies to the in-development version. Release DTO `mergeStrategy` is the project's current strategy (not stored per attempt) — SPEC updated: no
- 2026-10-16 — Merge poll is one self-deferring `merge_poll` job with the ETag and wait state in its payload (restart-safe). PR_CLOSED / MERGE_TIMEOUT return the project to status `releasing` in phase release so an Owner can retry `POST /release` — FR-65 — SPEC updated: no
- 2026-10-16 — Local manual-merge detection: the merge commit is the oldest first-parent commit on the default branch that contains the branch head (the head itself after a fast-forward) — FR-65 — SPEC updated: no
- 2026-10-16 — GitHub refusing GitHub Release creation (403/404/422) is logged and the release completes without a URL (5xx retries) — FR-64 — SPEC updated: no
- 2026-10-16 — Merge readiness is computed by a `merge_readiness` job when the pre-release gate is created and cached 5 min (MERGE_READINESS_CHECKED event); without GraphQL access, mergeStateStatus/reviewDecision are null and required approvals count as missing (§11 check) — FR-84 — SPEC updated: no
- 2026-10-16 — Until phase 10 adds the dedicated type, "release awaiting merge" notifications reuse `release_failed` (Owners) with awaiting-merge wording — FR-92 — SPEC updated: no
- 2026-10-17 — Preset content adapted from claude-kits@14ada86 (local marketplace clone at that exact commit). FR-89 `vercel.json` uses `{"git":{"deploymentEnabled":{"aide/**":false}}}` — confirm against Vercel docs/live project that branch keys are globs and API-triggered gitSource deploys are unaffected (§11 check) — SPEC updated: no
- 2026-10-17 — With a preset, a plan step may declare a v1-protected path (`.github/workflows/**`, `.env.example`) only when the step's agent owns it (e.g. devops-engineer, project-scaffolder), consistent with §4.14 Workflows RW "when a step declares it"; lockfiles stay non-declarable (preset protectedPaths: see the later 2026-10-17 entry — owner may declare, except migrations existing on base); without a preset v1 rules are unchanged — §4.11 — SPEC updated: no
- 2026-10-17 — Generated repos commit no lockfile (AIDE agents can't run npm); preset CI uses `npm ci` when a lockfile exists else `npm install`; db-engineer writes `lib/database.types.ts` by hand in the generator's shape — SPEC updated: no
- 2026-10-17 — Preset format extensions beyond §4.11: `planRules.maxFilesPerStep[] {glob, max}` (`oneMigrationPerStep` shorthand), `soft`/`reason` on order/exclusive rules, protected paths `{glob, existingOnBase, except, reason}`, unknown properties = load error; globs without `/` match a file name at any depth; dependency-free glob matcher in Domain — §4.11 — SPEC updated: no
- 2026-10-17 — Scaffolder steps are exempt from preset order rules (otherwise step 1's `app/layout.tsx` breaks "migrations before app/**"); soft order violations are warning `PLAN_ORDER` (spec names no code) — §4.12 — SPEC updated: no
- 2026-10-17 — POST /versions without `presetId` keeps the base version's preset, re-pinned at the currently shipped version/hash; explicit null = no preset. "Plan locked" for PRESET_LOCKED = any implementation_plan of the version locked/approved — FR-74 — SPEC updated: no
- 2026-10-17 — Preset protectedPaths follow §4.11's "unless declared in this step" exception for the owning agent (e.g. db-engineer may declare `lib/database.types.ts`); migrations already on the base commit are never declarable. Optimisation plans accept catalogue keys or generalist, protected paths enforced, ordering rules off — SPEC updated: no
- 2026-10-17 — `no_secret_env_in_client` ignores NODE_ENV (inlined by Next.js, not secret); preset checklist items are numbered [Cn] in the review prompt and violations tagged [Cn] take that item's category/severity with no penalty; preset guidance is in a `<stack_preset>` platform-guidance block (prompt versions: spec 3, plan 3, code 4, review 5) — FR-77 — SPEC updated: no
- 2026-10-17 — Projects list `?preset=none` filters projects without a preset — §7.2 — SPEC updated: no
- 2026-10-17 — Migrations already on the version's base commit are immutable for everyone: agent plans, Owner-edited plans and the Guardian at execution all reject them; preset path matching is case-insensitive (like v1 PathRules) — §4.11/§4.12 — SPEC updated: no
- 2026-10-17 — Plans are validated against the base commit's file list (fetched if not local); if it still can't be listed, a generated plan may not create any migration (fail closed). nextjs-supabase preset bumped to 1.0.1 (db-engineer prompt wording) — §4.11 — SPEC updated: no

## Known issues
- `GitHub` paths (App, PAT import, repo tools over GitHub snapshots) verified only against stub HTTP handlers — orchestrator
- E2E against k8s needs `Git__Local__ImportRoot` set (and a seeded "existing" repo under it) for the import E2E — local-k8s-engineer (phase 10)
- §11 to confirm on first real App registration: manifest `hook_attributes {active:false}` accepted without a `url`; GitHub App flows are verified only against stub HTTP handlers (no real App registered) — Ben
- New-project wizard loses entered values when the user leaves to Connect GitHub (the page warns); persist the draft in sessionStorage if wanted — vite-react-engineer
- E2E lifecycle spec lost its rollback-after-release scenario (FR-63 blocks it); add a rollback-before-release scenario — e2e-qa-engineer (phase 10)
- Refinement page shows only the latest round (RefinementDto has no past-rounds list) — orchestrator
- ReleaseVersionStampingInterceptor still needed by integration-test seeds (LifecycleSeed, direct inserts); remove once they set release_version_id — ef-data-engineer
- M9a screenshot check left a test user and project "Shell check" in the k8s DB — delete if unwanted — Ben
- Carried from v1 (see `docs/v1/PROGRESS.md`): rollback_events.restored_phase nullable (folded into the M10 versions migration per SPEC §5); TAG_CONFLICT cleanup; gates list unpaged — orchestrator
