<!-- Adapted from stack-packs stack-core@1.0.0 skills/stack-api-contract/SKILL.md, snapshot 2026-10-25 -->

# The API contract between layers

The backend owns `<backend path>/openapi/api.json` and commits it. The frontend's typed client is generated from that file. Agents in the two layers coordinate through this file, not through each other: each backend pack says **how the file is produced** (and its shape), and each frontend pack says **how it's consumed**. Nobody runs the generators during a step: the owning agent hand-writes the change in the generator's shape, and CI regenerates and diffs it.

## Conventions (every pack follows these)

- **Path:** `<backend path>/openapi/api.json` (`backend/openapi/api.json` by default; the composition summary gives the path), OpenAPI 3.x JSON, committed.
- **Operation IDs:** stable and readable (`getOrder`, `lockOrder`). They become client method names, so renaming one is a breaking change.
- **Prefix:** every route is under `/api`.
- **Schemas:** request and response DTOs are named schemas. Nullability and `required` match the code. Enums are strings.
- **Errors:** RFC 9457 problem details (`application/problem+json`), shaped `{ type, title, status, detail, errorCode, errors? }`:
  - `errorCode` is UPPER_SNAKE and comes from the spec's error table.
  - `errors` is the validation map `field → [messages]`.
  - Every endpoint documents its error responses.
- **Auth:** documented with security schemes (cookie or bearer), matching what the backend actually does.
- **Hand-written changes** keep the file's existing `openapi` version, key order, indentation and schema naming, so CI's regenerated file is identical.

## Contract-first (to let frontend and backend work in parallel)

1. A backend step adds the DTOs and endpoint signatures, with handlers returning **501**, and declares `openapi/api.json` with the new paths and schemas.
2. A later frontend step updates the generated client to match and builds against mocks (MSW or the framework's equivalent) typed with the generated types, including the spec's error cases.
3. A backend step implements the handlers. The frontend switches to the real API and updates its tests.

Any step that adds or changes an endpoint must declare `openapi/api.json` (or a later step must), and the contract step comes before the frontend's generated-client steps.

## Breaking-change rules

These are **not allowed** without a spec decision:
- removing or renaming an endpoint, operation ID, parameter or response field
- making an optional request field required
- changing a type or format
- removing an enum value that clients send
- changing documented status or error codes

These are **allowed:** new endpoints, new optional request fields, new response fields, and deprecations.

**Check:** compare your change against the committed file: paths and operation IDs, required request fields, schema properties and types, enum values, and documented responses. Say in the step summary whether the change is additive or breaking (and which spec decision allows a break). CI may also run `oasdiff breaking` against the base branch's file.

## Freshness checks (gate and CI)

The ci-engineer adds both checks to CI, using the packs' commands:
```bash
<backend pack's export command> && git diff --exit-code <backend path>/openapi/api.json
<frontend pack's gen:api command> && git diff --exit-code <frontend client path>
```
A failed check is a review finding: fix the hand-written file to match what the generator produces.

## When there's no separate backend

- **`fe-nextjs` full-stack:** server actions and route handlers are internal, so no OpenAPI file is needed. If the spec exposes a public API, document it from the route handler schemas (Zod) and follow these rules for it.
- **`data-supabase` BaaS mode:** the "contract" is the generated database types plus RLS policies. That pack covers it.

## Checklist

- [ ] The contract is updated with the endpoints, and operation IDs are stable.
- [ ] The change is additive, or a spec decision allows the break, and the step says which.
- [ ] The client matches the contract, the typecheck is clean, and mocks are typed.
- [ ] Error codes match the spec on both sides, and CI's freshness checks pass.
