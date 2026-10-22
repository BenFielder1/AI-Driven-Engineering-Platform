# AIDE Platform — Specification

2026-10-07 (synced with the build 2026-10-10) · Ben Fielder · v1 (rewrite of the TypeScript/NestJS implementation in `../AIDE-Platform-old-stack/`, with improvements)

## 1. Overview

AIDE (AI-Driven Engineering Framework) turns an idea into released software through a governed, six-phase lifecycle: **Refinement → Specification → Planning → Execution → Review → Release**. AI agents produce versioned artifacts (project request, technical specification, implementation plan, code, optimisation plan). Humans approve them at gates. Every state change is deterministic, logged and reversible by rollback.

It is for engineers and project owners who want AI-driven delivery with an audit trail and human control. Success means a user can take a one-paragraph idea to a merged, tagged release in a Git repo, approving each stage, and can roll back to any earlier project version.

This rewrite keeps the old platform's behaviour and fixes its known problems: seven services plus Redis and NATS become one ASP.NET Core app with PostgreSQL-backed background work. Inconsistent review thresholds become one decision table. Regex-parsed agent output becomes structured output. Settings that were never saved are now persisted. Projects without ownership get members and roles. See §10 for every change.

**In scope (v1):**
- Accounts and sign-in (email + password, GitHub), project membership and roles
- Projects with approval mode, thresholds and limits; persisted Git, LLM and notification settings
- The lifecycle state machine, approval gates (including escalation gates), artifact versioning, approval, locking, invalidation and integrity checks
- Five cognitive agents (idea refinement, spec, plan, stepwise code generation, code review/optimisation) behind one LLM provider abstraction: Anthropic, OpenAI, Ollama, plus a deterministic Fake provider
- Execution of plan steps with the Execution Guardian, retries and escalation; Git sync to a GitHub repo or a local Git repo, with one branch and one PR per project
- Review: compliance scoring, drift detection, the release decision, review cycles
- Release (merge + tag) and rollback to any earlier project version
- Real-time project updates in the browser
- Notifications: in-app, email (SMTP), Slack, Microsoft Teams, generic webhook, with signed one-click approval links
- Admin: event log, agent run log, dead letters with retry
- The `aide` CLI (a .NET global tool) using personal API tokens

**Out of scope (v1):** GitLab as a Git provider and the GitHub Copilot LLM adapter (both existed in the old stack; see §11). Multi-tenant organisations. Billing. Hosting outside Docker Desktop Kubernetes. Running or deploying the generated software. Editing code inside AIDE.

## 2. Users and permissions

| Role | Can | Cannot |
| --- | --- | --- |
| Admin (system role) | Everything on every project; view admin pages (events, agent runs, dead letters); retry dead letters; change any user's system role | — |
| User (system role, default) | Create projects (becomes their Owner); see only projects they are a member of; manage their API tokens and notification preferences | See or open projects they're not a member of (404) |
| Project Owner (per project) | Everything a Contributor can, plus: decide gates, approve/reject artifacts, request release, roll back, change settings, manage members, delete the project | — |
| Project Contributor (per project) | View everything in the project; start agents and execution; create draft artifact versions; trigger review | Decide gates, approve/reject artifacts, release, roll back, change settings (403) |
| Project Viewer (per project) | View everything in the project | Any mutation in the project (403) |
| System actor `aide-system` | Background workers act as this identity in audit records | Sign in |

Auth: ASP.NET Core Identity with cookie auth (email + password), plus GitHub OAuth as an external login linked to the same account. The CLI uses personal API tokens sent as `Authorization: Bearer aide_pat_…`. API tokens can't list, create or revoke tokens, retry dead letters or change system roles (`FORBIDDEN → 403`): a leaked token must not mint a replacement. Cookie-authenticated mutations need an antiforgery token (`ANTIFORGERY → 403`); bearer requests are exempt. A project always has at least one Owner. The first registered user becomes Admin; the last Admin can't be demoted (`CONFLICT → 409`). Passwords need at least 8 characters with a letter and a digit; 10 failed sign-ins lock the account for 5 minutes (still `INVALID_CREDENTIALS`).

## 3. Functional requirements

### Accounts and access
1. **FR-1** Users register with email, name and password, and sign in and out with a secure cookie session.
   - Given no account for `a@x.com`, when they register with a valid password, then the account exists, they are signed in, and `GET /api/auth/me` returns them.
   - Given an existing email, when someone registers with it, then `EMAIL_TAKEN → 409`.
2. **FR-2** Users can sign in with GitHub. The first GitHub sign-in with an unknown email creates an account; a signed-in user can link GitHub to their existing account. A GitHub email that matches an existing unlinked account is not auto-linked (account-takeover risk): the user signs in and links it from Account. When GitHub OAuth isn't configured, the button is hidden and `/api/auth/github*` returns 404.
   - Given a linked GitHub account, when the user completes the GitHub flow, then they are signed in as the linked user.
3. **FR-3** The first registered user becomes Admin; everyone else starts as User. Admins can change system roles.
   - Given an empty database, when the first user registers, then their role is Admin.
4. **FR-4** Users create and revoke personal API tokens (name, optional expiry). The plain token is shown once; only a hash is stored.
   - Given a token, when the CLI calls the API with it, then the request is authenticated as its owner; when it is revoked or expired, then `401`.

### Projects
5. **FR-5** A user creates a project through a four-step wizard: Project idea (name + idea text), Configuration (approval mode, compliance threshold, max files per step, max review cycles, token budget), Git repository, Review & create. Defaults: advisory mode, threshold 0.90, 20 files, 3 cycles.
   - Given valid input, when the project is created, then it is in phase `refinement`, status `active`, project version 1, the creator is Owner, the idea is stored, and `PROJECT_CREATED` is recorded.
6. **FR-6** The projects list shows only the user's projects (all projects for Admins), with paging, search by name, filters by phase and status, and sorting by created, updated or name.
   - Given two projects where the user is a member of one, when they list projects, then they see one.
7. **FR-7** Owners edit name, approval mode, threshold and limits, and delete a project (with typed-name confirmation). Deletion cascades to everything the project owns.
   - Given a Contributor, when they `PATCH` the project, then `403`.
8. **FR-8** Owners manage members: add an existing user by email with a role, change roles, remove members. The last Owner can't be removed or demoted.
   - Given one Owner, when they try to demote themselves, then `LAST_OWNER → 409`.
9. **FR-9** Project settings are persisted: Git (provider, repo, default branch, access token), LLM (provider, model per agent, optional per-project API key), notifications (channels, event subscriptions). Secrets are encrypted at rest and never returned by the API (only "set / not set"). Slack and Teams webhook URLs count as secrets: they are returned only as a host-masked `target` plus `hasSecret`. Switching a project's Git provider to Local drops the saved token.
   - Given a saved GitHub token, when settings are read, then the response shows `hasToken: true` and no token value.
10. **FR-10** Owners test the Git connection from settings. The test reports whether the repo is reachable and writable and the default branch exists.
    - Given a bad token, when they test, then the result is `ok: false` with the provider's reason, and nothing is saved.

### Lifecycle and gates
11. **FR-11** The project's phase changes only through the lifecycle state machine (§4.1). Every change writes a state transition (from, to, actor, reason, project version) and a `PHASE_CHANGED` event in the same transaction.
    - Given phase `refinement` with no approved project request, when anyone requests advance to `specification`, then `PHASE_PRECONDITION_FAILED → 409` with the reason, and nothing changes.
