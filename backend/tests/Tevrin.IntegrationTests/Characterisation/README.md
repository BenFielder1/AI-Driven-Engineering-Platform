# SDLC characterisation snapshots (0.7.0, FR-260, FR-261, §4.58)

These tests are the safety net for the 0.7.0 engine refactor. Each scenario in `SdlcCharacterisationTests` drives one
project through the SDLC end to end, the way a user and the worker would: the public HTTP API for every decision and
`AgentJobs` for the queued jobs, on the Fake LLM, Local Git (GitHub as `FakeGitHubApi` over a bare repository), Fake
deploy and Fake tracker providers. It then records a **snapshot** of what the project left in the database and compares
it with the committed file `Snapshots/<scenario>.json`.

## The freeze rule (FR-261)

The snapshots are committed in M54. **No later milestone may change a committed snapshot.** A refactor is correct only
if every scenario still produces exactly the same bytes. CI checks this with `scripts/ci/check-snapshots-frozen.sh`, which compares each snapshot byte for
byte with the base commit's copy (at its pre-rename path under `backend/tests/Aide.IntegrationTests/`).

If a scenario's snapshot differs, the refactor changed SDLC behaviour: fix the code, not the snapshot. The test
fails with a unified-style diff of the first differing lines and writes the actual snapshot next to the results (see
below) so you can compare the whole file.

## Never edit a snapshot by hand

Snapshots are generated. Don't edit, reformat or "fix up" a file under `Snapshots/`.

## Producing or regenerating the snapshots

The tests need PostgreSQL in Docker (Testcontainers), so they run on CI, not on the development machine (CLAUDE.md,
"Commit gate").

1. A scenario whose `Snapshots/<scenario>.json` is missing **fails** with `Snapshot missing: written to …` and writes
   its actual snapshot to `backend/TestResults/characterisation/<scenario>.json`. A missing snapshot never passes, so
   CI can't go green without committed snapshots.
2. CI uploads `backend/TestResults` as the `backend-integration-<shard>` artifact. Download it
   (`gh run download <run id> -n backend-integration-<shard>`), review the files, and copy
   `characterisation/*.json` into `Snapshots/`. Commit them in M54.
3. To deliberately rewrite a snapshot (only ever in M54, or with a spec decision that records the behaviour change),
   run the tests with `UPDATE_SNAPSHOTS=1`. Every scenario then writes its snapshot to `backend/TestResults/characterisation/`
   and fails, and you copy the files as in step 2.

A difference from the committed snapshot also writes the actual file to `backend/TestResults/characterisation/`.

## What a snapshot holds (§4.58)

Read with SQL from the project's rows, so it depends on table and column names and stored values only, not on the C#
types the refactor moves:

- `project`: phase, status, checkpoint, review cycle, extra review cycles, approval mode, released and development version
- `transitions`: from/to phase, checkpoint, reason, actor
- `gates`: type, status, decision, target checkpoint, whether a comment was given, gated artifact, reviewer
- `artifacts`: type, version, parent version, status, content hash, checkpoint at creation, restored-from version,
  author (user or agent run)
- `jobs` (not the `notification` outbox, which tests never run): type, the payload's `trigger`, final status, attempts
- `agentRuns`: agent, prompt version, status, phase
- `events`: type, actor and the payload, without timing, duration, token, cost, ETag and lease fields

Normalisation:

- Ids become `<kind>#n` (`artifact#3`, `job#7`, …), numbered per kind in creation order; ids found only inside strings
  become `id#n` in first-seen order (or their kind, e.g. `step#2`, `release_version#1`). Users are `user:owner`,
  `user:contributor`, `user:viewer`, `user:outsider`, and their emails `<role>@example.test`. A job and its agent run
  share an id, so the run shows the job's label.
- Git commit SHAs become `sha#n` in first-seen order; short forms (e.g. in Fake deployment URLs) `sha#n:<length>`;
  other short hex tokens `hex#n`.
- Timestamps and dates inside strings become `<timestamp>` and `<date>`; the project slug `<slug>`; test folders
  `<workspaces>`, `<remote>`, `<remote-url>/`, `<import-root>`.
