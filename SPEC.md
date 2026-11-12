# Tevrin — Specification (0.6.1)

2026-11-12 (part 2 added) · Ben Fielder · **0.6.1**, built on the 0.6.0 codebase in this repo. Earlier versions: [`docs/0.1.0/SPEC.md`](docs/0.1.0/SPEC.md) … [`docs/0.6.0/SPEC.md`](docs/0.6.0/SPEC.md). Stack: [`STACK.md`](STACK.md). Source: Ben's 0.6.1 brief (2026-11-11), his part 2 brief on review findings and build reliability (2026-11-12), the interview answers in §10, a survey of the review, plan, execution and deployment code, and the scrollbar CSS of benfielder.com.

> **Part 2 (2026-11-12).** Part 1 (§3.1–§3.4, M46–M49) was merged in PR #38. Part 2 adds §3.6–§3.8 (FR-246–FR-259, §4.42–§4.48, M51–M53) on the build branch `build/tevrin-0.6.1-review`. The release (M50) now runs after M53.

> **How this spec relates to 0.1.0–0.6.0.** 0.6.1 is an increment on the released 0.6.0 app. **Everything in the earlier specs stays in force unless this document amends it.** Amended items are listed in §3.0 and marked *(amends …)*. New requirements continue from **FR-232**, domain rules from **§4.35**, decisions use **`D-0.6.1-n`**, and milestones continue from **M46**. Earlier tests stay green unless an amendment changes what they assert; then update the test and name the amendment in it.

## 1. Overview

0.6.1 is a small release of fixes and improvements in six areas:

- **Branch names.** New project and release branches start with `tevrin/` instead of `aide/`. Projects that already have an `aide/` branch keep it.
- **Scrollbars.** A thin, themed scrollbar replaces the browser default across the app and marketing site, styled like benfielder.com.
- **Failure-aware review.** When a build, deploy or CI check fails, the review agent gets the redacted log excerpt. It must write a specific fix step from it, and Tevrin attaches the excerpt to that step so the coding agent sees it too. When the same failure repeats across cycles, the agent is told so and must try a different fix.
- **Deployment files in the plan.** Each deploy pack declares the config files it needs (`vercel.json`, `netlify.toml`, `wrangler.jsonc`, …). The plan prompt names them, and when a generated plan or optimisation plan leaves one out, Tevrin adds a deploy-engineer step to create it.
- **Every deterministic finding is acted on (part 2).** Each deterministic finding (dependency, endpoint, secret, stack check, Vercel config) gets a stable id in the review prompt. The optimisation plan must fix it in a step that names the id, or waive it with a reason when it's a false positive. Tevrin adds a step for any finding that's neither. A waived finding stops counting against the score. The dependency and endpoint checks also stop raising the known false positives: dev tooling, catch-all routes and file-based routes.
- **Build check before review (part 2).** After execution, Tevrin waits for the preview build and the required CI checks on the pushed commit. When one fails, a build-fix agent plans targeted fix steps from the logs, the coding agents run them, and Tevrin checks again. This repeats until the build is green or 5 fix attempts are used (a project setting), and only then does the project go to review. Review reuses the result for that commit instead of deploying again.

Success means:
- A new project's working branch is `tevrin/<slug>-<id>`. An existing project's next run still uses its `aide/` branch, and its branch-control config still passes.
- No page shows the browser's default grey scrollbar in either theme.
- For a failed Vercel preview build, the optimisation step names the error from the log (for example the missing module and the file that imports it) and contains the log excerpt. It is never a bare "Reproduce and fix the build failure".
- Every plan for a Vercel-deployed project creates `vercel.json` with the required config, either in the agent's own steps or in an added step.
- After a review, each deterministic finding listed in the report is either named in an optimisation step, waived with a reason, or marked "Added by Tevrin" in a step. The same finding is never re-reported cycle after cycle with no step.
- `Dependency '@eslint/eslintrc' …` (a devDependency), `Endpoint '/{any' …` and `Endpoint '/robots.txt' …` (served by `app/robots.ts`) are no longer raised.
- A project whose first build fails reaches review with a green preview build, or with the failure and a record of up to 5 fix attempts. Review never starts while a build fix is still possible.

**In scope (0.6.1):** branch prefix (§3.1) · scrollbars (§3.2) · failure evidence in review (§3.3) · deployment files in plans (§3.4) · release housekeeping (§3.5) · deterministic findings in the plan (§3.6) · check accuracy (§3.7) · build check before review (§3.8).

**Out of scope (0.6.1):**
- Renaming any other internal `aide` identifier listed in 0.6.0 §4.29 (stored formats, cookies, tokens, headers, `.aide/`, Netlify title prefixes).
- Moving existing projects or release versions from `aide/` to `tevrin/` branches.
- A JavaScript or overlay scroller. The scrollbar is CSS only.
- Fetching logs for CI providers other than GitHub Actions, GitLab CI and Bitbucket Pipelines.
- New review decision rules. Stall detection stays score-based (0.2.0 §4), and repeated failures only change the prompt (§4.37). Waivers (§4.43) only remove a finding's penalty; the decision table is unchanged.
- Tevrin running builds or tests itself (a command runner or sandbox). The build check uses the deploy platform's preview build and the repo's CI (D-0.6.1-15).
- A hard release block on a failed deployment. A failed preview stays a critical finding, and merge readiness still requires green required checks (unchanged).
- Human revocation of a waiver. Reviewers see waivers in the report and can send feedback (unchanged).

## 2. Users and permissions

Unchanged from 0.6.0. No new actions or roles. Failure log excerpts are shown only where review evidence is already shown, to users who can already see the project's review (0.2.0 permissions).

Part 2: `maxBuildFixAttempts` is edited by whoever can edit `maxReviewCycles` today (same project-settings permission). Build checks, build-fix steps and waivers are visible to anyone who can see the project's execution and review. Nobody can create or edit a waiver directly: only the review agent's output does, and Tevrin validates it.

## 3. Functional requirements

### 3.0 Amended earlier requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| 0.1.0 FR-34 branch `aide/<slug>-<short-id>`; 0.2.0 FR-60 `aide/<slug>/v<ver>` | New branches use the `tevrin/` prefix. Recorded branches are kept | FR-232, §4.35 |
| 0.6.0 §4.29 / out of scope: `aide/<slug>/v<ver>` branches stay | Withdrawn for new branches only. Every other identifier in §4.29 stays | FR-232, D-0.6.1-2 |
| 0.4.0 §4.20 branch control (`vercel.json` `"aide/**": false`, Netlify `ignore`, Cloudflare preview exclusion) | Must block every prefix the project uses (§4.36). Tevrin-written config blocks both | FR-233, FR-234 |
| 0.2.0 review prompt inputs; `RenderFindings` drops evidence | Failure findings carry a redacted log excerpt into the prompt | FR-237, FR-238 |
| 0.2.0/0.4.0 GitHub external checks (name, conclusion, URL) | Failed required checks also fetch output, annotations and a job log tail | FR-236 |
| 0.2.0 optimisation plan steps (LLM text only) | Tevrin appends a failure evidence block to fix steps | FR-239 |
| 0.2.0 plan validation (validate only, never add steps) | Tevrin adds a deploy-engineer step for missing deployment files | FR-242, FR-243 |
| Prompt templates `code_review` v8, `plan_generator` v6 | v9 and v7 | FR-238, FR-242 |
| Stack catalog 1.2.1 | 1.2.2: `tevrin/**` branch rules and `requiredFiles` in deploy packs | FR-234, FR-241 |
| 0.2.0 deterministic findings ("don't repeat them"; no step required) | Every finding has an id and needs a step or a waiver. Tevrin adds steps for the rest | FR-246–FR-250 |
| 0.2.0 compliance penalties for deterministic findings | A valid waiver removes the finding's penalty | FR-249 |
| 0.2.0 endpoint coverage (text search of the code) | Catch-all paths are read whole, and file-based routes count | FR-251, FR-252 |
| 0.2.0 dependency allowlist (all manifest dependencies) | Runtime dependencies only. Dev and test dependencies are skipped | FR-253 |
| 0.2.0/0.4.0 execution → PR job → Review | The PR job starts a build check. Review starts only after the check passes, is skipped or runs out of attempts | FR-254–FR-258 |
| 0.2.0 execution steps (plan steps only) | Build-fix steps are added to the version's steps with origin `build_fix` | FR-256 |
| `code_review` v9 | v10 (finding ids, waivers). New prompt `build_fix` v1 | FR-247, FR-256 |
| D-0.6.1-1 "no schema change" | Part 2 adds one additive migration and additive API fields (D-0.6.1-12) | §5, §6 |