12. **FR-12** Members with write access can request "advance to next phase". When the approval mode requires a gate for that entry (§4.2), a pending gate is created instead and the phase changes only when it is approved.
    - Given advisory mode in `specification` with an approved spec, when advance is requested, then a pending `spec_approval` gate exists and the phase is still `specification`.
13. **FR-13** Owners approve or reject a pending gate with an optional comment. Approval applies the gated transition. Rejection keeps the phase and re-runs the agent that produces the gated artifact with the comment as feedback.
    - Given a pending `plan_approval` gate, when it is rejected with "split step 4", then the gate is `rejected`, the plan generator runs again with that feedback, and the phase is unchanged.
14. **FR-14** When review needs a human (§4.5), an `escalation` gate is created. Its decision is one of: *continue* (start another review cycle), *roll back* (to a chosen version), or *accept* (mark release-eligible with the comment recorded). *Roll back* needs a `targetVersion`. *Reject* isn't a decision on an escalation gate (`INVALID_DECISION → 400`). An escalation for a blocked step (FR-33) accepts *continue* (blocked steps return to `pending` with attempt 0 and a job is queued) or *roll back*; *accept* is `INVALID_DECISION`. In review, *continue* adds one extra cycle: it re-enters execution through the optimisation plan, or queues a new review if there is no plan.
    - Given an open escalation gate, when an Owner chooses *continue*, then the max-cycle counter allows one more cycle and execution re-enters for the optimisation plan.
15. **FR-15** Pending gates send reminders after 24 hours and are flagged `overdue` after 72 hours. Nothing is decided automatically.
    - Given a gate pending for 73 hours, when the project page loads, then the gate shows as overdue and the Owner has been notified twice.

### Artifacts
16. **FR-16** Artifacts are versioned per type (`project_request`, `technical_specification`, `implementation_plan`, `optimization_plan`). Each version stores Markdown content, a SHA-256 content hash, its parent version, the project version at creation, its author (user or agent run), and its status.
    - Given spec v2 exists, when a new spec is produced, then it is v3 with parent v2, status `draft`.
17. **FR-17** Owners approve or reject a draft. Approve records approver and time and sets `locked` (§4.3). Reject records the comment and re-runs the producing agent with it. Approving an approved or locked artifact is a no-op.
    - Given a locked artifact, when anyone tries to change it, then `ARTIFACT_LOCKED → 409`.
    - Given a human-edited draft (FR-18), when it is rejected, then no agent re-runs; the comment is recorded.
18. **FR-18** Contributors and Owners can create a new draft version by editing the latest version's Markdown in the browser (or by upload via the CLI). It goes through the same approval rules.
    - Given locked spec v3, when a Contributor saves an edit, then spec v4 `draft` exists with parent v3.
19. **FR-19** Approving a new version of an upstream artifact invalidates downstream artifacts and execution per the cascade in §4.4, and records `ARTIFACT_INVALIDATED` for each.
    - Given an approved plan and completed steps, when spec v4 is approved, then the plan is `invalidated` and all steps of that plan are reset.
20. **FR-20** Every artifact read re-checks the content hash. On mismatch the artifact is marked `corrupted`, an `integrity` admin alert is raised, and the producing agent is re-run to regenerate it. The re-run happens only when the corrupted version is the current one (the open draft or the highest locked version) and was agent-authored (no `created_by_user_id`). The alert is an `INTEGRITY_ALERT` event (category `integrity`), listed on the admin events page.
    - Given a row whose content was altered in the DB, when it is read, then the API returns it with `integrity: "corrupted"` and an alert exists.
21. **FR-21** The artifacts page shows each type's latest version, full history, and a side-by-side diff between any two versions.
    - Given spec v1 and v3, when the user picks both, then the diff shows the changed lines.

### Agents
22. **FR-22** All LLM calls go through one provider abstraction with Anthropic, OpenAI, Ollama and Fake implementations. The provider and model come from project settings, falling back to system defaults from configuration.
    - Given a project set to Ollama with model `qwen2.5-coder`, when any agent runs, then the Ollama provider is called with that model.
23. **FR-23** Each agent invocation is an **agent run** record: agent, phase, step, provider, model, prompt template version, input artifact versions, status, token counts, cost estimate, duration, error. Runs are visible on the project and in admin.
    - Given a completed spec generation, when the user opens the run, then they see model, tokens, duration and which project request version it used.
24. **FR-24** Prompt templates are versioned files in the repo, one per agent, rendered with named placeholders (`{{PROJECT_REQUEST}}`, `{{TECHNICAL_SPECIFICATION}}`, …). A missing placeholder value fails the run before calling the provider.
    - Given the spec template references `{{PROJECT_REQUEST}}`, when there is no approved project request, then the run fails with `MISSING_INPUT` and no provider call is made.
25. **FR-25** Transient provider failures (timeouts, 429, 5xx) are retried up to 3 times with exponential backoff. After that, the run is `failed`, the triggering job moves to dead letters, and Owners are notified.
    - Given a provider that returns 503 four times, when an agent runs, then 4 attempts are logged, the run is `failed`, and a dead letter exists.
26. **FR-26** A project's token budget (optional) caps total tokens. A run that would start after the budget is used is refused with `BUDGET_EXCEEDED`, and Owners are notified. Owners can raise the budget.
    - Given budget 100k and 100k used, when execution is started, then `BUDGET_EXCEEDED → 409`.

### Refinement, specification and planning
27. **FR-27** Starting refinement runs the Idea Refinement agent on the idea text. It returns a project request draft and an optional list of clarifying questions. The user answers some or all and runs another round; each round creates a new draft version.
    - Given a draft with three questions, when the user answers two and re-runs, then a new draft exists that used those answers, and the answers are stored with the run.
28. **FR-28** On entering `specification`, the Spec Generator runs on the approved project request and produces a technical specification with the eleven required sections (Overview, Functional Requirements, Technical Architecture, Data Models, API Contracts, Feature Specifications, Design System & UI/UX, Security & Authentication, Testing Strategy, Deployment & Infrastructure, Open Questions). Output missing any section is rejected and retried once with the list of missing sections.
    - Given output missing "Testing Strategy", when it is validated, then the agent is re-run once with that feedback.
29. **FR-29** On entering `planning`, the Plan Generator runs on the approved spec and returns a structured plan: ordered steps, each with number, title, task, declared files (create/modify), dependencies (earlier step numbers) and optional user instructions. AIDE stores the structure and renders it to Markdown for the artifact.
    - Given a plan where step 3 depends on step 5, when it is validated, then it is rejected with `INVALID_PLAN` (dependencies must point to earlier steps) and retried once.
30. **FR-30** Approving a plan creates one execution step row per plan step for that plan version, all `pending`.
    - Given an approved plan with 12 steps, when execution steps are listed, then there are 12 `pending` steps for that plan version.

### Execution and Git
31. **FR-31** Starting execution runs steps one at a time, always picking the lowest-numbered `pending` step whose dependencies are `completed`. The Code Generator receives the project request, spec, plan (with step statuses), project rules and the current content of the step's declared files from the working copy.
    - Given steps 1–3 completed and step 4 depending on 2, when execution continues, then step 4 runs next.
32. **FR-32** The Execution Guardian validates each step's output before it is accepted, running all checks and reporting every violation (§4.6). Any `error` rejects the step; warnings are recorded.
    - Given output touching 25 files with max 20, when the Guardian runs, then the step is `rejected` with a `FILE_LIMIT` violation.
