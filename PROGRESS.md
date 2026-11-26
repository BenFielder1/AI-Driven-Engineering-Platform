# Build progress (0.8.0)

Spec: `SPEC.md` (0.8.0; 0.1.0–0.7.0 in `docs/`) · Build notes: `docs/0.8.0/session-notes.md` · Stack: `STACK.md` · Branch: `build/tevrin-0.8.0` · Started: 2026-11-23

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

**Invariant:** the SDLC behaves as in 0.7.0. The characterisation snapshots change only once, in the FR-328 rename re-baseline; after it, they are frozen again.

## Environment
- Packs (STACK.md): `fe-nextjs`, `be-aspnet`, `data-efcore-postgres`, `vcs-github`, `deploy-railway`, `local-docker-desktop-k8s`. All installed.
- Tools: .NET SDK 10.0.402, Node 24.14.1.
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben).
- VCS CLI auth: gh yes (BenFielder1). Railway CLI logged in; Railway deploys run only through `.github/workflows/deploy.yml`.

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-323 | Projects, namespaces and `Aide*` types renamed to Tevrin; architecture tests list host assemblies | 3.1 | backend | M65 | done | 2f43ed3, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45278720358) |
| FR-324 | Migrations change only by mechanical substitutions (CI check); EF drift empty | 3.1 | backend + data + CI | M65 | done | 2f43ed3 (+ 4865d26), [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45365150480) |
| FR-325 | Config/env/auth scheme/telemetry renamed, old names read with a warning | 3.1, 4.77 | backend + deploy | M65 | done | a9ca935, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45312651121) |
| FR-326 | Wire and stored identifiers, read both (tokens, cookie, plan block, merge strategy, others) | 3.1, 4.77 | backend + frontend + data | M65 | done | 4865d26, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45365150480) |
| FR-327 | Images, k8s, Railway IaC, smoke scripts renamed; rename runbook | 3.1 | deploy + docs | M65 | done | 3f04ef7, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45371572456) |
| FR-328 | One snapshot re-baseline with a mapped-equality CI check; freeze base moves | 3.1 | backend + CI | M65 | done | 4865d26 + 62c5ce1, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45365150480) |
| FR-329 | Leftover-name scan test with `scripts/ci/aide-allowlist.txt`; docs updated | 3.1 | backend + docs + CI | M65 | done | 49b6412, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45374604868) |
| FR-330 | `RunControl.Engine`/`Host` per pipeline; SDLC is `Host` | 3.2 | backend | M66 | done | 7a4f6da, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45394797913) |
| FR-331 | Start a run endpoint (inputs, one-active rule) | 3.2 | backend + data | M66 | done | 63c8601, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45412232128) |
| FR-332 | Run driver `stage.run` (idempotent) | 3.2, 4.62 | backend | M66 | done | 63c8601, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45412232128) |
| FR-333 | Cancel, roll back, retry endpoints | 3.2 | backend | M66 | done | 63c8601, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45412232128) |
| FR-334 | Runs without a subject | 3.2 | backend + data | M66 | done | 7a4f6da + 50ccb98, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45394797913) |
| FR-335 | Submit an input; `waiting_on_input` | 3.2 | backend + data | M66 | done | 63c8601, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45412232128) |
| FR-336 | Schedule trigger (cron, timezone, skip when active) | 3.2, 4.71 | backend + data | M66 | done | c9b6a15 + 88a87d0, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45452701300) |
| FR-337 | Webhook trigger (signature, replay, rotation, deliveries) | 3.2, 4.72 | backend + data | M66 | done | c9b6a15 + 88a87d0, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45452701300) |
| FR-338 | `Tevrin.Pack.Coach` project + architecture tests + test project | 3.3 | backend | M67 | done | 38d9c65, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45475257880) |
| FR-339 | `impulse-coach` pipeline definition | 3.3 | backend | M67 | done | 38d9c65 + e13bbc7 + 07f4ade, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45578882959) |
| FR-340 | `velocity.load-rules` guard (R1–R5) | 3.3, 4.63 | backend | M67 | done | 38d9c65, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45475257880) |
| FR-341 | Coach uses default agent policy; budget/audit hooks apply to every pipeline | 3.3 | backend | M67 | done | e13bbc7 + 2968049, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45578882959) |
| FR-342 | Impulse coaching pack docs guide | 3.3 | docs | M67 | done | 07f4ade, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45578882959) |
| FR-343 | Parallel stages (`fixture-parallel`) | 3.4, 4.64 | backend | M68 | done | ab601ee + 68818af, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073) |
| FR-344 | Veto (`fixture-veto`) | 3.4 | backend | M68 | done | ccf1b19, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45616618135) |
| FR-345 | Fan-out with isolated context (`fixture-forecast`) | 3.4, 4.65 | backend | M68 | done | fe72383, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073) |
| FR-346 | Aggregation stage | 3.4 | backend | M68 | done | fe72383, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073) |
| FR-347 | Route back to an earlier stage (`fixture-editorial`) | 3.4 | backend | M68 | done | ccf1b19, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45616618135) |
| FR-348 | Revision cap across the run | 3.4, 4.66 | backend | M68 | done | ccf1b19, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45616618135) |
| FR-349 | Multiple output variants | 3.4, 4.67 | backend | M68 | done | 1c5bbb8, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073) |
| FR-350 | Validator agent (`fixture-factcheck`) | 3.5 | backend | M69 | done | 328ea74 + 7a64b19, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45787248789) |
| FR-351 | Agent feedback store (`fixture-postmortem`) | 3.5, 4.75 | backend + data | M69 | done | 7a64b19 + 03d8a13, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45787248789) |
| FR-352 | Source-of-truth artifacts pinned per run (`fixture-cv`) | 3.5, 4.76 | backend + data | M67 | done | 9372e8b, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45467772497) |
| FR-353 | External outcome scoring, Brier | 3.5, 4.74 | backend + data | M69 | done | 328ea74, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45787248789) |
| FR-354 | Follow-up stages (delayed jobs) | 3.5, 4.70 | backend + data | M69 | done | 3addd12, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45787248789) |
| FR-355 | Map-reduce (`fixture-qa`) | 3.6, 4.68 | backend + data | M70 | done | 0f53d58 + 8f4349c + 2c81628 + c08549f, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45849216582) |
| FR-356 | Conversation stage (`fixture-callsim`) | 3.6, 4.69 | backend | M70 | done | 0f53d58 + 8f4349c + ab94bbb + ec16b5b, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45849216582) |
| FR-357 | Windowed inputs over runs (`fixture-trends`) | 3.6 | backend | M70 | done | 0f53d58 + 5ce5414, [CI](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45849216582) |
| FR-358 | Tool permission tiers | 3.7 | backend | M71 | todo | — |
| FR-359 | Mid-stage approval for destructive tools (`fixture-oncall`) | 3.7, 4.73 | backend + data | M71 | todo | — |
| FR-360 | Human gate before an external action | 3.7 | backend | M71 | todo | — |
| FR-361 | Webhook triggers and tools on agents (on-call fixture) | 3.7 | backend | M71 | todo | — |
| FR-362 | Start run / Submit input dialogs | 3.8 | frontend | M72 | todo | — |
| FR-363 | New constructs in stage list and timeline | 3.8 | frontend | M72 | todo | — |
| FR-364 | New gate cards | 3.8 | frontend | M72 | todo | — |
| FR-365 | Scores and feedback UI | 3.8 | frontend | M72 | todo | — |
| FR-366 | Triggers UI | 3.8 | frontend | M72 | todo | — |
| FR-367 | Version 0.8.0, changelog, guides, release docs | 3.9 | frontend + docs | M65 (bump) / M73 | todo | — |

