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
| FR-64 | Release tags user semver; GitHub Release (GitHub part phase 7) | 3.2 | backend | 2, 7 | partial (Local/tag done; GitHub Release phase 7) | DecideTag, FR42_amended_by_FR64_* |
| FR-65 | Merge strategy, awaiting_merge, merge poll | 3.2, 4.14 | both | 7 | todo | — |
| FR-66 | Initial version for new projects | 3.2 | both | 2 | done | initialVersion + wizard Version step; FR66_* tests |
| FR-67 | v1 project migration to release versions | 3.2, 4.10 | backend | 2 | done | migration ReleaseVersions backfill; FR67_* migration tests; applied on k8s |
| FR-70 | Repository Analyst → codebase_summary (auto-locked) | 3.3 | backend | 6 | todo | — |
| FR-71 | Read-only repo tools (tool use, limits, path safety, redaction) | 3.3 | backend | 6 | todo | — |
| FR-72 | Iteration refinement inputs | 3.3 | backend | 6 | todo | — |
| FR-73 | Import existing repo wizard | 3.4 | both | 6 | todo | — |
| FR-74 | Presets: data bundles, wizard Stack step, pinned per version | 3.5, 4.11 | both | 8 | todo | — |
| FR-75 | Preset spec sections; mandatory FR IDs + GWT | 3.5, 4.13 | backend | 6, 8 | todo | — |
| FR-76 | Plan steps carry agent + covers; preset plan validation | 3.5, 4.11 | both | 8 | todo | — |
| FR-77 | Preset review checklist + deterministic checks | 3.5 | backend | 8 | todo | — |
| FR-78 | User LLM connections | 3.6 | both | 4 | done | me/connections/llm CRUD + test, list-models validation; FR78_* tests |
| FR-79 | Bind Owner's LLM connection to project | 3.6 | both | 4 | done | binding + clearing rules under project lock, runs use bound key, Llm:AllowSystemKeys; FR79_* tests |
| FR-80 | GitHub App manifest registration + App sign-in | 3.7 | both | 5 | todo | — |
| FR-81 | User GitHub connection, repo picker, create repo | 3.7 | both | 5 | todo | — |
| FR-82 | `github_app` provider, installation tokens, per-version branch | 3.7 | backend | 5 | todo | — |
| FR-83 | PAT fallback | 3.7 | both | 5 | todo | — |
| FR-84 | Merge readiness | 3.7 | both | 7 | todo | — |
| FR-85 | Check runs as review input | 3.7 | backend | 7 | todo | — |
| FR-86 | Wizard Repository step | 3.7 | frontend | 5 | todo | — |
| FR-87 | Vercel connection | 3.8 | both | 9 | todo | — |
| FR-88 | Deployment settings + env vars | 3.8 | both | 9 | todo | — |
| FR-89 | vercel.json disables aide/** auto deploys | 3.8 | backend | 9 | todo | — |
| FR-90 | Preview deployments in review | 3.8 | both | 9 | todo | — |
| FR-91 | Production deployments, promote, Deployments page | 3.8 | both | 9 | todo | — |
| FR-92 | New events + notification types | 3.9 | both | 10 | todo | — |
| FR-93 | CLI additions | 3.9 | backend | 10 | todo | — |
| §4.10 | Release versions, semver, checkpoints, rollback scope, migration | 4.10 | backend | 2 | done | SemVer, ReleaseVersionRules, NewVersion transition matrix; S4_10_* tests |
| §4.11–4.12 | Preset format and the two presets | 4.11 | backend | 8 | todo | — |
| §4.13 | Iteration specs, review scope, plan scope | 4.13 | backend | 6 | todo | — |
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
- [ ] 5 M12b GitHub App — IDs: FR-80–FR-83, FR-86
- [ ] 6 M13a Iteration + import — IDs: FR-70–FR-73, §4.13, FR-75 (FR IDs part)
- [ ] 7 M13b Merge strategies + GitHub release — IDs: FR-64 (GitHub), FR-65, FR-84, FR-85
- [ ] 8 M14 Presets — IDs: FR-74–FR-77, §4.11–4.12
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

## Known issues
- E2E lifecycle spec lost its rollback-after-release scenario (FR-63 blocks it); add a rollback-before-release scenario — e2e-qa-engineer (phase 10)
- Refinement page shows only the latest round (RefinementDto has no past-rounds list) — orchestrator
- ReleaseVersionStampingInterceptor still needed by integration-test seeds (LifecycleSeed, direct inserts); remove once they set release_version_id — ef-data-engineer
- M9a screenshot check left a test user and project "Shell check" in the k8s DB — delete if unwanted — Ben
- Carried from v1 (see `docs/v1/PROGRESS.md`): rollback_events.restored_phase nullable (folded into the M10 versions migration per SPEC §5); TAG_CONFLICT cleanup; gates list unpaged — orchestrator