33. **FR-33** A rejected step is retried with the violations given to the agent as feedback: attempts 1–3 that are rejected are retried, and the 4th rejection makes the step `blocked` and creates an escalation gate (reason `step_blocked`). An unparseable code-generator reply is a rejected attempt (`INVALID_OUTPUT`).
    - Given a step rejected three times, when the fourth result is rejected, then the step is `blocked` and an escalation gate exists.
34. **FR-34** An accepted step is written to the project's working copy and committed on the project branch `aide/<project-slug>-<short-id>` with subject `[AIDE][Phase:execution][Step:N] <title>` and a body listing the files. It is pushed when the provider is GitHub. The step records branch, commit SHA and file manifest.
    - Given step 4 accepted, when it is synced, then a commit with that subject is on the project branch and step 4 shows its SHA.
35. **FR-35** Git providers: **GitHub** (clone over HTTPS with the project token; PR via the GitHub API) and **Local** (a Git repo on the AIDE data volume; the "PR" is a recorded branch comparison). Before the first step, AIDE prepares the working copy and project branch from the default branch.
    - Given a Local project, when execution starts, then a repo exists under the data volume with the project branch checked out.
36. **FR-36** When all steps of the current plan are completed, AIDE opens one PR (GitHub) from the project branch to the default branch, with a description generated from the spec, the plan and the step list, and advances the project to `review`.
    - Given the last step completes, when the sync finishes, then one PR exists and the phase is `review`.
37. **FR-37** Users can pause and resume execution. Pause lets the running step finish and starts no new step.
    - Given execution paused during step 5, when step 5 finishes, then step 6 does not start until resumed.

### Review
38. **FR-38** On entering `review` (or on manual trigger), AIDE runs a review cycle: deterministic checks (§4.7) plus the Code Review agent, which scores compliance per category against the spec with evidence (file and reason), lists violations, missing features and drift areas, and produces an optimisation plan.
    - Given a review cycle, when it completes, then a compliance report exists with category scores, overall score, drift level, decision and evidence, plus an `optimization_plan` draft.
39. **FR-39** The release decision follows §4.5. `release_eligible` creates a `pre_release` gate in every approval mode. `review_required` re-enters execution with the optimisation plan's steps once that plan is approved (FR-40), if cycles remain. If the review proposes no steps, it gets one corrective re-run and then escalates with reason `no_optimization_plan`. `escalation_required` creates an escalation gate.
    - Given score 0.82, threshold 0.90, drift low, cycle 1 of 3, when the decision runs, then the project re-enters `execution` with the optimisation steps.
40. **FR-40** Re-entering execution from review runs the optimisation plan's steps (generated by the review agent and approved like any artifact) on the same branch, then returns to review. While the plan is a draft the project stays in `review`; approving it applies `review → execution` (the plan must be locked with steps left). Rejecting or corrupting it revises the plan for the same cycle.
    - Given an approved optimisation plan with 3 steps, when they complete, then review cycle 2 starts automatically.
41. **FR-41** The review page shows the latest report, compliance score trend across cycles, category breakdown, drift level and areas, and evidence for each violation.
    - Given three cycles, when the review page loads, then the trend chart shows three points.

### Release and rollback
42. **FR-42** Approving the `pre_release` gate moves the project to `release` (status `releasing`) and starts the release job. The job merges the project PR (or the project branch, for Local) into the default branch, creates an annotated tag `v<project version>-aide`, sets status `released`, and records the release. The tag uses the version before the release completes (approving at v6 gives `v6-aide`; the project is then v7). On GitHub the PR is merged through the API with the method in `GitHub:ReleaseMergeMethod` (`merge` default, `squash` or `rebase`), pinned to the head commit AIDE checked. Release is idempotent: a tag of that version that already contains the branch head is reused as a no-op success. If the tag name exists on other commits (a re-release after rollback), the first free `v<N>-aide.2`, `.3`… is created and recorded.
    - Given a pending pre-release gate, when an Owner approves it, then the branch is merged, the tag exists, and status is `released`.
43. **FR-43** A merge conflict blocks the release. The release record is `failed` with the conflicting files, the project stays in `release` with status `releasing`, and Owners are notified. After resolving the conflict on the branch, an Owner retries with `POST /release`.
    - Given the default branch changed the same lines, when the release job runs, then the release is `failed` with `MERGE_CONFLICT` and the files listed; a retry after resolution succeeds. Other release failures are recorded on the release row (`GIT_ERROR`, `RELEASE_CANCELLED`).
44. **FR-44** Owners roll back to an earlier project version with a reason. The rollback restores the phase active at that version, invalidates artifacts and resets steps created after it, cancels pending gates and queued jobs, and records a rollback event — all in one transaction (§4.8). Released projects can roll back too; the branch, tags and release history are kept.
    - Given version 7 in `review`, when an Owner rolls back to version 4 (in `planning`), then the phase is `planning`, artifacts from versions 5–7 are invalidated, and a rollback record lists them.
45. **FR-45** Rollback is limited to one per project per minute and refused while a step or agent run is in progress or a release job is running (`ROLLBACK_BLOCKED`, with `details: ["reason: step_in_progress|agent_run_in_progress|release_running|release_partially_applied"]`).
    - Given a rollback 30 seconds ago, when another is requested, then `RATE_LIMITED → 429`.

### Real-time, notifications and activity
46. **FR-46** Project pages update live. The server pushes project events (phase, gate, artifact, step, run, review, release, rollback) to everyone viewing that project, and the page refreshes the affected data.
    - Given two users on the same project page, when a gate is approved, then the other user's page shows it within 2 seconds without reloading.
47. **FR-47** Notifications are sent for: gate required, gate overdue, artifact ready for approval, step blocked, run failed, budget exceeded, review completed, release completed or failed, rollback. Each user sees an in-app notification centre with unread count. Owners configure project channels: email (SMTP), Slack and Teams incoming webhooks, and a generic webhook (HMAC-signed body: header `X-AIDE-Signature: sha256=<hex HMAC-SHA256 of the raw body>`, plus `X-AIDE-Timestamp`, `X-AIDE-Delivery`, `X-AIDE-Event`). A webhook channel requires a signing secret (`VALIDATION` otherwise). Recipients: Owners for actionable types (gates, drafts, blocked steps, failed runs, budget, failed release); all members for review completed, release completed and rollback. Users can mute types in-app (`user_notification_preferences`); project channels are set by Owners. `BUDGET_EXCEEDED` is written on every refusal but notifies only on the first refusal since the budget last changed. The unread count is polled every 30 s (paused while the tab is hidden) and refreshed shortly after a live event that can notify.
    - Given Slack configured for "gate required", when a gate is created, then one Slack message is sent.
48. **FR-48** Gate notifications include signed one-click approve/reject links. A link is single-use, expires after 72 hours, and opens a confirmation page that requires the user to be signed in as an Owner of that project. Links exist only for `spec_approval`, `plan_approval` and `pre_release` gates, and only when `Approvals:SigningKey` is at least 32 characters; otherwise the notification links to the project page.
    - Given a used link, when it is opened again, then the page says it has already been used and nothing changes.
49. **FR-49** Delivery is retried with backoff per channel. A channel that succeeds is never re-sent when another channel fails. There are 5 attempts with backoff 1 min, 5 min, 30 min, 2 h. A delivery that can't be attempted is `skipped` (email without SMTP settings, or the gate was decided before a retry). Every send is capped at 60 s.
    - Given Slack succeeds and email fails, when retry runs, then only email is retried.
