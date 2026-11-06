---
title: Error codes
parent: Reference
nav_order: 5
---

# Error codes

API errors are RFC 9457 problem details with an `errorCode` extension. The backend defines the codes in [`ErrorCodes.cs`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/backend/src/Aide.Domain/Common/ErrorCodes.cs). The frontend maps them to user copy in one place, `frontend/src/lib/errors.ts`. Each spec lists the codes it introduced with their status and copy.

## Common codes

| Code | Status | Meaning |
| --- | --- | --- |
| `VALIDATION` | 400 | A field is invalid. Field errors are in the response |
| `UNAUTHENTICATED` | 401 | No valid session or token |
| `FORBIDDEN` | 403 | You lack permission |
| `NOT_FOUND` | 404 | Missing, or hidden from you |
| `CONFLICT` | 409 | Someone changed the resource first. Reload and retry |
| `ANTIFORGERY` | 403 | A browser mutation without a valid antiforgery token |
| `RATE_LIMITED` | 429 | Too many register or login attempts. Wait for `Retry-After` |
| `UPSTREAM_ERROR` | 502 | An external service failed or was unreachable |

## Families

| Family | Examples |
| --- | --- |
| Accounts and members | `EMAIL_TAKEN`, `INVALID_CREDENTIALS`, `LAST_OWNER` |
| Lifecycle, gates, artifacts | `PHASE_PRECONDITION_FAILED`, `GATE_PENDING`, `ARTIFACT_LOCKED`, `RESTORE_NOT_ALLOWED`, `BUDGET_EXCEEDED` |
| Review, release, rollback | `REVIEW_IN_PROGRESS`, `MERGE_CONFLICT`, `ROLLBACK_BLOCKED`, `TAG_EXISTS`, `VERSION_NOT_GREATER` |
| Credentials and Git hosts | `CREDENTIAL_INVALID`, `GITHUB_NOT_CONNECTED`, `GITHUB_APP_NOT_INSTALLED`, `GITHUB_RATE_LIMITED` |
| Deployments | `PLATFORM_GIT_NOT_CONNECTED`, `PLATFORM_PLAN_LIMIT`, `DEPLOYMENT_NOT_CONFIGURED`, `PLATFORM_ERROR` |
| Issue trackers | `TRACKER_NOT_CONNECTED`, `TRACKER_QUERY_INVALID`, `SOURCE_ITEMS_TOO_LARGE` |
| Stacks and analysis | `STACK_INCOMPATIBLE`, `STACK_LOCKED`, `STACK_INFER_NO_CODE`, `STACK_PROFILE_INCOMPLETE`, `DEPLOYMENT_GROUP_INVALID` |
| Agent runs (recorded on the run) | `MISSING_INPUT`, `INVALID_PLAN`, `MISSING_SECTIONS`, `MISSING_REQUIREMENT_IDS` |

The CLI maps some of these to exit codes: see the [CLI guide](../guides/cli.md).

## Where each code is specified

| Version | Error table |
| --- | --- |
| 0.1.0 to 0.5.0 (v1 to v5) | Section 6, API, in [v1](../v1/SPEC.md), [v2](../v2/SPEC.md), [v3](../v3/SPEC.md), [v4](../v4/SPEC.md) and [v5](../v5/SPEC.md) |
| 0.6.0 (v6) | No new codes. Copy that names the product now says Tevrin |