### 3.1 Branch prefix

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-232 | New project branches are `tevrin/<slug>-<short-id>` and new release-version branches are `tevrin/<slug>/v<version>` (fallbacks `tevrin/project-…`). A branch already recorded for a release version, or used by the project's earlier execution steps, is kept (§4.35) | backend (domain) |
| FR-233 | Branch control (readiness checklist `branch_control`, review check `deploy_branch_control`, the 0.2.0 Vercel config finding) passes only when the config blocks every Tevrin prefix the project uses (§4.36) | backend (domain) |
| FR-234 | Config that Tevrin writes or suggests (deploy-engineer agent text, deploy pack skills and checklists, Cloudflare preview-trigger exclusions, fake stack scripts) blocks both `tevrin/**` and `aide/**` | backend (infrastructure, packs) |

**FR-232 acceptance**
- Given a new project `shop-app` (id `0192f3a4…`), which has development version 0.1.0 (FR-66), when execution prepares the working copy, then the branch is `tevrin/shop-app/v0.1.0` and is recorded on the version.
- Given a project with no development version and no recorded step branch (0.1.0-style), then the branch is `tevrin/shop-app-0192f3a4`.
- Given a release version 1.1.0 with no recorded branch, when it starts, then its branch is `tevrin/shop-app/v1.1.0` and that name is recorded on the version.
- Given a release version with recorded branch `aide/shop-app/v1.0.0`, when execution resumes, then it uses `aide/shop-app/v1.0.0`.
- Given a project with no release version whose earlier steps ran on `aide/shop-app-0192f3a4`, when execution resumes, then it uses `aide/shop-app-0192f3a4`.
- Given an unsafe slug, then the fallback is `tevrin/project-<short-id>` (same rule as FR-34, new prefix).

**FR-233 acceptance**
- Given a project that only uses `tevrin/` branches and `vercel.json` sets `"tevrin/**": false`, then branch control passes.
- Given the same project with only `"aide/**": false`, then branch control fails with the existing major finding, and the message names `tevrin/**`.
- Given a project with an `aide/` branch and `vercel.json` with only `"tevrin/**": false`, then it fails and names `aide/**`.
- Given a Netlify `[build] ignore` command, then it passes only if it names every prefix the project uses.

**FR-234 acceptance**
- Given a deploy-engineer step for Vercel, then the agent text and pack skill show `git.deploymentEnabled` with both `"tevrin/**": false` and `"aide/**": false`.
- Given Tevrin sets up a Cloudflare preview trigger, then both `tevrin/*` and `aide/*` are excluded.

### 3.2 Scrollbars

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-235 | The page and every scrolling area (sidebar, dialogs, menus, command palette, drawers, code and log blocks, tables) use a themed scrollbar (§4.39). It follows Light and Dark, needs no JavaScript, and keeps native scrolling, keyboard scrolling and touch behaviour | frontend |

**FR-235 acceptance**
- Given Chromium, Edge or Safari, then the page scrollbar is 10px wide, its track is the page background, and its thumb is a pill with a 2px gap in the track colour.
- Given Firefox, then `scrollbar-width: thin` and `scrollbar-color` give the same colours.
- Given the theme switches Light ↔ Dark, then the scrollbar colours switch with no reload.
- Given hover on the thumb, then it darkens (light) or lightens (dark) to the hover token.
- Given a scrolling area inside a card or dialog, then its track is transparent so it blends with that surface.
- A Vitest test asserts the tokens exist in both themes. A Playwright check (Chromium) asserts the `::-webkit-scrollbar` width on `html`.

### 3.3 Failure evidence in review

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-236 | For each failed **required** check on GitHub, Tevrin also fetches the check run's `output.title` and `output.summary`, up to 50 annotations (path, line, level, message), and for GitHub Actions jobs the job log tail. For failed GitLab CI jobs it fetches the job trace tail, and for Bitbucket Pipelines the failed step's log tail. Excerpts are built per §4.38 and stored as the finding's evidence in `compliance_reports.external_checks` | backend |
| FR-237 | The review prompt's `DETERMINISTIC_FINDINGS` lists each failure finding with a stable failure id, its message, and its evidence excerpt (§4.38). Other findings render as before | backend |
| FR-238 | `code_review.md` v9: for each failure finding, the optimisation plan has a fix step that names the failure id, quotes the key error line, says the likely cause and the files to change, and says how to verify. A step that only says "reproduce and fix" is not acceptable | backend (prompt) |
| FR-239 | After the review output is parsed, Tevrin appends a **Failure evidence** block (failure id, source, the excerpt) to the step that names each failure id. If no step names a failure id, Tevrin adds the block to a new step "Fix: <finding message>", owned by the agent §4.40 picks. The coding agent receives the step text with the block | backend (domain + job) |
| FR-240 | When a failure has the same fingerprint (§4.37) as one in the previous cycle's report, the prompt marks it **repeated (n cycles)** and includes the previous fix step text. The prompt requires a different approach and that the step says what was tried | backend |

**FR-236 acceptance**
- Given a failed required GitHub Actions check `build`, when the review collects checks, then the finding evidence holds the check output, the annotations and the last lines of the job log, redacted.
- Given a failed check that isn't required, then it is still dropped (unchanged).
- Given the log endpoint returns 403/404 or times out, then the finding keeps the name and URL evidence, the review continues, and a warning is logged. The review never fails because a log fetch failed.
- Given a GitLab job `test` failed, then the evidence holds the trace tail.

**FR-237 acceptance**
- Given a failed Vercel preview with a build log tail containing `Module not found: Can't resolve '@/lib/db'`, when the review prompt is built, then `DETERMINISTIC_FINDINGS` contains a `failure:preview:vercel` entry with that line inside the excerpt.
- Given a secrets finding (not a failure), then it renders exactly as in 0.6.0.

**FR-238 acceptance**
- Given the fake LLM returns an optimisation plan with a step naming `failure:preview:vercel`, then the stored plan contains that step with the evidence block from FR-239.
- A prompt test asserts v9 has the failure-step rules and front matter `version: 9`.

**FR-239 acceptance**
- Given two failure findings and an agent plan that names only the first, then the first step gets its block, and a new step "Fix: <second message>" is appended with the second block. The plan passes `PlanValidator`.
- Given the block would push a step over the plan's step-text limit, then the excerpt is cut from the top and the cut is noted (`… N lines omitted`).