50. **FR-50** Each project has an activity feed: transitions, gate decisions, artifact changes, agent runs and step results, newest first, filterable by type.
    - Given 200 events, when the feed loads, then it shows the latest 50 with "load more".

### Admin and CLI
51. **FR-51** Admins see a cross-project event log (filter by project, type, status), agent run log, and dead letters with their error. They can retry a dead letter, which re-enqueues the original job. The event "status" filter is the outcome `ok` or `failure` (events are append-only). Retry needs a browser session, and returns `NOT_FOUND` if the job isn't `dead`, or `CONFLICT → 409` if an execution job is already queued or running for the project.
    - Given a dead letter, when an Admin retries it, then the job runs again and the dead letter is marked `retried`.
52. **FR-52** The `aide` CLI (a .NET global tool) supports: `config set|get|path`, `login`, `logout`, `projects list`, `project init`, `status <id>`, `artifact list|history|show|approve|reject|edit`, `execute <id>` (`--pause`, `--resume`), `review <id>`, `release <id>` (approves the pending `pre_release` gate, else retries the release), `rollback <id> <version> --reason`, `logs <id>`. The token is set only through `aide login` (prompt, stdin or `--token`) and is stored per API URL and sent only to that URL; plain http is allowed only for loopback hosts. Settings precedence: flag, then `AIDE_API_URL` / `AIDE_TOKEN` / `AIDE_CONFIG`, then the config file, then `http://localhost:8080`. Output is human-readable by default and JSON with `--json`. Exit codes: 0 success, 1 error, 2 usage, 3 auth (401/403), 4 conflict (409).
    - Given a valid token, when `aide status <id> --json` runs, then it prints the project state as JSON and exits 0; on `403` it exits 3.

## 4. Domain rules

### 4.1 Phases and transitions
- Order: `refinement → specification → planning → execution → review → release`. Forward transitions go to the next phase only.
- The other allowed moves are: `review → execution` (review cycle), any phase → an earlier phase (rollback, or the invalidation fallback in §4.4), and `release → release` (no-op). A rollback may restore the phase the project is already in, at an earlier version.
- Entry conditions:

| Enter | Requires |
| --- | --- |
| specification | `project_request` approved/locked |
| planning | `technical_specification` approved/locked |
| execution | spec and `implementation_plan` approved/locked; plan has ≥ 1 step; Git working copy prepared |
| review | all steps of the current plan (or optimisation plan) `completed` |
| release | latest decision `release_eligible` (or an escalation accepted); `pre_release` gate approved (the approval is the trigger); no other pending gate |

- Entering `execution` (by advance or `plan_approval`) prepares the Git working copy first. Entering it from `review` needs the optimisation plan locked with at least one step left.

- Status follows phase: `refinement|specification|planning → active`, `execution → building`, `review → reviewing`, `release → releasing`. `released` is set only when the release completes. `rolled_back` is set by a rollback and cleared by the next forward transition.
- The project version increments on every forward phase transition and on release. It never increments on `review → execution` (the review cycle loop) or a no-op; `execution → review` does increment.
- A gated advance records the requester's reason in the gate; the transition recorded on approval reads "<reason> (<type> gate approved[: <comment>])".
- Every transition is applied with optimistic concurrency on the project. Concurrent attempts produce one success and one `CONFLICT → 409`.

### 4.2 Gates by approval mode

| Entering | strict | advisory | autonomous |
| --- | --- | --- | --- |
| planning (`spec_approval`) | gate | gate | none |
| execution (`plan_approval`) | gate | none | none |
| release (`pre_release`) | gate | gate | gate |
| escalation (§4.5) | gate | gate | gate |

- With no gate, the transition applies as soon as its conditions are met.
- A gate decision is final. Only pending gates can be decided (`GATE_NOT_PENDING → 409`).
- Rejecting a `spec_approval` gate re-runs the **Spec Generator**, and rejecting a `plan_approval` gate re-runs the **Plan Generator**. The old stack re-ran idea refinement for spec gates; that's fixed here.
- `release_eligible` always creates a `pre_release` gate, in every approval mode.
- Escalation gates are decided with `continue`, `rollback` (with `targetVersion`) or `accept`. Anything else, including `reject`, is `INVALID_DECISION → 400`.
- Approving a new artifact version cancels pending gates that went stale (`GATE_CANCELLED`): all of them on a phase fallback, those whose artifact was invalidated, and those holding an older version of the type. The Owner re-requests the advance. Gates are never re-pointed, because decisions are final.

### 4.3 Artifact lifecycle
- Status flow: `draft → locked` (approve), `draft → rejected` (reject), `draft|locked → invalidated` (cascade or rollback), and any status → `corrupted` (hash mismatch).
- `approved` exists only as the moment of approval; stored rows go straight to `locked`.
- Only one `draft` per type at a time. A new draft supersedes the previous draft, which becomes `superseded`. Creating a draft takes an optional `baseVersion`; a stale base is `CONFLICT → 409` with `latestVersion` in the details.
- Drafts are allowed from the type's producing phase onwards; an optimisation plan only in `review`; none while `releasing` (`WRONG_PHASE`). A version with no `created_by_user_id` is agent-authored.
- Approving a draft created at a project version above the current one (stale after a rollback or fallback) is `ARTIFACT_NOT_DRAFT → 409`.
- The "current" artifact of a type is the highest version with status `locked`.
- Content hash: SHA-256 of the UTF-8 content with line endings normalised to `\n`, stored as lowercase hex.

### 4.4 Invalidation cascade (when a new version of the left-hand type is approved)

| Approved | Invalidates |
| --- | --- |
| project_request | technical_specification, implementation_plan, optimization_plan, all steps |
| technical_specification | implementation_plan, optimization_plan, all steps |
| implementation_plan | optimization_plan, steps of older plan versions |
| optimization_plan | nothing |

- Invalidation never deletes rows. It covers drafts as well as locked versions, so a stale draft can't be approved. Steps are reset to `invalidated`, queued execution jobs are cancelled, and the phase falls back to the phase that produces the first invalidated artifact. The fallback is recorded as its own transition kind (earlier phase, status follows phase, project version unchanged).
- Example: approving spec v4 while in `review` → phase `planning`, status `active`.

### 4.5 Review decision (one table; replaces the old stack's three inconsistent rule sets)
Inputs: score `s` (0–1), threshold `t`, drift `d`, cycle `c`, max cycles `m`. Evaluate the rows in order:

| # | Condition | Decision |
| --- | --- | --- |
| 1 | `s < 0.75` or `d = severe` | `escalation_required` |
| 2 | `s ≥ t` | `release_eligible` |
| 3 | `c ≥ m` | `escalation_required` (max cycles) |
| 4 | `c ≥ 2` and `s ≤ previous s` | `escalation_required` (no improvement) |
| 5 | otherwise | `review_required` |

Worked examples (t = 0.90, m = 3):
- 0.93 / low / c1 → release_eligible
- 0.82 / low / c1 → review_required
- 0.82 / low / c3 → escalation (max cycles)
- 0.84 then 0.83 / c2 → escalation (no improvement)
- 0.95 / severe → escalation
- 0.70 / none → escalation

