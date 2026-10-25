<!-- Adapted from stack-packs be-express@1.0.0 skills/be-express/SKILL.md, snapshot 2026-10-25 -->

# Node.js + Express (TypeScript)

## Detect

Read the `express` major (v5 forwards rejected promises to error handlers; v4 needs async wrappers), the TS setup (`tsx`, `tsc`, `tsup`), validation, logging, the ORM and the test runner. Mirror what exists.

## Scaffold

Write under the backend path:
- **`package.json`:** `"type": "module"`, `engines.node`, and the scripts `dev` (`tsx watch src/server.ts`), `build` (`tsc -p tsconfig.build.json`), `start` (`node dist/server.js`), `typecheck` (`tsc --noEmit`), `lint`, `test` (`vitest run`), `openapi:export` (`tsx scripts/export-openapi.ts`).
  - **Dependencies:** `express`, `zod`, `@asteasolutions/zod-to-openapi`, `helmet`, `pino`, `pino-http`, `express-rate-limit`, `cookie-parser`; `cors` only for cross-origin.
  - **Dev dependencies:** `typescript`, `tsx`, `@types/express`, `vitest`, `supertest`, `@types/supertest`, `@testcontainers/postgresql`, `eslint`, `typescript-eslint`, `prettier`.
  - Use current stable versions you are confident exist, and pin Node in `.nvmrc` (or `.node-version`). There's no lockfile until the first CI run.
- **`tsconfig.json`** (`strict`) and `tsconfig.build.json` (excludes tests), `eslint.config.js`, `vitest.config.ts`.
- **Layout:**
  ```
  src/
    app.ts        # createApp(deps) → express app (no listen)
    server.ts     # reads config, creates deps, listens on PORT
    features/<feature>/ (router.ts, schemas.ts, service.ts, *.test.ts)
    domain/       # pure rules
    lib/          # errors.ts, auth.ts, config.ts, logger.ts
    openapi/registry.ts
  scripts/export-openapi.ts
  test/           # integration tests and global setup
  openapi/api.json
  ```

## Conventions

- **`createApp(deps)`** takes its dependencies (DB client, clock) so tests can inject them. No module-level singletons except config.
- **Routers** per feature, mounted at `/api/<feature>`. Handlers validate input with Zod (a small `validate()` middleware), call a service and return DTOs. Never return ORM rows directly.
- **Middleware:** `helmet()`, `express.json({ limit })`, `pino-http`, a rate limiter on auth routes, `app.disable('x-powered-by')`.
- **Domain rules** are pure functions, unit-tested.
- **Async:** Express 5 propagates async errors to the error middleware. Never swallow promises.

## Errors

- `class AppError extends Error { status; errorCode }`, with codes in `lib/errors.ts` mirroring the spec's error table.
- The final error middleware maps:
  - `ZodError` to 400 with `errors`
  - `AppError` to its status
  - unknown errors to 500 (logged, generic body)
- Every error response is `application/problem+json` with `errorCode`.

## Auth

Follow the spec:
- **Same-origin:** session cookies (HttpOnly, Secure, SameSite=Lax) with a store in the database, and password hashing with `argon2`.
- **Cross-origin:** bearer JWT (`jose`).

Middleware `requireAuth` and `requirePermission(…)`, plus ownership checks in services. `cors` with the origin list from `CORS_ALLOWED_ORIGINS`, only for the cross-origin model.

## Contract export

- Register the Zod schemas and routes in `openapi/registry.ts` (`extendZodWithOpenApi(z)`, `registry.registerPath({ method, path, operationId, request, responses })`). The same schemas validate requests, so the contract can't drift.
- `scripts/export-openapi.ts` generates the document with `OpenApiGeneratorV31` and writes `openapi/api.json` (sorted keys, two-space indent, trailing newline).

You can't run the export, so **hand-write the change to `openapi/api.json` in that shape** (OpenAPI 3.1, the registered schema names, the problem-details error responses) alongside the registry change. CI re-exports and fails on any difference.

## Config and database URL

`lib/config.ts` parses `process.env` with Zod at startup and fails fast. It reads `DATABASE_URL` (`postgresql://…`) directly, as the data pack expects, adding `sslmode=require` for managed hosts if the platform doesn't, plus `PORT` (default 8080 in the container).

## Health

`/api/health/live` (no checks) and `/api/health/ready` (a DB ping through the data client).

## Testing

Vitest + supertest against `createApp(testDeps)`. Unit tests sit next to the code, named by FR. Integration tests live in `test/` against a Testcontainers Postgres (global setup runs the data pack's migrate command; tests truncate between cases). Cover each FR: success, 400, 401, 403, 404, each errorCode, 409.

## Gate

CI runs, from the backend path (`npm install` instead of `npm ci` while there's no lockfile):
```bash
npm ci && npm run typecheck && npm run lint && npm test && npm run build
npm run openapi:export && git diff --exit-code openapi/api.json
```

## Containerise

`Dockerfile` in the backend path, multi-stage: `node:<lts>` runs `npm ci` and `npm run build`, then `npm ci --omit=dev` into `node:<lts>-slim`. `USER node` (non-root), `ENV NODE_ENV=production PORT=8080`, `EXPOSE 8080`, `CMD ["node","dist/server.js"]`. Include the generated ORM client and its engine where the data pack needs it. `.dockerignore` excludes `node_modules/`, `dist/` and `.env`.

## Deploy notes

- **Render:** Node web service (`npm ci && npm run build`, `npm start`) or Docker, health check path `/api/health/ready`.
- **Railway:** Railpack Node or the Dockerfile, health check path `/api/health/ready`.
- **Vercel:** zero-config Express (the app exported as default from an entrypoint). It's serverless: pooled DB connections, no in-memory sessions, no background work.
- **Not supported** on Netlify, Cloudflare or GitHub Pages.

## Checklist

- [ ] `createApp` takes its dependencies, routers are thin, Zod is on every input, and DTOs are returned.
- [ ] The error middleware returns problem+json with the spec's errorCodes, and helmet, logging and rate limits are set.
- [ ] Auth middleware plus ownership checks, and CORS only for cross-origin.
- [ ] Config is validated, the contract is exported from the same Zod schemas and is fresh, and the gate passes.
