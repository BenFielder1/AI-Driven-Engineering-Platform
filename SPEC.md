# Tevrin — Specification (0.6.1)

2026-11-11 · Ben Fielder · **0.6.1**, built on the 0.6.0 codebase in this repo. Earlier versions: [`docs/0.1.0/SPEC.md`](docs/0.1.0/SPEC.md) … [`docs/0.6.0/SPEC.md`](docs/0.6.0/SPEC.md). Stack: [`STACK.md`](STACK.md). Source: Ben's 0.6.1 brief (2026-11-11), the interview answers in §10, a survey of the review, plan and deployment code, and the scrollbar CSS of benfielder.com.

> **How this spec relates to 0.1.0–0.6.0.** 0.6.1 is an increment on the released 0.6.0 app. **Everything in the earlier specs stays in force unless this document amends it.** Amended items are listed in §3.0 and marked *(amends …)*. New requirements continue from **FR-232**, domain rules from **§4.35**, decisions use **`D-0.6.1-n`**, and milestones continue from **M46**. Earlier tests stay green unless an amendment changes what they assert; then update the test and name the amendment in it.

## 1. Overview

0.6.1 is a small release of fixes and improvements in four areas:

- **Branch names.** New project and release branches start with `tevrin/` instead of `aide/`. Projects that already have an `aide/` branch keep it.
- **Scrollbars.** A thin, themed scrollbar replaces the browser default across the app and marketing site, styled like benfielder.com.
- **Failure-aware review.** When a build, deploy or CI check fails, the review agent gets the redacted log excerpt. It must write a specific fix step from it, and Tevrin attaches the excerpt to that step so the coding agent sees it too. When the same failure repeats across cycles, the agent is told so and must try a different fix.
- **Deployment files in the plan.** Each deploy pack declares the config files it needs (`vercel.json`, `netlify.toml`, `wrangler.jsonc`, …). The plan prompt names them, and when a generated plan or optimisation plan leaves one out, Tevrin adds a deploy-engineer step to create it.

Success means:
- A new project's working branch is `tevrin/<slug>-<id>`. An existing project's next run still uses its `aide/` branch, and its branch-control config still passes.
- No page shows the browser's default grey scrollbar in either theme.
- For a failed Vercel preview build, the optimisation step names the error from the log (for example the missing module and the file that imports it) and contains the log excerpt. It is never a bare "Reproduce and fix the build failure".
- Every plan for a Vercel-deployed project creates `vercel.json` with the required config, either in the agent's own steps or in an added step.

**In scope (0.6.1):** branch prefix (§3.1) · scrollbars (§3.2) · failure evidence in review (§3.3) · deployment files in plans (§3.4) · release housekeeping (§3.5).

**Out of scope (0.6.1):**
- Renaming any other internal `aide` identifier listed in 0.6.0 §4.29 (stored formats, cookies, tokens, headers, `.aide/`, Netlify title prefixes).
- Moving existing projects or release versions from `aide/` to `tevrin/` branches.
- A JavaScript or overlay scroller. The scrollbar is CSS only.
- Fetching logs for CI providers other than GitHub Actions, GitLab CI and Bitbucket Pipelines.
- New review decision rules. Stall detection stays score-based (0.2.0 §4), and repeated failures only change the prompt (§4.37).

## 2. Users and permissions

Unchanged from 0.6.0. No new actions or roles. Failure log excerpts are shown only where review evidence is already shown, to users who can already see the project's review (0.2.0 permissions).

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

## 5. Data
No schema changes and no migrations. Failure excerpts live in the existing `compliance_reports.external_checks` jsonb. `addedBy` and `addedReason` are new optional fields on steps in the stored `aide-plan` block. Older plans without them read as agent-written.

## 6. API
No new endpoints and no contract change. The plan is read from the artifact's `aide-plan` JSON block, so the optional step fields `addedBy` (`"tevrin"` or absent) and `addedReason` (string or absent) live there (D-0.6.1-10). Only Tevrin sets them; values from an agent reply are ignored.

### Error codes
None new. Log-fetch failures are logged warnings, not errors (FR-236). Plans that fail validation after a step is added use `INVALID_PLAN` (422, unchanged).

## 7. Pages and UI
- Global CSS: the scrollbar (FR-235). No layout changes.
- Plan panel: the **Added by Tevrin** badge with the reason as its description (FR-244). The badge follows the 0.6.0 badge styles and keeps text contrast AA.
- Review report: the existing evidence display shows the new excerpts in a scrolling monospace block (it uses the FR-235 scrollbar).

## 8. Non-functional requirements
- **Security:** every excerpt is redacted before and after the ANSI strip (§4.38). Raw GitHub/GitLab/Bitbucket logs are never stored whole, only the excerpt. Tokens used for log fetches are the project's existing Git credentials. No new scopes: the GitHub App already has `actions: write` (FR-145), which covers reading job logs. A 403 is skipped with a warning. Redirects never forward the `Authorization` header to another host.
- **Performance:** log fetches run in parallel with a 10-second timeout each and a 5 MB download cap. They add at most 15 seconds to a review cycle.
- **Tests:** no test calls a real provider. Fake Git and deploy providers return canned logs, and the fake LLM returns canned review and plan output (rule 10).

## 9. Environments
Unchanged from 0.6.0. No GitHub App permission change (D-0.6.1-11).

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

## 11. Open questions

| ID | Question | Who | Blocks |
| --- | --- | --- | --- |
| Q1 | ~~Approve `actions: read` on the GitHub App?~~ **Resolved:** the App already has `actions: write` (FR-145), which includes read (D-0.6.1-11) | — | nothing |
| Q2 | Are the §4.40 required-content lines right for how you deploy to Railway and Render (for example, should `railway.json` be required when Tevrin's own Railway IaC is used)? | Ben | nothing (defaults apply) |

## 12. Milestones

1. **M46 Version and branch prefix** (FR-232, FR-233, FR-234, FR-245 bump): version sources to 0.6.1, branch-name rules (§4.35) and branch-control rules (§4.36) with unit tests for every table row, pack and agent text, catalog 1.2.2, CLAUDE.md "next version" lines.
2. **M47 Scrollbars** (FR-235): tokens, global CSS, inner-area class, tests.
3. **M48 Deployment files in plans** (after M46; FR-241–FR-244): `requiredFiles` in packs, plan prompt v7, the add-step rule in `Aide.Domain` with tests from the §4.41 examples, the additive plan step fields, regenerated contract and client, plan panel badge.
4. **M49 Failure evidence in review** (after M48, which shares the plan pipeline; FR-236–FR-240): Git-provider log fetches behind the existing provider interfaces with fakes, excerpt and fingerprint rules in `Aide.Domain` with the §4.37/§4.38 examples as tests, review prompt v9, evidence blocks, repeated-failure input.
5. **M50 Release 0.6.1** (after M49): changelog date, `docs/history/0.6.1.md`, SPEC and PROGRESS to `docs/0.6.1/`, row in CLAUDE.md, then tag and release after Ben confirms. Manual checks for Ben: the GitHub App permission (Q1), a real Vercel project end to end (missing `vercel.json` gets added; a broken build produces a specific fix step), and the scrollbar in Chrome, Firefox and Safari.
