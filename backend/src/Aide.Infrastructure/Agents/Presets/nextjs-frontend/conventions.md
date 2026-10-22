This project uses the `nextjs-frontend` preset. These conventions apply to every step, whichever specialist writes it.
Where the technical specification is more specific, the specification wins.

### Stack
- Next.js (App Router) with TypeScript in strict mode (`strict`, `noUncheckedIndexedAccess`) and the `@/*` import alias.
- Tailwind CSS for all styling. No other CSS framework or component library unless the specification lists it.
- No database of its own. Data comes from the external APIs in the specification's Data Sources & Consumed APIs
  section, or from mocks/fixtures where the specification says so.
- Zod for every boundary: route params, query, body, forms, upstream API responses and environment parsing.
- Vitest + Testing Library (+ MSW for mocked upstreams) for unit and component tests, Playwright for end-to-end tests.
- GitHub Actions for CI, Vercel for hosting. Node is pinned in `.nvmrc` and `package.json` `engines`.

### Layout
```
app/                    pages and layouts (ui-engineer); app/api/** backend-for-frontend routes (api-engineer)
components/ui/          shared primitives (Button, Input, Dialog, Tabs, Toast, EmptyState)
components/<feature>/   feature components
lib/<domain>/           pure business rules + *.test.ts (domain-logic-engineer)
lib/api/                upstream client (server-only), Zod response schemas, errors.ts (error-code -> status map)
lib/ui/                 error-messages.ts (error code -> copy), formatting helpers
lib/types.ts            camelCase domain types (inferred from Zod schemas) and upstream -> domain mappers
mocks/                  MSW handlers and fixtures for tests (and the optional mock mode)
e2e/                    Playwright tests
```

### Rules every step follows
1. **Secrets stay on the server.** Upstream API keys and tokens are read from `process.env` only in `app/api/**`
   route handlers or modules that `import 'server-only'`. They never appear in a `"use client"` file, a component or a
   `NEXT_PUBLIC_` variable. Only genuinely public values use `NEXT_PUBLIC_`.
2. **Upstream data is untrusted.** Every upstream response is parsed with a Zod schema; an unexpected shape becomes a
   mapped error, not a crash. Upstream calls have a timeout.
3. **BFF routes are not open proxies.** The upstream host and paths are fixed in code, never taken from the request.
4. **Business rules live in `lib/<domain>/`** as pure, deterministic functions. Routes and components call them.
5. **One error vocabulary.** Error codes come from the specification. `lib/api/errors.ts` maps code -> HTTP status and
   upstream errors -> codes; `lib/ui/error-messages.ts` maps code -> user copy. API errors have the shape
   `{ "error": { "code": "...", "message": "...", "details": ... } }`.
6. **Rendering follows the specification's Routes & Rendering Strategy**: static, dynamic or revalidated per route.
   User-specific data is never statically cached.
7. **Server Components first.** Client components are leaf-level and only for interactivity and forms.
8. **Accessible and mobile-first.** Designed at 360px, tap targets at least 44px, a label on every control, WCAG AA.
9. **Tests sit next to the code** (`*.test.ts(x)`), name the requirement IDs they cover, and never call a real external
   API (use MSW handlers from `mocks/`).
10. **No secrets in the repository.** Configuration comes from environment variables listed in `.env.example`.

### Scripts (package.json)
`dev`, `build`, `start`, `lint`, `typecheck` (`tsc --noEmit`), `test` (`vitest run`), `test:e2e` (`playwright test`).
CI and reviewers rely on these names.
