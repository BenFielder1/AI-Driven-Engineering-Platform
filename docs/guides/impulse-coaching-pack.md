---
title: Impulse coaching pack
parent: Guides
nav_order: 12
---

# Impulse coaching pack

The Impulse coaching pack (`coach`) is the second pack on the [pipeline engine](pipelines-and-packs.md). It turns an athlete's session log into a reviewed training plan and a message from the coach. It adds no engine code: everything here is a definition, schemas, prompts and one guard.

## The pipeline

The pipeline is `impulse-coach`. The engine controls it, and it has four stages.

| Stage | Does | Reads | Writes |
| --- | --- | --- | --- |
| `analyse` | Agent `performance-analyst` summarises the training. It waits until a `session_log` exists | `session_log`, `athlete_profile` | `performance` |
| `plan` | Agent `programme-planner` drafts the next weeks. The guard `coach.velocity-load-rules` checks the draft | `performance`, `athlete_profile` | `plan_draft` |
| `review` | The `plan_approval` gate. A person approves the plan | `plan_draft` | none |
| `write` | Agent `coach-voice` writes the message | `plan_draft` | `coach_message` |

**Guard and retries.** If the guard finds a broken rule, the planner runs again with the findings as feedback. It gets up to 3 attempts. If the third still fails, the run raises one `escalation` gate and `write` doesn't run.

**Approval modes.** `strict` (the default) and `advisory` both use the `plan_approval` gate.

**Triggers.** The pipeline starts three ways:

- manually, from the UI or the API;
- by a signed webhook whose body is the `session_log`;
- by a schedule, for example a weekly plan.

A webhook call or schedule fire while a run is active is skipped.

## Artifacts and schemas

All artifacts are JSON. The schemas are in `backend/src/Tevrin.Pack.Coach/Schemas/`.

| Artifact | Kind | Fields (required in bold) |
| --- | --- | --- |
| `athlete_profile` | Project scope, needs approval | **`name`**, **`baselineWeeklyKm`**, `volumeUnit` (`km` or `minutes`), `raceDate`, `goal`, `velocity` |
| `session_log` | Input, locks automatically | **`sessions[]`**: each has **`date`**, **`type`**, and optionally `intensity`, `distanceKm`, `durationMin`, `rpe` (1 to 10), `notes` |
| `performance` | Locks automatically | **`summary`**, **`lastWeekVolume`**, `fatigue` (`low`, `moderate`, `high`), `trends[]`, `risks[]` |
| `plan_draft` | Needs approval | **`weeks[]`**: each has **`week`** and **`sessions[]`** (**`date`**, **`type`**, **`intensity`**, and optionally `distanceKm`, `durationMin`, `notes`). Also `rationale` |
| `coach_message` | Locks automatically | **`subject`**, **`body`** |

`intensity` is one of `easy`, `moderate`, `hard` or `race`.

**The athlete profile is shared.** It has one version history per project, not per run. Create it once, lock it, and every run pins the newest locked version when it starts. A newer version doesn't change a run in progress. Without a locked profile a run can't start: 409 `SOURCE_ARTIFACT_MISSING`.

## Velocity load rules

The guard checks each `plan_draft` against the profile and the last 4 weeks of `session_log`. It returns one finding per broken rule, written `rule: message`. Week volume is the sum of `distanceKm`, or of `durationMin` when the profile sets `volumeUnit: minutes`.

| Rule | Default | Example finding |
| --- | --- | --- |
| R1 weekly increase | A week is at most 110% of the week before. Week 1 compares with the last actual week, or `baselineWeeklyKm` | `R1: week 2 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)` |
| R2 recovery | At least 48 h between the starts of two `hard` or `race` sessions | `R2: hard sessions on 2026-12-01 and 2026-12-02 are 24 h apart (min 48 h)` |
| R3 hard count | At most 3 `hard` sessions per plan week | `R3: week 3 has 4 hard sessions (max 3)` |
| R4 taper | With a `raceDate`: a week ending 8 to 14 days before it is at most 70% of the previous 4-week peak. A week ending within 7 days is at most 50% | `R4: week 4 volume 48.0 km is 80% of peak 60.0 km within 14 days of the race (max 70%)` |
| R5 rest | At least 1 day with no session in every plan week | `R5: week 1 has no rest day` |

### Override the defaults

Set any of these keys in `athlete_profile.velocity`. Missing keys keep the default. Other keys are rejected.

| Key | Rule | Meaning |
| --- | --- | --- |
| `maxWeeklyIncreasePct` | R1 | Largest weekly volume increase, in percent |
| `minHoursBetweenHard` | R2 | Hours between hard session starts |
| `maxHardPerWeek` | R3 | Hard sessions per plan week |
| `taperPctWithin14Days` | R4 | Weekly cap as a percent of peak, 8 to 14 days out |
| `taperPctWithin7Days` | R4 | Weekly cap as a percent of peak, within 7 days |
| `minRestDaysPerWeek` | R5 | Days with no session per plan week |

```json
{
  "name": "Sam Rivera",
  "baselineWeeklyKm": 50,
  "volumeUnit": "km",
  "raceDate": "2027-04-18",
  "velocity": { "maxWeeklyIncreasePct": 8, "maxHardPerWeek": 2 }
}
```

Set `"volumeUnit": "minutes"` to count `durationMin` instead of `distanceKm`. A plan session that lacks the volume field for the athlete's unit is an R1 finding.

## Start a run