**FR-240 acceptance**
- Given cycles 2 and 3 both fail with the same Vercel error, when cycle 3's prompt is built, then the finding is marked `repeated (2 cycles)` and cycle 2's fix step text is included.
- Given cycle 3 fails with a different error, then the finding isn't marked repeated.

### 3.4 Deployment files in plans

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-241 | Each deploy pack's `pack.json` declares `requiredFiles` (§4.40): the path (with accepted alternatives), the owning agent, the folder rule, and the required content as checklist lines. Catalog 1.2.2 | backend (packs) |
| FR-242 | `plan_generator.md` v7 gets the required files for the resolved deploy targets in `STACK_GUIDANCE` and must plan a step that creates each missing one with its required content | backend (prompt + domain) |
| FR-243 | After a plan or optimisation plan is parsed and before it's stored, Tevrin adds a step for each required file that is neither in the base repo nor in any step's files (§4.41). The added step is owned by the pack's agent, placed straight after the scaffold step (or first in an optimisation plan), and its text lists the required content | backend (domain) |
| FR-244 | The plan view marks added steps with an **Added by Tevrin** badge and the reason ("vercel.json was missing from the plan") | frontend + backend (additive field) |

**FR-242 acceptance**
- Given a Next.js + Vercel stack, when the plan prompt is built, then `STACK_GUIDANCE` lists `vercel.json` (owner `deploy-engineer`) with its required content.

**FR-243 acceptance**
- Given a Vercel project, a 6-step plan with no step touching `vercel.json` and no `vercel.json` in the base repo, then the stored plan has 7 steps. Step 2 is a `deploy-engineer` step creating `frontend/vercel.json`, steps 2–6 become 3–7, and dependencies are renumbered. The plan passes `PlanValidator` and the `MISSING_VERCEL_CONFIG` warning isn't raised.
- Given a step already lists `vercel.json`, or the base repo has it, then the plan is unchanged.
- Given a Netlify + Cloudflare project missing both files, then two steps are added in pack order after the scaffold step.
- Given a stack with no deploy target, then nothing is added.

**FR-244 acceptance**
- Given an added step, then the plan panel shows the badge and the reason. An agent-written step shows no badge.

### 3.5 Release housekeeping

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-245 | The release version is `0.6.1` in `Directory.Build.props`, `frontend/package.json` (+ lock) and a new changelog entry, which `check-version.sh` checks. The docs site gets `docs/history/0.6.1.md`, and the branch-name and deploy-config docs show `tevrin/` | deploy + docs |

**FR-245 acceptance**
- Given CI on the build branch, then `check-version.sh` passes with all three at 0.6.1.
- Given the docs build, then no page under `guides/` or `operations/` shows a new branch as `aide/…`, except where it describes existing projects.