## Phases
- [x] M65 Rename (base: d1301d2) — FR-323–FR-329; version bump to 0.8.0 (FR-367)
  - [x] a. Version bump + PROGRESS ([CI 45272691359](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45272691359))
  - [x] b. Projects, namespaces and types (mechanical) + migration-check script (FR-323, FR-324) ([CI 45278720358](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45278720358))
  - [x] c. Config and telemetry fallbacks (FR-325) ([CI 45312651121](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45312651121))
  - [x] d. Wire and stored identifiers, read both; contract + client (FR-326) ([CI 45365150480](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45365150480), with f)
  - [x] e. Images, k8s, IaC, smoke scripts, runbook (FR-327) ([CI 45371572456](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45371572456))
  - [x] f. Snapshot re-baseline + mapped-equality check (FR-328): re-baseline 4865d26 (snapshots from scratch-branch CI run 45361838017), freeze pinned in 62c5ce1 ([CI 45365150480](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45365150480))
  - [x] g. Leftover-name scan + allowlist, CLAUDE.md/STACK.md/README/docs (FR-329) ([CI 45374604868](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45374604868))
- [x] M66 Run control and triggers — FR-330–FR-337
  - [x] a. Run control, definition fields, `waiting_on_input`, runs without a subject, trigger tables, error codes (FR-330, FR-334) — 7a4f6da + fix 50ccb98 ([CI 45394797913](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45394797913))
  - [x] b. `stage.run` driver; start, cancel, rollback, retry and input endpoints; engine gate decisions; `fixture-linear` (FR-331–FR-333, FR-335) — 63c8601 ([CI 45412232128](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45412232128))
  - [x] c. Schedules and webhooks (FR-336, FR-337) — c9b6a15 + test fix 88a87d0 ([CI 45452701300](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45452701300))
