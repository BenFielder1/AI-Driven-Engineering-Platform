---
title: Pipeline definitions
parent: Operations
nav_order: 6
---

# Pipeline definitions

How the 0.7.0 pipeline engine's definitions behave in a running deployment. For the format, see [Pipelines and packs](../guides/pipelines-and-packs.md).

## Pinning

Each pipeline run stores the hash of the definition it started with. The first time a hash is used, the engine saves its canonical content in the `pipeline_definitions` table, and never updates it. A deploy that changes `sdlc.pipeline.yaml` gives new runs the new hash. Runs already in progress keep the old one.

If an old run's stored definition names a guard, handler or tool that the new build no longer registers, moves on that run fail with 409 `DEFINITION_UNAVAILABLE`, and the missing names are logged. Reads and the Pipeline tab keep working. To fix it, redeploy a build that registers the names again.

If a release or merge-poll job dead-letters with DEFINITION_UNAVAILABLE, redeploy a build that registers the missing names, then retry the dead letter.

## The readiness check

`/api/health/ready` includes a `pipeline-definitions` check. It fails when:

- any registered definition is invalid, or
- the pipeline registry couldn't be built.

On failure the response is unhealthy, so Railway and Kubernetes keep traffic off that instance. This is on purpose: without a valid SDLC definition, every save that touches a project or adds a transition, gate, agent run or artifact fails, including saves from the job worker.

**If readiness fails:**

1. Read the API logs. Each problem is an Error line: `Pipeline definition <pack>/<source> (pipeline <id>) is invalid: <CODE> at <path>: <detail>`. A build failure logs `The pipeline registry failed to build`.
2. Look up the code in the [validator table](../guides/pipelines-and-packs.md#validator-codes).
3. Fix the definition or the pack registration, and redeploy. A definition error ships with the build, so a rollback to the previous release also fixes it.

## Run backfill at start-up

The 0.7.0 migration only adds tables and columns. A host service, `PipelineRunBackfillService`, then runs at start-up, before the job worker. It is skipped while migrations are pending.

- It stores the SDLC definition and creates one pipeline run per release version that lacks one. The current version's run takes the project's phase, status and checkpoint. Earlier versions get a `completed` run.
- It links existing transitions, gates, agent runs and artifacts to their runs.
- It is idempotent and safe on several replicas. Rows written by an older replica during an overlapping deploy are linked on the next start.
- It logs `Pipeline run backfill: N run(s) created, M row(s) linked`. If it fails it logs a warning and runs again on the next start.

Projects keep `current_phase`, `status` and `checkpoint` in step with the run, written in the same transaction, so a deploy rollback to 0.6.1 still works. Runs started before 0.7.0 have no stage timings for earlier stages.
