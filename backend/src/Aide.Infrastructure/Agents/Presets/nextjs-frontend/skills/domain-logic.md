Rules for the project's core business rules and algorithms (filtering, sorting, ranking, scoring, totals, scheduling,
eligibility, validation, state machines) as pure, tested TypeScript in `lib/<domain>/`. These are the rules most likely
to be subtly wrong and least likely to be caught by clicking around.

### Where and what
- `lib/<domain>/` has one file per concept (`pricing.ts`, `ranking.ts`, `schedule.ts`), each with a `*.test.ts` beside it.
- Exported input and output types at the top of the file or in `lib/<domain>/types.ts`.
- Only domain types go in and out: never raw upstream payloads, `Request`/`Response` or framework types. `lib/api/`
  validates and maps upstream data before it reaches these functions.

### Rules
1. **Pure.** No `fetch`, `fs`, env vars, `window`, `localStorage`, `Date.now()`, `Math.random()` or globals. Time,
   locale, seeds and config are parameters.
2. **Deterministic.** Same input, same output, including ordering. Explicit stable tie-breakers (timestamp, then id).
3. **Exact arithmetic where it matters.** Integer minor units for money; compare ratios by cross-multiplication.
   Format for display only in the UI.
4. **One source of truth for constants.** Limits and constants are exported from here and imported everywhere else.
5. **Total functions.** Empty input, a single item, maximum sizes and invalid values are handled explicitly, by a typed
   error or a discriminated result. Never return `undefined` silently.
6. **Small public surface.** Export what the API and UI need; keep helpers private.

### Tests (written with the code, in the same step)
1. Every rule and example in the specification becomes a test case, citing the requirement ID in the `describe` name.
2. Edge cases: empty, single, ties, limits (min and max), invalid input, large input.
3. Determinism: shuffle the input 20 times (seeded) and check the output is identical.

### Ambiguity
If the specification doesn't define a case, choose the most consistent behaviour, encode it in a test named
`"assumption: ..."`, and report it in the step notes.