- [x] M67 Coach pack — FR-338–FR-342, FR-352
  - [x] a. Source-of-truth artifacts pinned per run, `fixture-cv` (FR-352) — 9372e8b ([CI 45467772497](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45467772497))
  - [x] b1. `Tevrin.Pack.Coach`: definition, schemas, prompts, `coach.velocity-load-rules` guard, `triggers:` list, architecture tests, test project (FR-338–FR-340) — 38d9c65 ([CI 45475257880](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45475257880))
  - [x] b2. Token budget and audit hooks for every pipeline, pack registration, Fake outputs, worked-example integration tests (FR-341, FR-339 acceptance) — e13bbc7 + fix 2968049 ([CI 45578882959](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45578882959))
  - [x] c. Pipeline tab render check (Vitest) and the Impulse coaching pack guide (FR-339 acceptance, FR-342) — 07f4ade ([CI 45578882959](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45578882959))
- [x] M68 Flow constructs — FR-343–FR-349
  - [x] a. Definition syntax, validator and hash for parallel, veto, fan-out, aggregate, routes, limits and variants; fixtures (FR-343–FR-349) — 46e1adf ([CI 45608493696](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45608493696))
  - [x] b. Data: `pipeline_runs.revisions`/`outcome`, branch, variant and veto columns, `variant_select` gates with payload — 81944dc ([CI 45616618135](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45616618135))
  - [x] c. Driver: parallel (c2 ab601ee + fix 68818af), veto, routes, revision cap (c1 ccf1b19) (FR-343, FR-344, FR-347, FR-348) ([CI 45668753073](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073))
  - [x] d. Driver: fan-out with isolation, aggregate with disagreement (d1 fe72383), variants with gate or agent selection (d2 1c5bbb8) (FR-345, FR-346, FR-349) ([CI 45668753073](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45668753073))
- [x] M69 Quality and learning — FR-350, FR-351, FR-353, FR-354
  - [x] a. Data: `agent_runs.validator_findings`/`feedback_ids`, `agent_feedback`, `outcome_scores` — c1da188
  - [x] b. Validator agents (FR-350) — 328ea74
  - [x] c. Agent feedback store and injection (FR-351) — 7a64b19 + fix 03d8a13
  - [x] d. Outcome scores, Brier, prompt-comparison means (FR-353) — 328ea74
  - [x] e. Follow-up stages (FR-354) — 3addd12 ([CI 45787248789](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45787248789), after splitting the `rest` integration shard in 5d048d7: Pipelines, Stages and Projects now run as `pipelines-projects`)
- [x] M70 Scale and conversation — FR-355–FR-357
  - [x] a. Definition syntax, validator and hash for map/reduce, conversation and `from_runs`; fixtures `fixture-qa`, `fixture-callsim`, `fixture-trends` (D-0.8.0-23) — 0f53d58 ([CI 45789843317](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45789843317))
  - M70a review minors for later steps: validator error paths `inputs[{j}]` skip `from_runs` entries (wrong YAML index); map/conversation stages read as `Manual` until c/d land; `from_runs` not yet in prompt placeholders or `AgentStageRunner` inputs; `DEF_MAP_SOURCE` checks list order, not reachability.
  - [x] b. Data: `stage_executions.parent_execution_id`, `item_index` on agent runs and artifacts, `agent_runs.turn_index`/`turn_role`; `MAP_TOO_MANY_ITEMS` (D-0.8.0-24). Review minor for c: a child execution must always have an `item_index` (the unique index treats null as distinct) — 8f4349c ([CI 45793183265](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45793183265))
  - [x] c. Driver: map-reduce (FR-355), D-0.8.0-25 — 2c81628 + fix c08549f ([CI 45821499223](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45821499223))
  - [x] d. Driver: conversation (FR-356), D-0.8.0-26 — ab94bbb + fix ec16b5b ([CI 45830714380](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45830714380))
  - [x] e. `from_runs` input resolution (FR-357), D-0.8.0-27; FR-20 integrity filter on `from_runs` and FR-346 aggregate lists — 5ce5414 ([CI 45849216582](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/45849216582))
