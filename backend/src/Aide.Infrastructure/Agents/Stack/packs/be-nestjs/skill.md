<!-- Adapted from stack-packs be-nestjs@1.0.0 skills/be-nestjs/SKILL.md, snapshot 2026-10-25 -->

# Node.js + NestJS

## Detect

Read the `@nestjs/*` versions, the validation approach (class-validator or Zod), the ORM integration, the test runner (Jest by default; Vitest if the repo switched), the Swagger setup and the module structure. Mirror the nearest module.

## Scaffold

Write what the Nest CLI (`new --strict`) would create, under the backend path:
- **`package.json`:** scripts `build` (`nest build`), `start`, `start:dev`, `lint`, `test`, `test:e2e` (`jest --config ./test/jest-e2e.json`), `openapi:export` (`tsx scripts/export-openapi.ts`).
  - **Dependencies:** `@nestjs/common`, `@nestjs/core`, `@nestjs/platform-express`, `@nestjs/config`, `@nestjs/swagger`, `@nestjs/terminus`, `@nestjs/throttler`, `class-validator`, `class-transformer`, `helmet`, `nestjs-pino` (or the built-in logger), `reflect-metadata`, `rxjs`.
  - **Dev dependencies:** `@nestjs/cli`, `@nestjs/testing`, `typescript`, `tsx`, `jest`, `ts-jest`, `supertest`, `@testcontainers/postgresql`, `eslint`, `prettier`.
  - Use current stable versions you are confident exist, and pin Node in `.nvmrc` (or `.node-version`). There's no lockfile until the first CI run.
- **`nest-cli.json`** with the Swagger CLI plugin (`"plugins": ["@nestjs/swagger"]`), `tsconfig.json` (`strict`), `tsconfig.build.json`, `test/jest-e2e.json`.
- **`src/main.ts`:** `app.setGlobalPrefix('api')`, `ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true })`, helmet, the exception filter, listening on `PORT`.
- **`openapi/api.json`:** the initial contract (see Contract export).

## Conventions

- **One feature module** per bounded area (`orders/`: module, controller, service, `dto/`, `domain/`, `*.spec.ts`). Shared code in `common/` (filters, guards, interceptors, decorators) and `config/`.
- **Controllers** are thin and return DTOs (`@ApiOkResponse({ type: OrderDto })`), never ORM entities. **Services** orchestrate. **Domain rules** are pure classes or functions, unit-tested.
- **Injection:** constructor injection. The data client is a provider (`PrismaService` / `DrizzleProvider`, from the data pack). Inject a `Clock` provider for time.
- **Config:** `ConfigModule.forRoot({ isGlobal: true, validate })` with a schema (class-validator or Zod). Fail fast.

## Errors

- `DomainError(status, errorCode, title)`, with codes in `common/error-codes.ts` mirroring the spec's error table.
- A global `ProblemDetailsFilter` (`@Catch()`) maps:
  - `DomainError` to its status
  - the ValidationPipe's `BadRequestException` to 400 with an `errors` map
  - other `HttpException`s to their status
  - unknown errors to 500
- Every error response is `application/problem+json` with `errorCode`. Document error responses on endpoints with `@ApiResponse`.

## Auth

- **Guards:** `AuthGuard` (session cookie for same-origin, or JWT via `@nestjs/jwt` / passport-jwt for cross-origin), plus `PermissionsGuard` with a `@RequirePermission()` decorator, plus ownership checks in services.
- **Rate limiting:** `ThrottlerModule` on auth routes.
- **CORS:** only for the cross-origin model, `app.enableCors({ origin: <CORS_ALLOWED_ORIGINS list>, credentials })`.

## Contract export

- Every handler has a stable `operationId` (`@ApiOperation({ operationId: 'getOrder' })`), or set `operationIdFactory: (controllerKey, methodKey) => methodKey`.
- `scripts/export-openapi.ts`:
  1. creates the app with `NestFactory.create(AppModule, { logger: false })`
  2. builds `SwaggerModule.createDocument(app, config, { operationIdFactory })`
  3. writes `openapi/api.json`, stable-sorted with a two-space indent
  4. closes the app

  It must work without a live database, so the DB connection is lazy (or mocked in the export).

You can't run the export, so **hand-write the change to `openapi/api.json` in @nestjs/swagger's shape** (keep the existing `openapi` version, key order and indentation; schemas named after the DTO classes; the problem-details error responses). CI re-exports and fails on any difference.

## Config and database URL

`DATABASE_URL` (`postgresql://…`), as the data pack expects, validated by the config schema, with `sslmode=require` for managed hosts. `PORT` defaults to 8080 in the container.

## Health

Terminus `HealthController`: `/api/health/live` (no checks) and `/api/health/ready` (a DB ping through the data provider).

## Testing

- **Unit:** services and domain with `Test.createTestingModule` and mocked providers, named by FR.
- **e2e:** `test/*.e2e-spec.ts` with supertest against the full app plus a Testcontainers Postgres (migrations applied in global setup). Cover success, 400, 401, 403, 404, each errorCode and 409.

## Gate

CI runs, from the backend path (`npm install` instead of `npm ci` while there's no lockfile; add `npx tsc --noEmit` if the build doesn't typecheck tests):
```bash
npm ci && npm run lint && npm run build && npm test && npm run test:e2e
npm run openapi:export && git diff --exit-code openapi/api.json
```

## Containerise

`Dockerfile` in the backend path, multi-stage: `node:<lts>` runs `npm ci` and `npm run build`, then prunes dev dependencies, into `node:<lts>-slim`. `USER node` (non-root), `ENV NODE_ENV=production PORT=8080`, `EXPOSE 8080`, `CMD ["node","dist/main.js"]`. Include the ORM's generated client. `.dockerignore` excludes `node_modules/`, `dist/` and `.env`.

## Deploy notes

- **Render:** Node service or Docker, health check path `/api/health/ready`.
- **Railway:** Node or the Dockerfile, health check path `/api/health/ready`.
- **Vercel:** zero-config NestJS support. It's serverless: pool DB connections and avoid in-memory state and background work.
- **Not supported** on Netlify, Cloudflare or GitHub Pages.

## Checklist

- [ ] Feature modules, thin controllers returning DTOs, and pure domain rules tested by FR.
- [ ] A global ValidationPipe and ProblemDetailsFilter with the spec's errorCodes. Guards plus ownership checks.
- [ ] Config is validated at boot, and health uses Terminus.
- [ ] Operation IDs are stable, the export runs without a DB, the contract is fresh, and the gate passes.
