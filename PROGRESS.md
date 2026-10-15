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
| FR-53 | Design system + app shell (sidebar, header, ⌘K, logo) | 3.1, 7.1 | frontend | 1 | todo | — |
| FR-54 | Project-mode sidebar, stage stepper, next action | 3.1 | frontend | 1 | todo | — |
| FR-55 | One page per stage, `?version=` read-only | 3.1 | both | 3 | todo | — |
| FR-56 | Review page: every cycle, cycle picker, recharts trend | 3.1 | both | 3 | todo | — |
| FR-57 | Execution page: every run, attempt history | 3.1 | both | 3 | todo | — |
| FR-58 | Restore artifact version as new draft | 3.1 | both | 3 | todo | — |
| FR-59 | Artifacts page per release version; cross-version diff | 3.1 | both | 3 | todo | — |
| FR-60 | Start new version after merged release | 3.2 | both | 2 | todo | — |
| FR-61 | Version validation errors | 3.2, 4.10 | backend | 2 | todo | — |
| FR-62 | Released + in-development badges, version switcher, read-only past | 3.2 | both | 2 | todo | — |
| FR-63 | Checkpoints per release version; rollback scoped; released closed | 3.2, 4.10 | both | 2 | todo | — |
| FR-64 | Release tags user semver; GitHub Release (GitHub part phase 7) | 3.2 | backend | 2, 7 | todo | — |
| FR-65 | Merge strategy, awaiting_merge, merge poll | 3.2, 4.14 | both | 7 | todo | — |
| FR-66 | Initial version for new projects | 3.2 | both | 2 | todo | — |
| FR-67 | v1 project migration to release versions | 3.2, 4.10 | backend | 2 | todo | — |
| FR-70 | Repository Analyst → codebase_summary (auto-locked) | 3.3 | backend | 6 | todo | — |
| FR-71 | Read-only repo tools (tool use, limits, path safety, redaction) | 3.3 | backend | 6 | todo | — |
| FR-72 | Iteration refinement inputs | 3.3 | backend | 6 | todo | — |
| FR-73 | Import existing repo wizard | 3.4 | both | 6 | todo | — |
| FR-74 | Presets: data bundles, wizard Stack step, pinned per version | 3.5, 4.11 | both | 8 | todo | — |
| FR-75 | Preset spec sections; mandatory FR IDs + GWT | 3.5, 4.13 | backend | 6, 8 | todo | — |
| FR-76 | Plan steps carry agent + covers; preset plan validation | 3.5, 4.11 | both | 8 | todo | — |
| FR-77 | Preset review checklist + deterministic checks | 3.5 | backend | 8 | todo | — |
| FR-78 | User LLM connections | 3.6 | both | 4 | todo | — |
| FR-79 | Bind Owner's LLM connection to project | 3.6 | both | 4 | todo | — |
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
| §4.10 | Release versions, semver, checkpoints, rollback scope, migration | 4.10 | backend | 2 | todo | — |
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
- [ ] 1 M9 Design system + shell — IDs: FR-53, FR-54, NFR-A11Y2 (tokens)
  - [ ] vite-react-engineer (tokens, fonts, shadcn primitives, logo, shell, nav config, ⌘K)
  - [ ] vite-react-engineer (restyle v1 pages, project-mode sidebar, stage stepper)
  - [ ] gate · review · redeploy · commit
- [ ] 2 M10 Versions + checkpoints — IDs: FR-60–FR-64 (Local), FR-66, FR-67, §4.10
- [ ] 3 M11 Stage pages + history — IDs: FR-55–FR-59
- [ ] 4 M12a LLM connections — IDs: FR-78, FR-79
- [ ] 5 M12b GitHub App — IDs: FR-80–FR-83, FR-86
- [ ] 6 M13a Iteration + import — IDs: FR-70–FR-73, §4.13, FR-75 (FR IDs part)
- [ ] 7 M13b Merge strategies + GitHub release — IDs: FR-64 (GitHub), FR-65, FR-84, FR-85
- [ ] 8 M14 Presets — IDs: FR-74–FR-77, §4.11–4.12
- [ ] 9 M15 Vercel — IDs: FR-87–FR-91, §4.15
- [ ] 10 M16 Integrate & harden — IDs: FR-92, FR-93, NFR-*2, E2E
- [ ] Final — spec-verifier (v1 + v2) → fixes → github-ci-engineer → readme-writer

## Decisions
- 2026-10-15 — M12 and M13 are each split into two phases (connections/GitHub App; iteration/merge) — keeps phases at 2–5 tasks — SPEC updated: no

## Known issues
- Carried from v1 (see `docs/v1/PROGRESS.md`): rollback_events.restored_phase nullable (folded into the M10 versions migration per SPEC §5); TAG_CONFLICT cleanup; gates list unpaged — orchestrator