- [ ] M71 Tools and safety — FR-358–FR-361 (security: destructive tools stay on fixtures)
  - [ ] a1. Definition and registration: tool `tier`, handler/tool `external`, `tool_policy: { max_tier }`, `DEF_TOOL_TIER`; fixture `fixture-oncall` (FR-358, FR-359, FR-360 syntax)
  - [x] a2. Data: `agent_runs.tool_loop_state` (jsonb); error codes `TOOL_STATE_TOO_LARGE`, `EXTERNAL_ACTION_REJECTED`. No other schema needed: the tool-call tier goes in each `agent_runs.tool_calls` element (`AgentToolCallRecord`, optional for old rows), gate `type` is a free string, and the §4.73 call state is derived from `tool_loop_state` + the run's `tool_approval` gate (cleared on resume)
  - [ ] b. Runner: tier enforcement and recording at call time (FR-358)
  - [ ] c. Destructive tool approval mid-stage, saved loop state, resume (FR-359, §4.73)
  - [ ] d. External action gate before external handlers and tools (FR-360), `fixture-cv` submit
  - [ ] e. On-call end to end: signed webhook → investigator (read) → executor (destructive) (FR-361)
- [ ] M72 Engine UI — FR-362–FR-366
  - Carry-over from M70: map `src/lib/errors.ts` copy for `DEF_MAP_SOURCE`, `DEF_CONVERSATION_PARTICIPANTS` (`MAP_TOO_MANY_ITEMS` added in M70b).
  - Carry-over from M70: show `turn_index`/`turn_role` and map `item_index` on timeline entries (additive DTO fields).
  - Carry-over from M69: show the follow-up label (`stage_executions.followup`) on timeline entries (additive DTO field), and the outcome scores and feedback (FR-365).
  - Carry-over from M68: expose `approval_gates.payload` (variant labels and artifact ids) on the engine gate DTO for the `variant_select` card.
  - Carry-over from M67: show an artifact's project scope and a run's pinned project artifacts (`pinnedArtifacts`, FR-352) in the run view; fill `{artifact}` in `SOURCE_ARTIFACT_MISSING` from the problem `reason`.