### 3.6 Deterministic findings in the plan (part 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-246 | Every deterministic finding gets a stable **finding id** (§4.42), stored on the finding in the report. Failure findings keep their `failure:` ids | backend (domain) |
| FR-247 | `code_review.md` v10: `DETERMINISTIC_FINDINGS` shows each finding with its id. For every finding id, the optimisation plan must have a step that names the id and fixes it, or the reply's new `waivedFindings` list must hold the id with a reason saying why it's a false positive. Findings waived earlier in the version are shown as waived and need no action. Findings left unaddressed in the previous cycle are marked **carried (n cycles)** | backend (prompt) |
| FR-248 | After parsing, Tevrin applies §4.43: each finding id that no step names and no valid waiver covers is added to a Tevrin step ("Fix: deterministic findings", one per owning agent, marked **Added by Tevrin**) listing each id, its message and what would satisfy the check | backend (domain) |
| FR-249 | A valid waiver (§4.43) removes the finding's compliance penalty, and the finding is shown as **Waived** with the reason. It stays waived for the rest of the version while the finding's id and message are unchanged. Critical findings, secret findings and `failure:` ids can't be waived | backend (domain) + frontend |
| FR-250 | The review report shows, for each deterministic finding, how it was handled: "Step n", "Waived: <reason>", "Waiver refused", or "No step" (only when §4.43 couldn't add one) | frontend + backend (additive fields) |

**FR-246 acceptance**
- Given the findings `Dependency '@eslint/eslintrc' …` and `Endpoint '/api/todos/{id}' …`, then their ids are `finding:dependency:eslint-eslintrc` and `finding:endpoint:api-todos-id`, and they are the same in every cycle.

**FR-247 acceptance**
- A prompt test asserts v10 has the step-or-waiver rule, the `waivedFindings` output field and front matter `version: 10`.
- Given a finding waived in cycle 1, when cycle 2's prompt is built, then it shows `(waived in cycle 1: <reason>)`.
- Given a finding that had no step and no waiver in cycle 1 and is found again, when cycle 2's prompt is built, then it shows `carried (1 cycle)`.

**FR-248 acceptance**
- Given the worked example in §4.43, then the stored plan has 4 steps with the handling shown there, the added steps carry `addedBy: "tevrin"`, and the plan passes `PlanValidator`.
- Given every finding is named in a step or waived, then the plan is unchanged.

**FR-249 acceptance**
- Given a valid waiver for a major dependency finding, then the architecture category loses no deterministic penalty for it, and the report shows it as Waived.
- Given a waiver for a critical secret finding, then it's refused, the penalty stays, and the finding gets a step.
- Given cycle 2 raises the same id with a different message, then the earlier waiver no longer applies.

**FR-250 acceptance**
- Given a review with one stepped, one waived and one refused finding, then the report shows "Step 2", "Waived: <reason>" and "Waiver refused" next to them.
- Given a report stored before 0.6.1 part 2, then it renders as before with no handling label.

### 3.7 Check accuracy (part 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-251 | Spec endpoint paths are read whole, including catch-all and constrained segments (§4.44). A catch-all spec path is covered by any catch-all or fallback route in the code | backend (domain) |
| FR-252 | Endpoint coverage also counts file-based routes and static files (§4.45), so `app/robots.ts` covers `/robots.txt` and `pages/api/todos/[id].ts` covers `/api/todos/{id}` | backend (domain) |
| FR-253 | The dependency allowlist checks runtime dependencies only. Dev, test and build-only dependencies are skipped (§4.46) | backend (domain) |

**FR-251 acceptance**
- Given spec text `GET /{any*}` (or `/{*any}`, `/[...slug]`, `/*`), then the spec path is read whole, never as `/{any`.
- Given that path and a Next.js `app/[...slug]/page.tsx` or a `vercel.json` SPA rewrite, then no endpoint finding is raised.
- Given that path and no catch-all or fallback route anywhere, then the finding is raised with the whole path.

**FR-252 acceptance**
- Each worked example in §4.45 is a unit test.
- Given spec path `/robots.txt` and only `src/app/robots.ts`, or only `public/robots.txt`, then no finding is raised.

**FR-253 acceptance**
- Given `@eslint/eslintrc` only in `devDependencies`, then no dependency finding is raised.
- Given `lodash` in `dependencies` and not in the spec, then the finding is raised (unchanged).
- Each manifest row of §4.46 has a unit test.

### 3.8 Build check before review (part 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-254 | When execution completes and the PR job has pushed and queued the preview rollout, Tevrin starts a **build check** for the head commit instead of moving to Review. The project stays in Execution with status `Building` (no lifecycle change) | backend (job) |
| FR-255 | The build check waits for the version's preview rollout and the PR's required CI checks on that head commit, with the existing wait rules and timeouts (§4.47). It passes when all succeed, is skipped when there is neither a preview target nor a required check, and fails otherwise | backend (domain + job) |
| FR-256 | On a failure with attempts left, the **build-fix agent** (`build_fix.md` v1, §4.48) plans 1–5 fix steps from the failures, using the review's failure ids, excerpts and repeat marks, and an error summary of the whole log. The steps are validated, get failure evidence blocks (FR-239) and missing deploy files (FR-243), and are added to the version as execution steps with origin `build_fix`. They run through the normal execution path (Guardian, commit, push). When they're done, the PR job runs again and the next build check starts | backend |
| FR-257 | Build-fix steps may not hide errors. The Guardian rejects a build-fix step that adds `ignoreBuildErrors`, `ignoreDuringBuilds`, `@ts-nocheck`, `@ts-ignore`, `@ts-expect-error`, a file-wide `eslint-disable`, `--no-verify`, or that deletes or skips tests (§4.48) | backend (domain) |
| FR-258 | At most `maxBuildFixAttempts` fix attempts per cycle (project setting, 0–10, default 5; 0 turns the build check off, so execution goes straight to Review as in 0.6.0). When attempts run out, or the build-fix agent can't produce a valid plan after one retry, the check ends `exhausted`, and Review starts with the failures as findings (as today). The review prompt's `REVIEW_CYCLE` says the build check was exhausted after n attempts | backend + data |
| FR-259 | The execution page shows a **Build check** panel for the current cycle: status, attempt n of max, each source (preview platform and CI checks) with its result and link, and the failures with their excerpts. Build-fix steps appear in the step list with a **Build fix** badge and their attempt number. Project settings show `maxBuildFixAttempts`. Live updates use the existing execution events | frontend + backend |

**FR-254 acceptance**
- Given a project with a Vercel preview target and `maxBuildFixAttempts` 5, when the last plan step completes, then a build check (attempt 0) is created for the head SHA, and the project is still in Execution.

**FR-255 acceptance**
- Given the preview rollout succeeds and the required check `build` passes on the head SHA, then the check is `passed`, the project moves to Review, and the review collectors use that rollout and those check results without queuing a new rollout.
- Given a project with no deploy target and no required checks, then the check is `skipped` and the project moves to Review.
- Given the rollout finished for an older SHA, then the check keeps waiting for the head SHA's rollout.
- Given the rollout wait times out (30 min), then the check fails with the `failure:preview:<platform>` timeout finding, as review does today.

**FR-256 acceptance**
- Given the Vercel build fails with `Module not found: Can't resolve '@/lib/db'` in `app/page.tsx`, then the fake build-fix LLM gets the failure id, the excerpt and the error summary. Its plan is stored as a build-fix step naming `failure:preview:vercel`, with the evidence block. The coding agent runs it, the branch is pushed, a rollout is queued, and attempt 1 starts.
- Given attempts 1 and 2 fail with the same fingerprint, when attempt 3 is planned, then the prompt marks the failure `repeated (2 attempts)` and includes attempt 2's step text.
- Given the build-fix reply names no failure id, then Tevrin adds the "Fix:" step as FR-239 does.

**FR-257 acceptance**
- Given a build-fix step whose `next.config.ts` adds `typescript: { ignoreBuildErrors: true }`, then the Guardian rejects it with violation `BUILD_ERRORS_SUPPRESSED`, and the retry feedback says why.
- Given a plan step (not a build fix) that adds `// @ts-expect-error` with a reason, then it's not rejected (unchanged).

**FR-258 acceptance**
- Given `maxBuildFixAttempts` 2 and three failing checks in a row, then two fix attempts run, the third check ends `exhausted`, and Review starts with the failure findings.
- Given `maxBuildFixAttempts` 0, then no build check is created (0.6.0 behaviour).
- Given `maxBuildFixAttempts` 11 in a settings update, then the API returns 400 `VALIDATION`.
- Given an optimisation plan runs in cycle 2, then the attempt count starts again at 0.

**FR-259 acceptance**
- Given a check on attempt 2 of 5 waiting for Vercel, then the panel shows "Attempt 2 of 5 · Waiting for Vercel preview", the CI checks so far, and the previous attempt's failure excerpt.
- Given a build-fix step, then the step list shows it with the "Build fix · attempt 2" badge.
- The panel passes the axe check in the E2E a11y suite.

## 4. Domain rules

### 4.35 Branch name choice
Prefix `tevrin/`. The order is:
1. The release version's recorded `branch_name`, if any.
2. Else, for a project without release versions, the branch of its most recent recorded execution step.
3. Else, the new name.

| Recorded version branch | Latest step branch | Has version 1.2.0 | Result |
| --- | --- | --- | --- |
| `aide/shop/v1.2.0` | — | yes | `aide/shop/v1.2.0` |
| null | — | yes | `tevrin/shop/v1.2.0` (then recorded) |
| — | `aide/shop-0192f3a4` | no | `aide/shop-0192f3a4` |
| — | none | no | `tevrin/shop-0192f3a4` |

### 4.36 Required branch-control prefixes
Required = {`tevrin/`} ∪ {`aide/` if any recorded version or step branch of the project starts with `aide/`}. Vercel: `git.deploymentEnabled["<prefix>**"]` is `false` for each required prefix, and no other key matching it is `true`. Netlify: the `ignore` command names each required prefix. Examples:
- Only `tevrin/` branches, config `{"tevrin/**": false}` → pass.
- Only `tevrin/` branches, `{"aide/**": false}` → fail (missing `tevrin/**`).
- Has `aide/` branch, `{"aide/**": false, "tevrin/**": false}` → pass.
- `{"tevrin/**": false, "tevrin/shop*": true}` → fail (a true rule matches).

### 4.37 Failure fingerprint
A failure id is `failure:<source>:<name>`, for example `failure:preview:vercel` or `failure:check:build`. The fingerprint is the failure id plus the **first error line** of the excerpt (the first line matching `error|Error|ERR!|failed|FAILED|Cannot|Can't|not found`, case-sensitive as written). Before comparing, digits, hex runs of 7 or more characters, timestamps and absolute temp paths are replaced with `#`. If no line matches, the fingerprint is the failure id alone.
- `12:01:03 Error: Cannot find module 'zod' (/vercel/path0/a1b2c3d4e5/x.js)` and `12:09:44 Error: Cannot find module 'zod' (/vercel/path0/9f8e7d6c5b/x.js)` → same fingerprint → repeated.
- `Cannot find module 'zod'` vs `Type error: Property 'id' does not exist` → different.

### 4.38 Failure excerpt
The source is the stored, already-redacted log. Build the excerpt like this:
1. Run `SecretScanner.Redact` again.
2. Strip ANSI codes.
3. Take the 40 lines around the first error line (10 before, 30 after) plus the last 20 lines, joined by `… N lines omitted` if they don't overlap.
4. Add up to 10 annotations as `path:line level message`.
5. Cap the excerpt at 6,000 characters per failure and 18,000 characters across all failures in one prompt. When over the cap, keep failures in severity order and cut the rest to their first error line.

With no error line, the excerpt is the last 60 lines.

### 4.39 Scrollbar tokens
New tokens `--scrollbar-track`, `--scrollbar-thumb` and `--scrollbar-thumb-hover` in both themes. Track = `--background`. Thumb: between `--border` and `--field-border` (light about slate-300, dark about slate-600). Hover = `--field-border` (light) or `--muted-foreground` (dark). Page: `::-webkit-scrollbar { width: 10px; height: 10px }`, a thumb `border: 2px solid var(--scrollbar-track); border-radius: 999px`, the corner in the track colour. Inner areas: the track is transparent and the thumb border is transparent with `background-clip: padding-box`. Firefox: `scrollbar-width: thin; scrollbar-color: thumb track`.

### 4.40 Required deployment files (catalog 1.2.2)

| Pack | Path (alternatives) | Owner | Folder | Required content |
| --- | --- | --- | --- | --- |
| deploy-vercel | `vercel.json` | deploy-engineer | each Vercel-deployed app folder | `$schema`, `framework`, build/output settings where they differ from the defaults, `git.deploymentEnabled` `tevrin/**` and `aide/**` false, SPA rewrites for client-routed SPAs |
| deploy-netlify | `netlify.toml` | deploy-engineer | each deployed folder | `[build]` command and publish, `ignore` naming both prefixes, SPA redirect for client-routed SPAs |
| deploy-cloudflare | `wrangler.jsonc` (`wrangler.toml`, `wrangler.json`) | deploy-engineer | each Worker/Pages folder | `name`, `compatibility_date`, `main` or `assets`, env bindings declared without values |
| deploy-render | `render.yaml` | deploy-engineer | repo root | one service per deployed app, build/start commands, health check path, env var keys with `sync: false` for secrets |
| deploy-railway | `railway.json` (`railway.toml`) | deploy-engineer | each service folder | builder, start command, health check path, pre-deploy migrate command when a data pack has migrations |
| deploy-github-pages | `.github/workflows/pages.yml` | deploy-engineer (D-0.6.1-10) | repo root | build, upload-pages-artifact, deploy-pages, with `pages: write` and `id-token: write` |

The agent that owns a fix step for a failure follows the failure source: a preview or deploy failure goes to deploy-engineer, a CI check to the agent owning the failing layer (by the annotation paths, else ci-engineer).

### 4.41 Adding missing-file steps
1. For each resolved deploy target, in catalog order, and each required file: the file counts as present when its path or an alternative is in the base repo or in any step's file list, in its folder.
2. For each missing file, add a step: owner from §4.40, title "Add <path> for <platform>", text from the required-content lines, files = [path], depends on the scaffold step.
3. Insert the steps after step 1 (the scaffold) in a plan, or first in an optimisation plan. Renumber later steps and their dependencies, and set `addedBy: "tevrin"` and `addedReason`.
4. Re-run `PlanValidator`. If the added step breaks a limit, raise the existing `INVALID_PLAN` retry path. Never store an invalid plan.

### 4.42 Finding ids (part 2)
A finding id is `finding:<kind>:<subject>`, where `<subject>` is a slug: lower-case it, replace every run of characters outside `a-z0-9` with `-`, trim `-` from both ends, cut to 60 characters, and use `root` if nothing is left. Duplicates in one review get `-2`, `-3` in report order. Failure findings keep `failure:` ids (§4.37, D-0.6.1-11).

| Kind | Subject | Example |
| --- | --- | --- |
| `dependency` | package name | `@eslint/eslintrc` → `finding:dependency:eslint-eslintrc` |
| `endpoint` | spec path | `/api/todos/{id}` → `finding:endpoint:api-todos-id`; `/robots.txt` → `finding:endpoint:robots-txt`; `/` → `finding:endpoint:root` |
| `secret` | path + line | `frontend/src/config.ts` line 12 → `finding:secret:frontend-src-config-ts-12` |
| `stack` | check key + path (or `repo`) | `container_non_root` on `backend/Dockerfile` → `finding:stack:container-non-root-backend-dockerfile` |
| `vercel` | rule + path | `finding:vercel:<rule>-<path>` |

A deterministic check added later uses its own `<kind>` and the same slug rule. The id depends only on the kind and subject, so it's the same in every cycle.

### 4.43 Step or waiver (part 2)
Run after `FailureEvidenceSteps.Attach` and before `DeployFileSteps.AddValidated`, for every non-failure finding:
1. **Carried waiver.** If an earlier cycle of this version waived the same id with the same message, the finding is Waived (no action needed).
2. **Named.** If a step's title or task contains the id as a whole token (not followed by `-`, `_`, `.` plus a letter or digit), the handling is "Step n" (the lowest such n). A step wins over a waiver.
3. **Waived.** Else, if `waivedFindings` holds the id with a trimmed reason of 10–500 characters, the finding is Waived. The waiver is **refused** when the finding is critical, its kind is `secret`, or the id starts with `failure:`. A refused waiver goes on to step 4. Unknown ids in `waivedFindings` are ignored.
4. **Added.** Group the rest by owner: the agent that owns the finding's path (the manifest path for a dependency); for an endpoint, backend-engineer when the stack has a backend layer, else frontend-engineer; for a stack check, the agent its pack names; for `vercel`, deploy-engineer; else the owner of step 1. For each owner with at least one path, append a step "Fix: deterministic findings (<owner>)" with `addedBy: "tevrin"` and `addedReason` "<n> deterministic findings had no step". Its task lists each id, message and what would satisfy the check, and its files are the findings' paths (`modify`). A finding with no path goes into the task of the last step owned by that agent as "Also address <id>: <message>", or step 1 when there is none. Its handling is "Step n".
5. Re-run `PlanValidator`. If an added step breaks a limit, drop it, log a warning, and mark its findings "No step". With an empty plan, findings with no path are also "No step". "No step" findings are marked carried in the next cycle.
6. **Scoring.** Waived findings add no penalty in `ComplianceScoring` and don't count in `ReviewAssessment`. They stay in the report.

Worked example (Next.js frontend + ASP.NET backend, cycle 2). The agent's plan: step 1 frontend-engineer modifies `frontend/package.json` and names `finding:dependency:lodash`; step 2 backend-engineer modifies `backend/src/Api/TodoEndpoints.cs`. The reply waives `finding:dependency:zod` ("zod is the spec's 'schema validation library'") and `finding:secret:frontend-src-config-ts-12` ("test key").

| Finding (severity, path) | Handling |
| --- | --- |
| `finding:dependency:lodash` (major, `frontend/package.json`) | Step 1 |
| `finding:dependency:zod` (major) | Waived; architecture penalty removed |
| `finding:secret:frontend-src-config-ts-12` (critical, `frontend/src/config.ts`) | Waiver refused → added step 3 "Fix: deterministic findings (frontend-engineer)", files `frontend/src/config.ts` |
| `finding:endpoint:api-todos-id` (major, no path) | Appended to step 2 → Step 2 |
| `finding:stack:container-non-root-backend-dockerfile` (major, `backend/Dockerfile`) | Added step 4 "Fix: deterministic findings (deploy-engineer)" |

The stored plan has 4 steps.

### 4.44 Reading spec endpoint paths (part 2)
`SpecReading.PathPattern` also accepts `*`, `+`, `?` and `=`, and reads a `{…}` segment up to its closing brace (so `{id:int}` and `{*any}` stay whole). Trailing-punctuation trimming never cuts inside braces. A path left with an unclosed `{`, `[` or `<` is dropped with a warning, never reported.

Segments are literal, **param** (`:x`, `{x}`, `{x:…}`, `{x?}`, `<x>`, `[x]`) or **catch-all** (`*`, `**`, `{*x}`, `{**x}`, `{x*}`, `{x+}`, `:x*`, `:x+`, `[...x]`, `[[...x]]`, `(.*)`).
- `GET /{any*}` → `/{any*}`, a root catch-all. `/docs/[...slug]` → a catch-all with prefix `/docs`. `/api/items/{id:int}` → a param. "the route /files/*." → `/files/*`.

A catch-all path with prefix P is covered when the code has a route whose text matches P followed by a catch-all marker, or a file route (§4.45) with prefix P and a catch-all segment. A **root** catch-all is also covered by any fallback: `MapFallback`/`MapFallbackToFile`, a `'*'`, `'/*'` or `'/(.*)'` route, Next `app/[...x]`, `app/[[...x]]`, `app/not-found.*`, `pages/404.*`, a SPA rewrite in `vercel.json` (source `/(.*)` or `/:path*`), a `/*` redirect in `netlify.toml` or `_redirects`, or `not_found_handling = "single-page-application"` in a wrangler file.

### 4.45 File-based routes (part 2)
Endpoint coverage also builds routes from file paths (any folder depth, so `frontend/src/app/…` counts). A spec literal segment matches only the same literal. A spec param matches a file param or literal. A file catch-all matches one or more remaining segments, and an optional catch-all also matches zero. Trailing `/` and query strings are ignored.
- **Next.js App Router** (`app/`, `src/app/`): `page.{tsx,ts,jsx,js,mdx}` and `route.{ts,js}` give the folder path. Drop `(group)` and `@slot` folders, and skip any path with a `_private` folder. `[x]` is a param, `[...x]` a catch-all, `[[...x]]` an optional catch-all. Metadata files at any level: `robots.{ts,js,txt}` → `robots.txt`; `sitemap.{ts,js,xml}` → `sitemap.xml`; `manifest.{ts,js,webmanifest}` → `manifest.webmanifest`; `manifest.json` → `manifest.json`; `favicon.ico`; and `icon`, `apple-icon`, `opengraph-image`, `twitter-image` with any extension → that name.
- **Pages Router** (`pages/`, `src/pages/`): `x.tsx` and `x/index.tsx` → `/x`, the same for `.ts`, `.jsx` and `.js`. Skip `_app`, `_document` and `_error`.
- **Static files**: `public/<p>` → `/<p>`.
- **SvelteKit** (`src/routes/`): `+page.svelte` and `+server.{ts,js}` give the folder path, with the same `[x]`, `[...x]` and `(group)` rules.

| File | Route |
| --- | --- |
| `frontend/src/app/robots.ts` | `/robots.txt` |
| `app/(marketing)/pricing/page.tsx` | `/pricing` |
| `app/api/todos/[id]/route.ts` | `/api/todos/{param}` (covers spec `/api/todos/{id}`) |
| `app/docs/[...slug]/page.mdx` | `/docs/{catch-all}` |
| `app/_lib/x/page.tsx` | none |
| `pages/api/todos/[id].ts` | `/api/todos/{param}` |
| `pages/index.tsx` | `/` |
| `public/robots.txt` | `/robots.txt` |
| `src/routes/blog/[slug]/+page.svelte` | `/blog/{param}` |

### 4.46 Runtime dependencies (part 2)
These rules apply to the manifests `DependencyManifests.Used` already reads:

| Manifest | Checked | Skipped |
| --- | --- | --- |
| `package.json` | `dependencies`, `peerDependencies`, `optionalDependencies` | `devDependencies` |
| `requirements*.txt` | other files | files whose name contains `dev`, `test`, `lint` or `doc` |
| `pyproject.toml` | `[project].dependencies`, `[tool.poetry.dependencies]` | `optional-dependencies` groups named `dev`, `test`, `tests`, `lint` or `docs`; `[dependency-groups]`; `[tool.poetry.group.*]` |
| `*.csproj` | `PackageReference` | references with `PrivateAssets="all"`; projects with `<IsTestProject>true`, under a `tests/` folder, or named `*Tests` |
| `Cargo.toml` | `[dependencies]` | `[dev-dependencies]`, `[build-dependencies]` |
| `Gemfile` | top-level gems | gems in `group :development` or `:test` blocks |
| `pom.xml` | other dependencies | `<scope>test</scope>` |
| `go.mod` | `require` (unchanged) | none |

### 4.47 Build check (part 2)
A build check belongs to a release version, a review cycle and an **attempt**. Attempt 0 checks the commit pushed after the plan's own steps. Attempt k checks the commit after the k-th fix. With `maxBuildFixAttempts` = m, a cycle has at most m fixes and m + 1 checks.

| From | Event | To |
| --- | --- | --- |
| — | The PR job finishes after execution, with m > 0 | `waiting` (attempt 0) |
| `waiting` | No preview target for the version and no required checks (the review collectors' rules) | `skipped` → Review |
| `waiting` | The preview rollout for the head SHA and every required check on it succeeded | `passed` → Review |
| `waiting` | A source failed or timed out for the head SHA, and fixes used < m | `failed` → plan fix k+1 (§4.48) |
| `waiting` | The same, and fixes used = m | `exhausted` → Review |
| any | Fix planning is invalid after one retry, or a build-fix step is blocked and the escalation is answered "continue" | `exhausted` → Review |
| any | The version is invalidated or rolled back, or execution is stopped | `cancelled` |

- Results for an older SHA are ignored, and the check keeps waiting. The waits and timeouts are the existing ones: the preview rollout follows `PreviewReviewRules` (10 s, 30 min) and the checks follow `ExternalCheckRules` (30 s, 15 min). Waiting uses job deferral, never sleeps.
- On `passed`, `skipped` or `exhausted`, the PR job's existing advance to Review runs and **no new rollout is queued**. Review's collectors read the same rollout and checks. The attempt count resets in each review cycle.
- Worked example, m = 5: attempt 0 fails (`Can't resolve '@/lib/db'`) → fix 1 → attempt 1 fails (`Type error: Property 'id' does not exist`) → fix 2 → attempt 2 passes → Review. That's 3 checks and 2 fixes.
- Audit events: `build_check.started`, `build_check.completed` (with status) and `build_fix.planned` (with step numbers).

### 4.48 Build-fix planning (part 2)
- **Prompt** `build_fix.md` v1, agent key `build_fix`. It uses the model and credentials configured for `code_review` (D-0.6.1-17).
- **Inputs:**
  - `FAILURES`: id, source, excerpt (§4.38), and a `repeated (n attempts)` mark from the fingerprint (§4.37) across this version's earlier attempts and cycles, with the previous fix step text.
  - `ERROR_SUMMARY`: every distinct line in each stored log that matches the §4.37 error pattern, de-duplicated after normalisation, up to 30 lines and 3,000 characters per failure.
  - `STACK_GUIDANCE`: the required files (§4.40) and the packs' build commands.
  - The plan's step titles, the repo file list, and the read-only repo tools (up to 25 calls).
- **Rules (in the prompt):**
  - Fix every error in the error summary, not only the first.
  - Then look for the same class of problem elsewhere:
    - every import of a missing module;
    - type errors in touched files and their callers;
    - `package.json` scripts and lockfile agreement with the pack's build command;
    - env vars read at build time;
    - framework config.
  - 1–5 steps. Each step names the failure ids it fixes, declares its files, and says how to verify.
  - Never hide errors (the FR-257 list).
- **Output:** `{"steps": [...], "summary": "..."}` with the plan step schema.
- **Processing:**
  1. `PlanValidator` (files per step, protected paths, agents).
  2. `FailureEvidenceSteps.Attach`.
  3. `DeployFileSteps.AddValidated`.
  4. An invalid plan gets one retry with the reasons.
  5. The steps are stored as execution steps with origin `build_fix` and the check's id. Step numbers continue after the version's highest step number, and dependencies are renumbered to match (they only point within the fix plan).
- **Guardian rule** (`BUILD_ERRORS_SUPPRESSED`, build-fix steps only): reject when a written file adds, compared with its previous content, any of:
  - `ignoreBuildErrors` or `ignoreDuringBuilds`;
  - `@ts-nocheck`, `@ts-ignore` or `@ts-expect-error`;
  - an `eslint-disable` comment that isn't `-line` or `-next-line`;
  - `--no-verify`, `continue-on-error: true`, or `|| true` in a build or test script;
  - `.skip(`, `xit(`, `xdescribe(`, `[Fact(Skip`, `[Theory(Skip` or `@pytest.mark.skip`.

  Deleting a test file is also rejected. The normal retry path (3 retries, then blocked) applies.

## 5. Data
No schema changes and no migrations. Failure excerpts live in the existing `compliance_reports.external_checks` jsonb. `addedBy` and `addedReason` are new optional fields on steps in the stored `aide-plan` block. Older plans without them read as agent-written.

**Part 2** (one additive migration, D-0.6.1-12):
- New table `build_checks`:
  - `id` (v7), `project_id`, `release_version_id`, `review_cycle`, `attempt`, `head_sha`, `status` (string enum, §4.47);
  - `sources` jsonb (name, kind `preview`/`check`, conclusion, URL);
  - `failures` jsonb (failure id, fingerprint, message, excerpt, error summary);
  - `started_at`, `completed_at`, `xmin`.
  - Unique on (`release_version_id`, `review_cycle`, `attempt`), with FKs cascading like `execution_steps`.
- `execution_steps`: `origin` (string, not null, default `plan`; `plan` or `build_fix`), and a nullable `build_check_id` FK.
- `projects`: `max_build_fix_attempts` (int, not null, default 5).
- Compliance report findings (existing jsonb): optional `findingId`, `handling` (`step`, `waived`, `waiver_refused`, `no_step`), `stepNumber` and `waivedReason`. A new `waivers` list holds id, message, reason and cycle, so later cycles can read earlier waivers. Rows stored before part 2 read with no handling.

## 6. API
No new endpoints and no contract change. The plan is read from the artifact's `aide-plan` JSON block, so the optional step fields `addedBy` (`"tevrin"` or absent) and `addedReason` (string or absent) live there (D-0.6.1-10). Only Tevrin sets them; values from an agent reply are ignored.

**Part 2** (additive, contract and client regenerated):
- Project create, update and read DTOs gain `maxBuildFixAttempts` (0–10, default 5). Edited with the same permission as `maxReviewCycles`.
- A new read endpoint lists a version's build checks, newest first, beside the existing execution routes (for example `GET /api/projects/{projectId}/versions/{versionId}/build-checks`). Each item has `id`, `reviewCycle`, `attempt`, `headSha`, `status`, `sources[]`, `failures[]` (`failureId`, `source`, `message`, `excerpt`), `fixStepNumbers[]`, `startedAt` and `completedAt`. Project viewers can read it. An unknown project or version returns 404 `NOT_FOUND`.
- The execution step DTO gains `origin` (`plan` or `build_fix`) and `buildCheckAttempt` (int or null).
- The review report finding DTO gains `findingId`, `handling`, `stepNumber` and `waivedReason` (all nullable).
- The existing execution event stream sends a `buildCheckUpdated` event with the build check item.

### Error codes
None new. Log-fetch failures are logged warnings, not errors (FR-236). Plans that fail validation after a step is added use `INVALID_PLAN` (422, unchanged). An out-of-range `maxBuildFixAttempts` is 400 `VALIDATION`. `BUILD_ERRORS_SUPPRESSED` is a Guardian violation code (like the existing ones), not an API error.

## 7. Pages and UI
- Global CSS: the scrollbar (FR-235). No layout changes.
- Plan panel: the **Added by Tevrin** badge with the reason as its description (FR-244). The badge follows the 0.6.0 badge styles and keeps text contrast AA.
- Review report: the existing evidence display shows the new excerpts in a scrolling monospace block (it uses the FR-235 scrollbar).
- Review report (part 2): each deterministic finding shows its id and its handling label (FR-250). Waived findings show a neutral **Waived** badge and the reason, and are visually separate from the findings that count.
- Execution page (part 2): the **Build check** panel above the step list (FR-259):
  - States: waiting (spinner and "Waiting for <source>"), passed, failed, skipped ("No preview target or required checks"), exhausted (warning: "Build still failing after n fix attempts. Review will include the failures") and cancelled.
  - Each failure's excerpt shows in the scrolling evidence block. Earlier attempts collapse into a list.
  - Build-fix steps carry a **Build fix · attempt n** badge.
- Project settings (part 2): a "Build fix attempts" number field (0–10) next to "Max review cycles", with the help text "Fix attempts per cycle before review. 0 turns the build check off".

## 8. Non-functional requirements
- **Security:** every excerpt is redacted before and after the ANSI strip (§4.38). Raw GitHub/GitLab/Bitbucket logs are never stored whole, only the excerpt. Tokens used for log fetches are the project's existing Git credentials. No new scopes: the GitHub App already has `actions: write` (FR-145), which covers reading job logs. A 403 is skipped with a warning. Redirects never forward the `Authorization` header to another host.
- **Performance:** log fetches run in parallel with a 10-second timeout each and a 5 MB download cap. They add at most 15 seconds to a review cycle.
- **Tests:** no test calls a real provider. Fake Git and deploy providers return canned logs, and the fake LLM returns canned review and plan output (rule 10).
- **Cost and time (part 2):** each fix attempt makes one `build_fix` LLM call (plus at most one retry) and runs its 1–5 coding steps. Each check waits at most the existing preview and check timeouts. `maxBuildFixAttempts` caps both per cycle, and 0 turns it off.
- **Safety (part 2):** the build-fix agent has only the read-only repo tools. Its steps go through the same Guardian, protected paths and file limits as plan steps, plus the suppression rule (FR-257). Waivers can't hide critical, secret or failure findings (§4.43).
- **Observability (part 2):** structured logs for each build check transition (project, version, cycle, attempt, status, failure ids) and for each refused waiver.

## 9. Environments
Unchanged from 0.6.0. No GitHub App permission change (D-0.6.1-11). Part 2 needs no new accounts or permissions: the build check reads the same deployments and checks that review reads.

## 10. Decisions and assumptions

| ID | Decision | By | Status |
| --- | --- | --- | --- |
| D-0.6.1-1 | Version 0.6.1 (patch) as Ben asked, though it includes small improvements. No schema or breaking API change | Ben | agreed |
| D-0.6.1-2 | New branches use `tevrin/`. Recorded `aide/` branches are kept, and branch control requires both prefixes only for projects that have one | Ben (interview) | agreed |
| D-0.6.1-3 | Deployment files: prompt guidance plus a deterministic added step (owner: the pack's agent, because `vercel.json` is exclusive to deploy-engineer and the scaffolder can't write it) | Ben (interview) | agreed |
| D-0.6.1-4 | Review gets deploy log tails and new CI log excerpts (GitHub check output, annotations, Actions job log; GitLab trace; Bitbucket step log) | Ben (interview) | agreed |
| D-0.6.1-5 | The scrollbar is CSS only, copying benfielder.com (10px, background track, `line-strong` thumb, 2px track-coloured border, pill radius) with Tevrin tokens | Claude | assumed |
| D-0.6.1-6 | Failure ids and fingerprints use the §4.37 heuristic. Repeats change the prompt only, not the review decision | Claude | assumed |
| D-0.6.1-7 | Excerpt caps: 6,000 characters per failure and 18,000 per prompt | Claude | assumed |
| D-0.6.1-8 | Failing non-required GitHub checks stay dropped (unchanged), so log fetches are only for required checks | Claude | assumed |
| D-0.6.1-9 | Build (M46 review): Cloudflare preview triggers and Pages exclusions that exist before 0.6.1 are patched to add `tevrin/*` (keeping other excludes) on link and verify; if the patch fails, readiness `branch_control` is Missing. A Vercel key `*` doesn't match `tevrin/…` (it can't cross `/`) | Claude | assumed |
| D-0.6.1-10 | Build (M48): the GitHub Pages workflow is owned by deploy-engineer (the VCS packs exclude `pages.yml` from ci-engineer, so a ci-engineer step would be `INVALID_PLAN`), and only `.github/workflows/pages.yml` counts as present. Deploy-engineer also owns `railway.json`/`railway.toml` and `wrangler.toml`/`wrangler.json`. The added-step fields live in the `aide-plan` block, not the API contract. No step is added when the repo file list is unknown or truncated, or the optimisation plan is empty. Steps are added only after the agent's own plan validates, so retry reasons use the agent's numbering. `addedBy` survives human edits only on steps Tevrin added; a step an agent keeps when regenerating a plan becomes agent-written | Claude | assumed |
| D-0.6.1-11 | Build (M49): failure ids are slugged (`ci / test` → `failure:check:ci-test`), component-qualified when a platform has more than one component (`failure:preview:railway-backend`), and de-duplicated with `-2`, `-3`; a failed Supabase preview migration is `failure:preview:database-migration`. The fingerprint uses log and annotation lines only, never the check title. Logs keep the last 5 MB while streaming. An excerpt over 6,000 characters keeps the top (the error window). The 18,000 total cap applies only to the prompt. Fingerprints are stored under `external_checks.failures` and violations carry `failureId` (old rows read back unchanged). A step carrying a failure block has a 16,000-character limit. When no step names a failure, Tevrin adds a "Fix:" step (marked Added by Tevrin) declaring the files named in the excerpt; if none can be named, or the plan is full, the block goes on step 1 with "Also fix <id>:" (amends FR-239 acceptance 1). The GitHub App's existing `actions: write` covers job logs | Claude | assumed |
| D-0.6.1-12 | Part 2 ships in 0.6.1, as Ben asked ("in this version"). It adds one additive migration (`build_checks`, `execution_steps.origin`/`build_check_id`, `projects.max_build_fix_attempts`) and additive API fields, which amends D-0.6.1-1's "no schema change". There is still no breaking change | Ben (version) / Claude (migration) | agreed / assumed |
| D-0.6.1-13 | Every deterministic finding needs a step or a waiver, and a valid waiver lifts the finding's penalty | Ben (interview) | agreed |
| D-0.6.1-14 | Guards on waivers: critical, `secret` and `failure:` findings can't be waived. A waiver lasts for the rest of the version while the finding's id and message are unchanged. The decision table is unchanged | Claude | assumed |
| D-0.6.1-15 | Build reliability: a build-check loop between execution and review that uses the preview build and the required CI checks. Tevrin doesn't run builds itself (it has no command runner, and Railway hosting can't run Docker) | Ben (interview) | agreed |
| D-0.6.1-16 | Up to 5 fix attempts per cycle (project setting 0–10), then review with the failures. The count resets each review cycle | Ben (interview) / Claude (reset) | agreed / assumed |
| D-0.6.1-17 | `build_fix` uses the `code_review` model settings. Build-fix steps need no approval gate in any mode (Q3). The build check stays inside the Execution phase with status `Building`, so the lifecycle state machine is unchanged | Claude | assumed |
| D-0.6.1-18 | Check fixes: catch-all spec paths are read whole (§4.44), file-based routes and `public/` files count (§4.45), and only runtime dependencies are checked (§4.46) | Ben (interview) | agreed |
| D-0.6.1-19 | Unaddressed findings get one added step per owning agent. A finding with no path is appended to that agent's last step, or step 1. If an added step breaks validation it's dropped and its findings are marked "No step" (and carried), rather than failing the review | Claude | assumed |