- The job trigger `phase_entered` (0.6.1) is shown as `stage_entered`, in job rows and in any payload `trigger` field,
  because FR-273 (M56) writes the generic name, with the same meaning, in new payloads.
- Ids used as object keys are labelled in an order that never depends on the raw id: members are visited by blanked key,
  then blanked value.
- A list made only of id labels (`<kind>#n` of any kind but `sha` and `hex`, or `user:<role>`), e.g. `resetStepIds`,
  `invalidatedArtifactIds` or recipients, is a set: it is sorted by kind, then number (users by role). Such lists are
  often read with no `ORDER BY`. Lists of commits (`sha#n`), of objects or of other values keep their order. This rule
  was added in M55 (D-0.7.0-22) after CI run 44125867549 returned `resetStepIds` in another order on a namespace-only
  commit. It was a one-time normalisation change: the matching arrays in the committed snapshots were re-sorted and
  nothing else changed. Two arrays in `new_project_request_approved_mid_execution.json` moved;
  `rollback_to_checkpoint_2.json` has one more such array that was already sorted. This is the only snapshot edit
  allowed in 0.7.0. An id seen first inside such a set is still numbered in the order the set was read; that is stable
  only while the id isn't referenced anywhere later in the snapshot.
- Content hashes stay. When an artifact's content itself holds a run-specific value (an id, a SHA, a date), its hash is
  taken over the normalised content and shown as `normalised:<sha-256>`.
- Each list is in creation order (`created_at`). Rows written in one transaction share `created_at`, and v7 ids are
  random within a millisecond, so ties are ordered by the row's content with every run-specific value blanked.
  **This is part of what is frozen.** Today every writer stamps the rows of one request or job with a single
  `TimeProvider` timestamp. Later milestones must keep that: rows of one unit of work share `created_at`, and later units
  get later times. If a milestone can't, it may change how ties are ordered here and update this rationale, but it may
  never change a snapshot.
- JSON with sorted keys, 2-space indent, LF line endings and a trailing newline.

`FR260_A_scenario_recorded_twice_gives_byte_identical_snapshots` runs the strict scenario on two projects and requires
identical bytes, so a value that leaks through normalisation fails at once.

## Scenarios

| Snapshot | Scenario |
| --- | --- |
| `strict_happy_path` | (1) strict: refinement → … → release/released, gates `spec_approval`, `plan_approval`, `pre_release` |
| `advisory_happy_path` | (2) advisory: `spec_approval` and `pre_release` only |
| `autonomous_happy_path` | (3) autonomous: `pre_release` only |
| `gate_rejected_then_approved` | (4) `spec_approval` rejected with a comment, the spec generator re-run with it, the new spec approved |
| `draft_rejected` | (5) the spec draft rejected, re-generated, then approved |
| `two_review_cycles_then_release_eligible` | (6) the default Fake review: cycle 1 review_required, the optimisation plan run, cycle 2 release-eligible |
| `escalation_continue` | (7) escalation `continue`, the optimisation plan run, cycle 2 release-eligible |
| `escalation_rollback` | (7b) escalation `rollback` to checkpoint 3 |
| `escalation_accept` | (7c) escalation `accept`, then released |
| `new_project_request_approved_mid_execution` | (8) after step 1, a new project request approved: cascade and fallback |
| `rollback_to_checkpoint_2` | (9) rollback from review to checkpoint 2 |
| `release_refused_awaiting_merge_then_merged` | (10) GitHub branch protection refuses the merge, `awaiting_merge`, merged on GitHub, released (execution seeded as in `MergeFlowTests`) |
| `merge_abandoned` | (10b) manual merge strategy, `awaiting_merge`, the PR closed: `PR_CLOSED`, back to `releasing` (execution seeded) |
| `new_version_after_release` | (11) released, then 0.2.0 started and refined |
| `stack_inference_analysis_then_refinement` | (12) a Local import with "infer": analysis, stack profile approved, refinement |
| `build_check_failed_once_then_fixed` | (13) Fake deploy: attempt 0 fails on the Fake build marker, a fix is planned and run, attempt 1 passes (execution seeded as in `BuildCheckTests`; the marker is removed on the branch before the fix step) |
| `corrupted_artifact_regenerated` | (14) the spec draft altered behind the API, read as corrupted, regenerated, then approved |