### From the UI

Open the project's Pipeline tab and select **Start run**. The button shows for Contributors and up, and a Viewer doesn't see it.

1. Pick the pipeline, here `impulse-coach`, and an approval mode if the definition offers a choice.
2. Fill in each input the pipeline declares. The editor is built from the input's schema. `session_log` has no flat form, so it opens as a JSON editor that lists the schema errors inline. You can also choose **Upload a JSON file**.
3. Leave an input blank to add it later. The run then waits on input.
4. Select **Start run**. A valid submit lands on the new run.

For a run that is waiting on input, select **Submit input** on the Pipeline tab, pick the input, and enter it in the same editor. The run continues once the input passes its schema.

### From the API

The examples assume `$TEVRIN` is your base URL and `$PID` is the project id. Add your sign-in credentials to each call.

1. Add the athlete profile (Contributor), then lock version 1 (Owner):

```bash
curl -X POST "$TEVRIN/api/projects/$PID/pipelines/impulse-coach/artifacts/athlete_profile/versions" \
  -H 'Content-Type: application/json' \
  -d '{ "content": { "name": "Sam Rivera", "baselineWeeklyKm": 50 } }'

curl -X POST "$TEVRIN/api/projects/$PID/pipelines/impulse-coach/artifacts/athlete_profile/versions/1/lock"
```

2. Start the run with a session log (Contributor):

```bash
curl -X POST "$TEVRIN/api/projects/$PID/pipeline-runs" \
  -H 'Content-Type: application/json' \
  -d '{
    "pipelineId": "impulse-coach",
    "inputs": { "session_log": { "sessions": [
      { "date": "2026-11-23", "type": "tempo", "intensity": "hard", "distanceKm": 12, "rpe": 8 },
      { "date": "2026-11-25", "type": "easy run", "intensity": "easy", "distanceKm": 8 }
    ] } }
  }'
```

Errors: 409 `RUN_ALREADY_ACTIVE` (a run is still going), 409 `SOURCE_ARTIFACT_MISSING` (no locked profile), 400 `INPUT_INVALID` (the log fails its schema, with the failing paths).

### From a webhook

A webhook lets another system send each session log as it happens.

1. As an Owner, create the webhook. The response has `id`, `url` and `secret`. **The secret is shown once**, so store it:

```bash
curl -X POST "$TEVRIN/api/projects/$PID/pipeline-webhooks" \
  -H 'Content-Type: application/json' \
  -d '{ "pipelineId": "impulse-coach" }'
```

2. Sign the exact raw body. The signature header is `X-Tevrin-Signature: t=<unix seconds>,v1=<hex>`, where the hex is `HMAC-SHA256(secret, "<t>.<raw body>")`. Send a unique `X-Tevrin-Delivery: <uuid>` header too.

```bash
SECRET='whsec_test'
T=1764590400                      # use $(date +%s) for a real call
BODY='{"a":1}'                    # the exact bytes you send
SIG=$(printf '%s' "$T.$BODY" | openssl dgst -sha256 -hmac "$SECRET" | awk '{print $NF}')

curl -X POST "$TEVRIN/api/hooks/pipelines/$WEBHOOK_ID" \
  -H 'Content-Type: application/json' \
  -H "X-Tevrin-Signature: t=$T,v1=$SIG" \
  -H "X-Tevrin-Delivery: $(uuidgen)" \
  --data-binary "$BODY"
```

**Worked example.** Secret `whsec_test`, `t` = 1764590400, body `{"a":1}`. The signed text is `1764590400.{"a":1}`, and the signature is:

```
v1=54fe8ee64e5499df7c6cee87b1b22c2800b3fbfed80e088b818a25b927713fb0
```

Rules:

- The timestamp must be within 300 seconds of the server clock, so the fixed `t` above only works as a test vector.
- Don't reformat the body after signing. The signature covers the raw bytes.
- The body is at most 256 KB. For this pipeline, it must be a valid `session_log`.
- Rotating the secret keeps the old one valid for 24 hours.

| Response | Meaning |
| --- | --- |
| 202 `{ runId }` | A run started |
| 401 `WEBHOOK_SIGNATURE_INVALID` | Bad signature, old timestamp, or unknown webhook |
| 409 `WEBHOOK_REPLAY` | That delivery id was seen in the last 24 hours |
| 400 `INPUT_INVALID` | The body fails the `session_log` schema |
| 400 `VALIDATION` | The `X-Tevrin-Delivery` header is missing or isn't a UUID |
| 413 `VALIDATION` | The body is over 256 KB |
| 409 `RUN_ALREADY_ACTIVE` | A run is still in progress (the webhook trigger uses `on_active: skip`) |

## Schedule a weekly plan

As an Owner, create a schedule. The cron has 5 fields and is read in the given IANA timezone:

```bash
curl -X POST "$TEVRIN/api/projects/$PID/pipeline-schedules" \
  -H 'Content-Type: application/json' \
  -d '{ "pipelineId": "impulse-coach", "cron": "0 6 * * 1", "timezone": "Europe/London" }'
```

This starts a run every Monday at 06:00 London time. If a run is still active at the due time, the fire is skipped. The skip is recorded as a `SCHEDULE_FIRED` event with outcome `skipped`. After downtime, missed fires run once.

An invalid cron or timezone gives 400 `SCHEDULE_INVALID`. You can list, pause and delete schedules at `/api/projects/{id}/pipeline-schedules`.