## 11. Open questions

| ID | Question | Who | Blocks |
| --- | --- | --- | --- |
| Q1 | ~~Approve `actions: read` on the GitHub App?~~ **Resolved:** the App already has `actions: write` (FR-145), which includes read (D-0.6.1-11) | — | nothing |
| Q2 | Are the §4.40 required-content lines right for how you deploy to Railway and Render (for example, should `railway.json` be required when Tevrin's own Railway IaC is used)? | Ben | nothing (defaults apply) |
| Q3 | In `strict` approval mode, should build-fix steps wait for a human approval gate before they run? The default is no gate in any mode (D-0.6.1-17); set attempts to 0 to turn fixes off | Ben | nothing (default applies) |

## 12. Milestones

1. **M46 Version and branch prefix** (FR-232, FR-233, FR-234, FR-245 bump): version sources to 0.6.1, branch-name rules (§4.35) and branch-control rules (§4.36) with unit tests for every table row, pack and agent text, catalog 1.2.2, CLAUDE.md "next version" lines.
2. **M47 Scrollbars** (FR-235): tokens, global CSS, inner-area class, tests.
3. **M48 Deployment files in plans** (after M46; FR-241–FR-244): `requiredFiles` in packs, plan prompt v7, the add-step rule in `Aide.Domain` with tests from the §4.41 examples, the additive plan step fields, regenerated contract and client, plan panel badge.
4. **M49 Failure evidence in review** (after M48, which shares the plan pipeline; FR-236–FR-240): Git-provider log fetches behind the existing provider interfaces with fakes, excerpt and fingerprint rules in `Aide.Domain` with the §4.37/§4.38 examples as tests, review prompt v9, evidence blocks, repeated-failure input.
5. **M51 Check accuracy and finding ids** (part 2; FR-246, FR-251–FR-253): finding ids (§4.42), spec path reading (§4.44), file-based routes (§4.45) and runtime dependencies (§4.46) in `Aide.Domain`, with each worked example and table row as a unit test.
6. **M52 Findings in the plan** (after M51; FR-247–FR-250): `code_review` v10, step-or-waiver (§4.43) after failure evidence in `CodeReviewOutput.ReadPlan`, scoring without waived penalties, carried and waived prompt marks, the additive report fields, regenerated contract and client, and the report handling labels.
7. **M53 Build check before review** (after M52, which shares the plan pipeline; FR-254–FR-259):
   - the migration;
   - build-check state rules (§4.47) in `Aide.Domain`, with every table row as a unit test;
   - `build_fix` v1, its job and step creation;
   - the Guardian suppression rule;
   - the PR job and review hand-off (no second rollout);
   - the settings field, the build-checks endpoint and event, a regenerated contract and client, and the Build check panel and badge;
   - an integration test of the §4.47 worked example with fake deploy, Git and LLM providers.
8. **M50 Release 0.6.1** (after M53): changelog date, `docs/history/0.6.1.md`, SPEC and PROGRESS to `docs/0.6.1/`, row in CLAUDE.md, then tag and release after Ben confirms. Manual checks for Ben: the GitHub App permission (Q1), a real Vercel project end to end (missing `vercel.json` gets added; a broken build produces a specific fix step), and the scrollbar in Chrome, Firefox and Safari. Part 2 adds: a real Vercel project whose first build fails reaches review with a passing build or an exhausted check; a waived finding in a real review; and `/robots.txt` and catch-all findings gone on a real Next.js project.