The previous score in row 4 is the latest earlier-cycle report on the current timeline (a failed review cycle doesn't skip the row). Reports from before the latest rollback, or above the current project version, are not part of the timeline. Escalation reasons are `low_score`, `severe_drift`, `max_cycles`, `no_improvement`, `step_blocked` and `no_optimization_plan`.

### 4.6 Execution Guardian checks (all run; any `error` rejects)
1. **single-step** (error): output targets exactly the requested step, which is `pending` or `in_progress`, and contains at least one file.
2. **file-limit** (error): files touched ≤ project max files per step.
3. **dependency-order** (error): all declared dependencies are `completed`.
4. **plan-alignment**: a produced file not declared for this step is an error; a declared file that wasn't produced is a warning. Paths are normalised (forward slashes, no leading `./`) and compared exactly.
5. **scope-creep** (error): no file declared by another step; no protected path (`.git/`, `.aide/`, CI workflows, lockfiles, env files) unless declared in this step.
6. **secret-scan** (error): no content matching known secret patterns (API keys, private keys, tokens). Secret values are never echoed in violations.

Path safety (applies to plan validation and the Guardian):
- A path that is absolute, contains `..` or a drive letter, has a `.git` segment (including Windows aliases such as `.git.` or `~1` short names), control characters, line breaks or backticks, reserved device names (`CON`, `NUL`, `AUX`, `PRN`, `COM`, `LPT`), `:<>"|?*`, or a segment ending in a dot or space is `UNSAFE_PATH` (error), even when declared.
- Nothing under `.aide/` is writable by agents, and no plan may declare it. Agent-generated plans may not declare any other protected path either; Owner-edited plans may (§4.6 "unless declared").
- Protected paths match case-insensitively. Duplicate normalised produced paths are an error (`DUPLICATE_PATH`).
- Plan approval re-validates the plan against the project's current max files per step.

### 4.7 Review scoring
- **Categories and weights:** feature coverage 0.30, API contracts 0.20, data model 0.15, architecture 0.15, security 0.10, testing 0.10.
- **Category scores:** the review agent scores each category 0–1 with evidence. Deterministic checks then apply fixed penalties (critical −0.20, major −0.10, minor −0.03 each, floored at 0) to their category.
- **Deterministic checks** are stack-agnostic (severities: secret = critical, missing spec endpoint = major, dependency not on the allowlist = major): secret scan (security), dependency allowlist extracted from the spec's architecture section (architecture), and every spec API endpoint path appearing somewhere in the code (API contracts). Only deterministic findings carry penalties; the agent's own violations are reported without one. The allowlist check is skipped when the spec has no Dependencies list. Lines the secret scan flags are redacted before code is sent to the review agent.
- **Overall score** = Σ category × weight, rounded to 3 dp.
- **Drift level** comes from the agent's drift areas, weighted critical 3, major 2, minor 1: total 0 → none, 1–3 → low, 4–8 → moderate, ≥ 9 → severe.

### 4.8 Rollback
- The target must be an existing project version below the current version. A target whose recorded phase is `release` is refused (`INVALID_TARGET_VERSION`). Checks run in the order `INVALID_TARGET_VERSION`, `ROLLBACK_BLOCKED`, `RATE_LIMITED` (with `Retry-After`). The reason is trimmed; blank is `VALIDATION`.
- The phase is restored from the latest state transition recorded at that version (refinement if there is none, as for v1).
- Rollback invalidates artifacts with `project_version_at_creation > target`. Steps belonging to invalidated plans are reset. Pending gates are cancelled, queued jobs are cancelled, and the review state (`review_cycle`, extra cycles) is cleared. Blocked and rejected steps of surviving plans return to `pending` with attempt 0; completed steps stay completed, because their commits stay on the branch.
- Rollback locks the project's queued and running job rows in its transaction. A running release job blocks it; a queued release job is cancelled (`RELEASE_CANCELLED`).
- The Git branch is **not** rewritten. Later commits stay in history, and the next execution adds new commits.
- The project version is set to the target; the next forward transition makes it target + 1.

### 4.9 Background work
- Every state change and its follow-up jobs are written in one transaction (outbox).
- Workers claim jobs with `FOR UPDATE SKIP LOCKED`. A job gets 4 attempts (1 + 3 retries) with backoff 2 s / 8 s / 32 s, then becomes a dead letter. Non-transient failures fail at once. An expired lease counts as a failed attempt. A retried dead letter becomes `retried` and a new queued job with the same payload is created.
- Handlers are idempotent: step completion is keyed by step id, so a re-delivered completion doesn't double-count, which was a bug in the old stack.
- At most one execution job runs per project at a time, enforced by a partial unique index on `jobs(project_id)` where `type = 'execution'` and status is `queued` or `running`.
- One execution job is one step attempt. The next job is enqueued in the same transaction as the step outcome, unless paused, blocked or done. A redelivered job finds its step's commit by an `AIDE-Step-Id` trailer.
- A job's PR or release work re-checks the phase after slow Git work and records nothing if a rollback superseded it.

## 5. Data model

| Entity | Key fields | Constraints / indexes | Notes |
| --- | --- | --- | --- |
| user | id, email (citext), name, system_role, github_id?, avatar_url?, created_at | unique email; unique github_id | ASP.NET Identity tables underneath; all tables are plural snake_case |
| api_token | id, user_id, name, token_hash, last_used_at, expires_at?, revoked_at? | unique token_hash | plain value shown once; also created_at on api_token, project_member and approval_link |
| project | id, name, slug, idea, current_phase, status, project_version, approval_mode, compliance_threshold numeric(3,2), max_files_per_step, max_review_cycles, token_budget?, tokens_used, execution_paused, review_cycle, extra_review_cycles, last_rollback_at?, created_at, updated_at | index (status, current_phase); `xmin` concurrency | |
| project_member | project_id, user_id, role | PK (project_id, user_id) | ≥ 1 owner enforced in domain |
| project_git_settings | project_id, provider (github/local), repo_url?, default_branch, token_encrypted?, branch_name?, pull_request_url?, pull_request_number? | PK project_id | token via Data Protection |
| project_llm_settings | project_id, provider, default_model, models_by_agent jsonb, api_key_encrypted? | PK project_id | |
| project_notification_settings | project_id, channels jsonb (type, target, secret_encrypted, events[]) | PK project_id | |
| artifact | id, project_id, type, version, parent_version?, content text, content_hash, status, project_version_at_creation, created_by_user_id?, agent_run_id?, approved_by?, approved_at?, rejection_comment?, created_at | unique (project_id, type, version); index (project_id, type, status) | content immutable once inserted |
| approval_gate | id, project_id, type (spec_approval/plan_approval/pre_release/escalation), artifact_id?, status (pending/approved/rejected/cancelled), decision?, reviewer_id?, comment?, overdue, reminded_at?, decided_at?, created_at | partial unique (project_id, type) where pending | escalation `decision`: continue/rollback/accept; target_version? for rollback decisions |
| execution_step | id, project_id, plan_artifact_id, step_number, title, task, declared_files jsonb, dependencies int[], user_instructions?, status (pending/in_progress/completed/rejected/blocked/invalidated), attempt, violations jsonb?, file_manifest jsonb?, commit_sha?, branch_name?, started_at?, completed_at? | unique (plan_artifact_id, step_number) | |
| agent_run | id, project_id, agent, phase, step_id?, provider, model, prompt_version, input_artifacts jsonb, user_feedback?, status, attempts, input_tokens, output_tokens, cost_estimate numeric(10,4), duration_ms, error?, output jsonb?, completed_at?, created_at | index (project_id, created_at) | replaces agent_logs; one row per job (run id = job id) |
| state_transition | id, project_id, from_phase, to_phase, actor, reason, project_version, created_at | index (project_id, created_at) | |
| compliance_report | id, project_id, review_cycle, project_version, score numeric(4,3), threshold numeric(3,2), category_scores jsonb (agent score, penalty, evidence {file, reason}), violations jsonb (source deterministic/agent, evidence), missing_features jsonb, drift_level, drift_areas jsonb, decision, escalation_reason?, optimization_plan_artifact_id? (FK, set null), agent_run_id, created_at | index (project_id, review_cycle) | |
| rollback_event | id, project_id, from_version, target_version, restored_phase, reason, invalidated_artifact_ids uuid[], reset_step_ids uuid[], requeued_step_ids uuid[], cancelled_gate_ids uuid[], cancelled_job_ids uuid[], triggered_by, created_at | index (project_id, created_at) | |
| release | id, project_id, project_version, attempt, tag, merge_commit_sha?, pr_url?, status (succeeded/failed), error_code?, error_message?, conflicting_files, created_at, completed_at? | unique (project_id, project_version, attempt) | a re-release after rollback reuses a version number |
| event | id, project_id?, type, payload jsonb, actor, created_at | index (project_id, created_at); index (type) | append-only audit log; source for the live feed |
| job | id, type, payload jsonb, project_id?, status (queued/running/succeeded/failed/dead/cancelled/retried), attempts, run_after, locked_by?, locked_at?, last_error?, created_at, completed_at? | index (status, run_after); partial unique (project_id) where type = execution and status in (queued, running) | outbox + work queue |
| notification | id, user_id, project_id?, type, title, body, link, notification_key?, read_at?, created_at | index (user_id, read_at); unique (user_id, notification_key) | in-app; key is `<type>:<sourceId>` so redelivery never duplicates |
| user_notification_preferences | user_id, muted_types, updated_at | PK user_id | in-app mute list only |
| notification_delivery | id, project_id?, notification_key, channel, status (pending/sent/failed/dead/skipped), attempts, last_error?, next_attempt_at?, created_at, sent_at? | unique (notification_key, channel) | per-channel idempotency |
| approval_link | id, gate_id, decision, nonce_hash, expires_at, used_at? | unique nonce_hash | single use |

Database: PostgreSQL (major version: 17)

Conventions: snake_case names, UUIDv7 keys generated by the app, UTC `timestamptz`, enums stored as strings, `jsonb` only for payloads and evidence lists. The working copies for Git live on a persistent volume (`/data/workspaces/<project-id>`), not in the database.

## 6. API

All routes are under `/api`, JSON, ProblemDetails errors with `errorCode`. "Member" means any project role, "Writer" means Contributor or Owner.

| Method | Route | Body / query | Response | Errors (code → status) | Auth |
| --- | --- | --- | --- | --- | --- |
| POST | /api/auth/register | email, name, password | 201 user | `EMAIL_TAKEN → 409`, `VALIDATION → 400` | public |
| POST | /api/auth/login · /logout | email, password | 204 | `INVALID_CREDENTIALS → 401` | public / user |
| GET | /api/auth/me | — | user + system role | — | user |
| GET | /api/auth/github · /github/link · /github/callback | — | redirect (404 when OAuth isn't configured); a failure redirects to `/login?error=GITHUB_AUTH_FAILED&reason=oauth_failed\|link_required\|already_linked` (a failed link lands on `/account?error=…`) | — | public / user (link) |
| GET | /api/auth/providers · /antiforgery | — | whether GitHub sign-in is configured / antiforgery token cookie | — | public / user |
| GET/POST/DELETE | /api/me/tokens[/:id] | name, expiresAt? | token list / 201 token (once) / 204 | `NOT_FOUND → 404` | user |
| GET/PATCH/POST | /api/me/notifications · /unread-count · /:id/read · /read-all | page | notifications / unread count | — | user |
| GET/PUT | /api/me/notification-preferences | mutedTypes | preferences | `VALIDATION → 400` | user |
| GET | /api/projects | page, limit≤100, search, phase, status, sortBy, sortOrder | paged list | — | user |
| POST | /api/projects | wizard fields | 201 project | `VALIDATION → 400` | user |
| GET/PATCH/DELETE | /api/projects/:id | partial project | project / 204 | `NOT_FOUND → 404`, `FORBIDDEN → 403`, `CONFLICT → 409` | member / owner |
| GET | /api/projects/:id/state | — | phase, status, version, artifact summary, execution counts, review state, pending gates | — | member |
| GET/POST/PATCH/DELETE | /api/projects/:id/members[/:userId] | email, role | members | `USER_NOT_FOUND → 404`, `LAST_OWNER → 409` | member / owner |
| GET | /api/projects/:id/settings | — | git, llm, notifications (secrets as flags) | — | owner |
| PATCH | /api/projects/:id/settings/git · /llm · /notifications | section fields | section | `VALIDATION → 400` | owner |
| POST | /api/projects/:id/settings/git/test | — | ok, reason | — | owner |
| POST | /api/projects/:id/phase/advance | reason? | state (or pending gate) | `PHASE_PRECONDITION_FAILED → 409`, `GATE_PENDING → 409` | writer |
| GET | /api/projects/:id/refinement | — | current round: draft, questions, answers, status (`cancelled` with a reason if the job was skipped) | — | member |
| POST | /api/projects/:id/refinement/run | answers[]? | 202 run | `BUDGET_EXCEEDED → 409`, `WRONG_PHASE → 409`, `REFINEMENT_IN_PROGRESS → 409` | writer |
| GET | /api/projects/:id/artifacts | — | latest per type | — | member |
| GET | /api/projects/:id/artifacts/:type/latest · /history · /versions/:v | — | artifact(s) with integrity | `NOT_FOUND → 404` | member |
| GET | /api/projects/:id/artifacts/:type/diff | from, to | diff (Myers, 3 lines of context, collapsed `skipped` runs; refuses more than 100,000 lines with `VALIDATION`) | `NOT_FOUND → 404` | member |
| POST | /api/projects/:id/artifacts/:type | content, baseVersion? | 201 draft | `WRONG_PHASE → 409`, `CONFLICT → 409` (stale base), `INVALID_PLAN → 400` (plan types; per-step reasons) | writer |
| POST | /api/projects/:id/artifacts/:artifactId/approve · /reject | comment? | artifact | `ARTIFACT_NOT_DRAFT → 409`, `ARTIFACT_LOCKED → 409`, `INVALID_PLAN → 400` (approve) | owner |
| GET | /api/projects/:id/gates | status? | gates | — | member |
| POST | /api/projects/:id/gates/:gateId/approve · /reject | comment? (+ decision, targetVersion for escalation) | gate | `GATE_NOT_PENDING → 409`, `INVALID_DECISION → 400` | owner |
| GET | /api/projects/:id/execution/steps | planType?, planVersion? | steps of the current plan (newest locked plan of either type) when omitted | — | member |
| POST | /api/projects/:id/execution/start · /pause · /resume | — | 202 state | `PHASE_PRECONDITION_FAILED → 409`, `BUDGET_EXCEEDED → 409` | writer |
| GET | /api/projects/:id/runs[/:runId] | agent?, page | agent runs | — | member |
| GET/POST | /api/projects/:id/git/pull-request | — | recorded PR / 202 re-queues the PR job when no PR is recorded for the current plan | `WRONG_PHASE → 409` | member / writer |
| POST | /api/projects/:id/review | — | 202 | `WRONG_PHASE → 409`, `REVIEW_IN_PROGRESS → 409` | writer |
| GET | /api/projects/:id/compliance/latest · /trend · /drift | — | report / series / drift | `NOT_FOUND → 404` | member |
| POST | /api/projects/:id/release | — (retries a failed release) | 202 release | `NOT_RELEASING → 409`, `RELEASE_IN_PROGRESS → 409` | owner |
| GET | /api/projects/:id/releases · /releases/latest | — | release attempts | `NOT_FOUND → 404` | member |
| POST | /api/projects/:id/rollback | targetVersion, reason | rollback record | `INVALID_TARGET_VERSION → 400`, `ROLLBACK_BLOCKED → 409`, `RATE_LIMITED → 429` | owner |
| GET | /api/projects/:id/rollbacks · /versions | — | rollback history / versions up to the current one with their recorded phase | — | member |
| GET | /api/projects/:id/activity | type?, before? | events page | — | member |
| GET/POST | /api/approvals/:token | decision | confirmation view data / gate | `LINK_EXPIRED → 410`, `LINK_USED → 409`, `FORBIDDEN → 403` | user (owner of project) |
| GET | /api/admin/events · /runs · /dead-letters | filters (events: project, type, status `ok`\|`failure`), page, limit | lists with total | — | admin (a token is enough) |
| GET | /api/admin/users | page, limit | users | — | admin |
| POST | /api/admin/dead-letters/:jobId/retry | — | job; writes `DEAD_LETTER_RETRIED` | `NOT_FOUND → 404` (not `dead`), `CONFLICT → 409` | admin (browser session) |
| PATCH | /api/admin/users/:id/role | systemRole | user | `CONFLICT → 409` (last Admin) | admin (browser session) |
| GET | /api/health/live · /ready | — | 200 / 503 | — | public |
| WS | /api/hubs/projects | join(projectId) | `projectEvent` messages | `FORBIDDEN` on join | member |

Common errors on every route: `VALIDATION → 400` (also 405 and 415), `UNAUTHENTICATED → 401`, `FORBIDDEN → 403`, `ANTIFORGERY → 403` (cookie-authenticated mutations without a valid `X-XSRF-TOKEN` header), `NOT_FOUND → 404` (also for projects the user isn't a member of), `CONFLICT → 409` (concurrency).

Wire conventions: enums are snake_case strings (`admin`, `spec_approval`); validation error keys are camelCase (`git.repoUrl`, `channels[0].target`). Agent run and job failure codes (not HTTP errors) include `MISSING_INPUT`, `MISSING_SECTIONS`, `INVALID_OUTPUT`, `INVALID_PLAN`, `PROVIDER_NOT_CONFIGURED`, `PROVIDER_ERROR`. Adding a value to a response enum (an event category, a diff line kind) is additive. The hub requires a matching `Origin` (or `Live:AllowedOrigins`); a cookie WebSocket upgrade without one is refused with `FORBIDDEN`.

## 7. Pages and UI

| Route | Page | Key content / states |
| --- | --- | --- |
| /login, /register | Sign in / register | email + password, "Continue with GitHub"; error · loading |
| /projects | Projects list | search, phase/status filters, sort, paging, phase badge; empty · loading · error |
| /projects/new | New project wizard | 4 steps (Idea, Configuration, Git, Review & create), per-step validation, back/next |
| /projects/:id | Overview | phase timeline with current phase, status, version, pending gates with actions, next action button, activity feed, live updates; forbidden · not found |
| /projects/:id/refinement | Refinement | current draft, clarifying questions with answer fields, "Run another round", run status |
| /projects/:id/artifacts | Artifacts | card per type: latest version, status, integrity badge |
| /projects/:id/artifacts/:type | Artifact detail | rendered Markdown, version picker, history, diff view, edit (new draft), approve/reject with comment (owner) |
| /projects/:id/execution | Execution | step list with status, attempt, violations, commit link; start/pause/resume; live progress |
| /projects/:id/review | Review | latest score vs threshold, category breakdown, trend chart, drift level/areas, evidence list, decision |
| /projects/:id/runs | Agent runs | table: agent, model, tokens, duration, status; detail drawer |
| /projects/:id/settings | Settings | tabs: General, Members, Git (with Test), LLM, Notifications, Danger zone (delete, rollback) |
| /approvals/:token | Approval link | gate summary, confirm approve/reject; expired · used · forbidden |
| /notifications | Notifications | list with unread state, mark read |
| /account | Account | profile, linked GitHub, API tokens (create shows token once, revoke) |
| /admin, /admin/events, /admin/runs, /admin/dead-letters, /admin/users | Admin | filters, paging, retry, role change; forbidden for non-admins |

UI rules:
- Dark and light theme (system default).
- Destructive actions (delete, rollback, reject) need confirmation; delete needs the typed project name.
- Owner-only actions are hidden for other roles, and the API enforces them anyway.
- Long-running actions show a pending state until the live event arrives.

## 8. Non-functional requirements

- **Performance:** API p95 < 300 ms for reads with 50 projects and 1,000 artifacts locally (excluding agent calls). Live events reach browsers within 2 s. Agent calls stream nothing to the UI in v1; the run shows as running.
- **Reliability:** no lost work on pod restart. Jobs marked `running` by a dead worker are re-queued after a 5-minute lease expiry.
- **Security:**
  - HttpOnly, SameSite=Lax cookies, `Secure` whenever the request is HTTPS (`Auth:Cookie:SecurePolicy`, default `SameAsRequest`, because the local Kubernetes app is served over plain `http://localhost:8080`; set `Always` behind TLS).
  - Antiforgery for cookie-authenticated mutations: a double-submit `XSRF-TOKEN` cookie and `X-XSRF-TOKEN` header; failure is `ANTIFORGERY → 403`. Bearer-token requests are exempt.
  - Rate limiting on login (5/min/IP) and register.
  - Secrets encrypted at rest with ASP.NET Data Protection; keys persisted to the data volume.
  - No secret ever logged. Webhook bodies HMAC-signed. Approval tokens HMAC-signed and single-use.
- **Logging and observability:**
  - Structured JSON logs with project id, run id and job id.
  - OpenTelemetry traces and metrics to console in dev.
  - `/api/health/ready` checks the DB and that migrations are applied.
- **Accessibility:** WCAG 2.2 AA for core flows (keyboard navigation, focus states, labelled forms, colour contrast in both themes).
- **Browsers:** current Chrome, Edge, Firefox and Safari. Desktop-first, usable at 375 px width.
- **Testing:**
  - Every domain rule in §4 has unit tests, including the worked examples.
  - Every endpoint has integration tests against PostgreSQL (Testcontainers).
  - E2E uses the Fake LLM provider and the Local Git provider, so no network or keys are needed.

## 9. Local deployment

- **URL:** the app runs on Docker Desktop Kubernetes at `http://localhost:8080`, in namespace `aide`. The web container serves the SPA and proxies `/api`, including WebSocket upgrade for `/api/hubs/`.
- **Workloads:** `db` (PostgreSQL 17 StatefulSet), `api` (one replica; the API and background workers run in the same process in v1), `web`, and the `db-migrate` Job.
- **Volumes:** `aide-data` (5 Gi PVC) on the API, mounted at `/data`, for Git working copies, local Git repos and Data Protection keys.
- **Secrets** (`secrets.env`, git-ignored):
  - DB password and connection string
  - Data Protection application name
  - default LLM provider keys (Anthropic, OpenAI)
  - GitHub OAuth client id/secret
  - approval-link signing key
  - SMTP settings
- **Optional:** Ollama reached at `http://host.docker.internal:11434`. Mailpit can be added to the dev compose file for email testing.
- **Seed data (dev only):**
  - admin `admin@aide.local` with a generated password logged once at Warning (by the pod that creates the account; it is not logged again)
  - enabled by `Seed:Enabled` (true in Development and the local Kubernetes overlay, false elsewhere); the demo project is created once per database, so deleting it doesn't bring it back
  - one demo project in `refinement` using the Fake provider and Local Git
- **Dev loop:** API on 5080, Vite on 5173, compose DB on 5432.

## 10. Decisions

Each decision is an improvement over the old stack (`../AIDE-Platform-old-stack/`).

- **D1 — One backend, no broker.**
  - Old: seven NestJS services plus Redis/BullMQ and NATS/Kafka.
  - New: a modular monolith with a PostgreSQL job/outbox table and hosted workers (§4.9).
  - Why: transactional consistency between state and follow-up work, far less to run on a laptop.
- **D2 — One review decision table (§4.5).**
  - Old: the state machine, decision engine and failure handler used different thresholds (0.75 vs 0.85, "cycle > 2" vs max cycles). They also disagreed on whether low compliance triggers a soft gate or an automatic rollback.
  - New: low compliance escalates to a human; nothing rolls back automatically.
- **D3 — The escalation ("soft") gate is a real gate type** with recorded decisions. In the old stack it was only an event with no record.
- **D4 — `released` is set only after the release succeeds.** The old stack set it on entering the release phase, before the merge.
- **D5 — Rollback restores the phase deterministically.** It uses state transitions and `project_version_at_creation`. The old stack mixed project and artifact version numbers and left the phase to an unimplemented manager.
- **D6 — Structured agent output** for plans and reviews (JSON schema / tool use), rendered to Markdown for display. This replaces regex parsing of Markdown checklists.
- **D7 — Prompt templates ship in the repo, versioned, and every agent run records them** with model and token usage. The old stack loaded prompts from a `.github/skills/` folder that isn't in the repo.
- **D8 — Stack-agnostic review.**
  - Old: the checkers assumed the generated code was NestJS/Prisma/TypeScript (decorator scanning, kebab-case suffixes, `apps/` folders).
  - New: the review agent scores against the project's own spec, and the deterministic checks are language-independent (§4.7).
- **D9 — Project membership and roles.** Old: any authenticated user could see any project.
- **D10 — Persisted, encrypted project settings.** Old: Git token, LLM and notification settings were accepted but never saved.
- **D11 — Cookie auth + GitHub login for the browser; API tokens for the CLI.** This replaces JWTs held by the dashboard and fits the same-origin deployment.
- **D12 — Local Git provider** so the whole flow works offline on a laptop. The GitHub provider is kept.
- **D13 — Fake LLM provider** with deterministic outputs for demos, E2E and integration tests.
- **D14 — Single-use approval links with stored nonces.** Old: links were signed but reusable until expiry.
- **D15 — Token budget per project and cost estimates per run.** New safeguard.
- **D16 — Rejecting a spec gate re-runs the spec generator,** not idea refinement (an old-stack bug).
- **D17 — Idempotent step completion** keyed by step id (old: a counter that could double-count). One execution job per project at a time.
- **D18 — Secret-scan Guardian check** added. Exact path matching replaces the old suffix-based partial matching, which let wrong files pass.
- **D19 — Gate timeouts become reminders and an `overdue` flag.** The old stack named a 72-hour timeout but never defined its effect.
- **D20 — Live updates over SignalR at `/api/hubs/projects`.** Replaces Socket.IO, same-origin.
- **D21 — The CLI is rewritten as a .NET global tool** in the backend solution (`src/Aide.Cli`).

## 11. Open questions

- [x] GitLab provider: the old stack had a GitLab provider. Keep it out of v1 (proposed), or add it in a later milestone? — Ben
  - **Answered 2026-10-07:** out of v1. Keep the Git provider abstraction so it can be added later.
- [x] GitHub Copilot LLM adapter: the old stack had one. Drop it (proposed; Anthropic/OpenAI/Ollama cover the need), or re-add it later? — Ben
  - **Answered 2026-10-07:** dropped. Anthropic, OpenAI, Ollama and Fake only.
- [x] Should agent output stream token-by-token to the UI (nice for long spec/plan generation), or is "running" enough for v1 (proposed)? — Ben
  - **Answered 2026-10-07:** no streaming in v1; runs show as running until complete.
- [x] Default models per agent (proposed: Anthropic for spec/plan/review, a cheaper model for refinement). Confirm the model IDs at build time from provider docs. — Ben
  - **Answered 2026-10-07:** model IDs come from each provider's docs at build time and are configuration, not code. Anthropic for spec, plan, code generation and review; a cheaper model for refinement.
  - **Built 2026-10-08:** configuration is `Llm:DefaultProvider` and `Llm:Providers:<Anthropic|OpenAI|Ollama|Fake>:{ApiKey,BaseUrl,DefaultModel,Models:<Agent>}` in appsettings. Models confirmed from provider docs: Anthropic `claude-opus-5-5` (spec, plan, code generation, review) and `claude-sonnet-5-5` (refinement); OpenAI `gpt-6-astra` and `gpt-6.1-sol` (refinement); Ollama `qwen2.5-coder`; Fake `fake-1`. Cost estimates use `Llm:Pricing:<model>` (USD per million tokens); unpriced models report 0.

## 12. Milestones

1. **M1 Foundations:**
   - Auth: register/login, GitHub login, API tokens, first-user admin (FR-1–FR-4).
   - Projects, wizard, list, members, persisted settings, Git test (FR-5–FR-10).
   - App shell with theme and navigation.
2. **M2 Lifecycle core:**
   - State machine, gates, artifacts, invalidation and integrity (FR-11–FR-21).
   - Event log, outbox/job worker, live updates, activity feed (FR-46, FR-50).
   - All of §4.1–4.4 and §4.9 tested.
3. **M3 Agents:**
   - Provider abstraction (Anthropic, OpenAI, Ollama, Fake), agent runs, prompt templates, retries, budget (FR-22–FR-26).
   - Refinement, spec and plan generation (FR-27–FR-30).
4. **M4 Execution and Git:**
   - Step runner, Guardian, retries/blocking, pause/resume (FR-31–FR-33, FR-37).
   - Local and GitHub providers, commits, PR (FR-34–FR-36).
5. **M5 Review:** review cycle, scoring, drift, decision, optimisation loop, review page (FR-38–FR-41).
6. **M6 Release and rollback:** merge, tag, conflicts, rollback with limits (FR-42–FR-45).
7. **M7 Notifications:** in-app, email, Slack, Teams, webhook, approval links, per-channel retries, gate reminders (FR-47–FR-49, FR-15).
8. **M8 Admin and CLI:**
   - Admin pages and dead-letter retry (FR-51).
   - The `aide` CLI (FR-52).
   - Final E2E through the full lifecycle with Fake + Local providers.

## Revision notes

- 2026-10-10: synced with the build. Rule refinements (§4.1–4.9), schema additions (§5), extra routes and error codes (§6), cookie policy (§8), seed (§9) and the model configuration (§11) now match the code. Each is logged under Decisions in `PROGRESS.md`.