- [ ] M73 Release 0.8.0 — FR-367 (on Ben's confirmation)

## Decisions
- 2026-11-23 — The 0.8.0 spec moved from `docs/0.8.0/` to the root `SPEC.md` after 0.7.0 was released (PR #63) — CLAUDE.md version workflow — SPEC updated: yes (header)

- 2026-11-24 — D-0.8.0-16: `AIDE_RUN_MIGRATIONS`, the data-protection application name `aide` and the `Aide.Secrets.*` purposes keep their names (generated-project contract; decryption of stored secrets) — FR-325 acceptance corrected — SPEC updated: yes

- 2026-11-24 — M65 steps d and f land together: the merge-strategy value (`aide_merges`) and the system actor (`aide-system`) are captured by the characterisation snapshots, so the commit that changes what they write is the FR-328 re-baseline commit. Readers of every old name land first in that same commit; writers whose values the snapshots or golden prompts capture are flipped there. The new snapshots come from a CI run on a scratch build branch, then are checked by the mapped-equality script before they're committed — Claude — SPEC updated: no
- 2026-11-24 — FR-326 keeps these `aide` names (all go on FR-329's allowlist):
  - short-lived data-protection purposes for OAuth state and the GitHub App manifest state (`Aide.GitHub.*`, `Aide.Bitbucket.*`, `Aide.GitLab.*`), so in-flight sign-ins survive the deploy (the sign-in properties are written as `tevrin.*` and the `aide.*` ones are still read);
  - the Supabase migration lock `aide_supabase_migrations`, shared by old and new deploys;
  - the bundled catalog's pack versions (`+aide.<n>`, read either way), until each pack's next version;
  - the dev seed admin `admin@aide.local` and the local data folders beside the data-protection keys.

  The system actor (`aide-system`), the plan block and stack-profile fence tags, and the fake failure markers are read both ways now and written as `tevrin` in the FR-328 commit — Claude — SPEC updated: no
- 2026-11-24 — The `aide_merges` migration rewrites only `projects.merge_strategy` (and the column default); stored event and job payloads keep the old value, since events are the audit trail and every reader accepts both — Claude — SPEC updated: no

- 2026-11-24 — D-0.8.0-17: the generic workload, service and job names (`api`, `web`, `db`, `db-migrate`; Railway `api`, `web`, `postgres`) stay; FR-327 renames the namespace, images, data PVC, labels, Railway project, variables and database — Claude (M65e review) — SPEC updated: yes (FR-327 row and acceptance, §10)
- 2026-11-24 — The dev loop (`docker-compose.yml`, `.env.example`) keeps its `aide` project, volume, database and user, so an existing dev database keeps working ("Unchanged: dev loop", §11); FR-329 allowlists them. `.railway/railway.ts` ignores `ConnectionStrings__Aide` for 0.8.x so `railway config apply` never removes the variable a 0.7.0 deployment reads — Claude — SPEC updated: no

- 2026-11-24 — FR-329's allowlist allows any `aide` name under the test folders (backend tests, e2e, Vitest files, mocks) with removal 0.9.0, rather than one entry per name: tests exercise the old names still read, the FR-197–FR-201 product-name rules and pre-rename fixtures. Outside tests, every entry names its tokens and files. The scanner is `scripts/ci/check-aide-names.py` (Python for per-entry path and token regexes) — Claude — SPEC updated: no

- 2026-11-24 — Engine-run gates (M66): approval-link emails, gate reminders and timers, `NotificationComposer` and the Home "awaiting gates" count stay SDLC-only in 0.8.0; engine gates show in the engine gate inbox and the live `gateUpdated` push, and have their own decision path — Claude — SPEC updated: no

## Known issues
- An SDLC rollback is refused (`ROLLBACK_BLOCKED`) while any agent run in the project is in progress, including an engine run's agent (M66): conservative, SDLC behaviour unchanged; revisit when engine and SDLC runs commonly share a project.
- Non-SDLC agent runs (coach, fixtures) record `ARTIFACT_CREATED`/`GATE_CREATED` and token use (FR-341), but not `RUN_STARTED`/`RUN_COMPLETED` events: the engine hook interface has no slot for them. Their payloads carry ids and counts only, never content (M67).
- Parallel groups with `on_branch_failure: continue`: a branch whose job is dead-lettered after its transient retries waits for a manual retry instead of being marked failed (M68).
- A follow-up stage whose job is dead-lettered leaves its stage execution open (the reconcile skips follow-up rows); like the parallel `continue` case, it needs a job-ended hook (M69).
- A map item whose job is dead-lettered after its transient retries stays open: it doesn't count toward `max_failed`, and the map waits for a manual retry (same job-ended-hook gap as parallel `continue` and follow-ups) (M70).
- Map item and fan-out variant artifacts on a run **with a subject** would collide on `ix_artifacts_project_id_type_version` (no `item_index`/`variant`). Not reachable: engine-started runs have no subject, and the SDLC has no map stage (M70c review).
- Map job reads filter `jobs.payload->>'stageExecutionId'` with no expression index yet; add one in a later migration if map stages get large or job history grows (M70c).
- The map item isolation check loads every sibling output of the current map entry on each item start (O(n²) rows at `max_items` 1000); push the probe into SQL if large maps get slow (M70c review).
- Conversation (M70d review minors, left as is): a redelivered escalated turn job after approval raises a fresh escalation (same as plain stages); the stop guard re-runs on already-checked turns at each new turn job.
- A map stage plans its entry twice (`EnterMapAsync`): an input arriving in between fails the job with a misleading `DEFINITION_UNAVAILABLE`; the run stays active and a manual retry recovers. Use the one-plan pattern from the conversation turn 1 (M70d re-review).
- No index on `pipeline_runs(project_id, pipeline_id, status, completed_at)` for `from_runs`; add one in a later migration if it gets slow. Conversation stop guards don't get `RunsInputFact`. A map item handler on a stage with `from_runs` reloads the lists per item (up to 1,000 identical queries); load once per map entry if that combination is used (M70e).
- Integration shards: `rest` hit its 22-min step timeout in M69 and was split (5d048d7, `pipelines-projects`); watch shard times as tests grow.

## Manual checks (Ben)
- FR-327: run `docs/operations/rename-to-tevrin.md` steps 1–3 on preview **before the 0.8.0 build PR merges** (otherwise the preview deploy's drift check fails until they're done), and on production before publishing 0.8.0; step 4 after 0.8.0 runs there.
- FR-327 local: free localhost:8080 from the old `aide` namespace, update `secrets.env` to `ConnectionStrings__Tevrin` / `Database=tevrin;Username=tevrin`, then `deploy-local.sh` and the smoke test (runbook §7).
- Delete the scratch branch `build/tevrin-0.8.0-snapshots` (used only to produce the FR-328 snapshots, CI run 45361838017) when convenient.
- M66: the API image now installs `tzdata` (schedules use IANA zones); confirm a schedule in a non-UTC zone fires at the right local time on the Docker Desktop deploy.
- (more as milestones land; see SPEC §12 M73)
