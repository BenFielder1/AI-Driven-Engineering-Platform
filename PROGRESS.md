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
| FR-338 | `Tevrin.Pack.Coach` project + architecture tests + test project | 3.3 | backend | M67 | todo | — |
| FR-339 | `impulse-coach` pipeline definition | 3.3 | backend | M67 | todo | — |
| FR-340 | `velocity.load-rules` guard (R1–R5) | 3.3, 4.63 | backend | M67 | todo | — |
| FR-341 | Coach uses default agent policy; budget/audit hooks apply to every pipeline | 3.3 | backend | M67 | todo | — |
| FR-342 | Impulse coaching pack docs guide | 3.3 | docs | M67 | todo | — |
| FR-343 | Parallel stages (`fixture-parallel`) | 3.4, 4.64 | backend | M68 | todo | — |
| FR-344 | Veto (`fixture-veto`) | 3.4 | backend | M68 | todo | — |
| FR-345 | Fan-out with isolated context (`fixture-forecast`) | 3.4, 4.65 | backend | M68 | todo | — |
| FR-346 | Aggregation stage | 3.4 | backend | M68 | todo | — |
| FR-347 | Route back to an earlier stage (`fixture-editorial`) | 3.4 | backend | M68 | todo | — |
| FR-348 | Revision cap across the run | 3.4, 4.66 | backend | M68 | todo | — |
| FR-349 | Multiple output variants | 3.4, 4.67 | backend | M68 | todo | — |
| FR-350 | Validator agent (`fixture-factcheck`) | 3.5 | backend | M69 | todo | — |
| FR-351 | Agent feedback store (`fixture-postmortem`) | 3.5, 4.75 | backend + data | M69 | todo | — |
| FR-352 | Source-of-truth artifacts pinned per run (`fixture-cv`) | 3.5, 4.76 | backend + data | M67 | todo | — |
| FR-353 | External outcome scoring, Brier | 3.5, 4.74 | backend + data | M69 | todo | — |
| FR-354 | Follow-up stages (delayed jobs) | 3.5, 4.70 | backend + data | M69 | todo | — |
| FR-355 | Map-reduce (`fixture-qa`) | 3.6, 4.68 | backend + data | M70 | todo | — |
| FR-356 | Conversation stage (`fixture-callsim`) | 3.6, 4.69 | backend | M70 | todo | — |
| FR-357 | Windowed inputs over runs (`fixture-trends`) | 3.6 | backend | M70 | todo | — |
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
- [ ] M67 Coach pack — FR-338–FR-342, FR-352
- [ ] M68 Flow constructs — FR-343–FR-349
- [ ] M69 Quality and learning — FR-350, FR-351, FR-353, FR-354
- [ ] M70 Scale and conversation — FR-355–FR-357
- [ ] M71 Tools and safety — FR-358–FR-361
- [ ] M72 Engine UI — FR-362–FR-366
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
- Integration test shard `rest` is slow (15–18 min); rebalancing is an open follow-up from 0.7.0.

## Manual checks (Ben)
- FR-327: run `docs/operations/rename-to-tevrin.md` steps 1–3 on preview **before the 0.8.0 build PR merges** (otherwise the preview deploy's drift check fails until they're done), and on production before publishing 0.8.0; step 4 after 0.8.0 runs there.
- FR-327 local: free localhost:8080 from the old `aide` namespace, update `secrets.env` to `ConnectionStrings__Tevrin` / `Database=tevrin;Username=tevrin`, then `deploy-local.sh` and the smoke test (runbook §7).
- Delete the scratch branch `build/tevrin-0.8.0-snapshots` (used only to produce the FR-328 snapshots, CI run 45361838017) when convenient.
- M66: the API image now installs `tzdata` (schedules use IANA zones); confirm a schedule in a non-UTC zone fires at the right local time on the Docker Desktop deploy.
- (more as milestones land; see SPEC §12 M73)
