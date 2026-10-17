Rules for Next.js App Router route handlers (`app/api/**/route.ts`) that sit in front of external APIs: Zod validation,
a server-only upstream client, response parsing and error mapping. The specification's Data Sources & Consumed APIs
section gives each upstream endpoint; its API section (if any) gives the BFF routes the UI calls.

### Shared helpers (`lib/api/`, created once)
```ts
// lib/api/errors.ts
import { NextResponse } from 'next/server';

export const ERROR_STATUS = {
  INVALID_INPUT: 400,
  NOT_AUTHENTICATED: 401,
  FORBIDDEN: 403,
  NOT_FOUND: 404,
  RATE_LIMITED: 429,
  INTERNAL: 500,
  UPSTREAM_INVALID: 502,
  UPSTREAM_UNAVAILABLE: 503,
  // + the specification's codes
} as const;
export type ErrorCode = keyof typeof ERROR_STATUS;

export function apiError(code: ErrorCode, message?: string, details?: unknown) {
  return NextResponse.json({ error: { code, message: message ?? code, details } }, { status: ERROR_STATUS[code] });
}

export class UpstreamError extends Error {
  constructor(readonly code: ErrorCode, readonly status?: number) { super(code); }
}

// The ONLY place upstream failures are interpreted.
export function fromUpstreamError(e: unknown) {
  if (e instanceof UpstreamError) return apiError(e.code);
  console.error(e);
  return apiError('INTERNAL', 'Something went wrong');
}
```

```ts
// lib/api/client.ts
import 'server-only';
import type { z } from 'zod';
import { UpstreamError } from './errors';

const BASE_URL = process.env.UPSTREAM_API_URL; // name from the spec's Environment Variables section

export async function upstream<T>(path: string, schema: z.ZodType<T>, init: RequestInit & { revalidate?: number } = {}) {
  if (!BASE_URL) throw new UpstreamError('INTERNAL');
  let res: Response;
  try {
    res = await fetch(new URL(path, BASE_URL), {
      ...init,
      headers: { Accept: 'application/json', Authorization: `Bearer ${process.env.UPSTREAM_API_KEY ?? ''}`, ...init.headers },
      signal: AbortSignal.timeout(8000),
      ...(init.revalidate === undefined ? { cache: 'no-store' } : { next: { revalidate: init.revalidate } }),
    });
  } catch {
    throw new UpstreamError('UPSTREAM_UNAVAILABLE');
  }
  if (res.status === 404) throw new UpstreamError('NOT_FOUND', 404);
  if (res.status === 429) throw new UpstreamError('RATE_LIMITED', 429);
  if (!res.ok) throw new UpstreamError('UPSTREAM_UNAVAILABLE', res.status);
  const parsed = schema.safeParse(await res.json().catch(() => null));
  if (!parsed.success) throw new UpstreamError('UPSTREAM_INVALID');
  return parsed.data;
}
```
Only send an `Authorization` header if the upstream needs one; `path` is always a fixed path built in code.

### Handler shape
```ts
// app/api/products/[id]/route.ts
import { NextResponse } from 'next/server';
import { z } from 'zod';
import { upstream } from '@/lib/api/client';
import { ProductSchema } from '@/lib/api/schemas';
import { apiError, fromUpstreamError } from '@/lib/api/errors';
import { toProduct } from '@/lib/types';

const Params = z.object({ id: z.string().regex(/^[a-z0-9-]{1,64}$/) });

export async function GET(_req: Request, ctx: { params: Promise<{ id: string }> }) {
  const params = Params.safeParse(await ctx.params);
  if (!params.success) return apiError('INVALID_INPUT', undefined, params.error.flatten());
  try {
    const product = await upstream(`/products/${encodeURIComponent(params.data.id)}`, ProductSchema, { revalidate: 300 });
    return NextResponse.json({ product: toProduct(product) });
  } catch (e) {
    return fromUpstreamError(e);
  }
}
```

### Rules
1. **Zod validates params, query and body** before calling upstream; normalise (trim, case) in the schema.
2. **Zod parses every upstream response** (`lib/api/schemas.ts`); domain types are inferred from those schemas.
3. **One upstream client**, server-only, with a fixed base URL, a timeout and explicit caching.
4. **Errors only via `apiError` / `fromUpstreamError`.** A new code goes into `ERROR_STATUS` and the UI's error copy together.
5. **No open proxy**: never forward a URL, host or arbitrary path from the request.
6. **Secrets server-side only**: no `NEXT_PUBLIC_` prefix on upstream keys; never imported into client code.
7. **Return domain types** (camelCase mappers from `lib/types.ts`), never raw upstream payloads.
8. **Business rules come from `lib/<domain>/`.** Handlers stay thin, about 60 lines at most.
9. **Caching**: user-specific responses are `no-store`; shared data uses the revalidate interval from the specification.

### Tests
Per route, a Vitest test with the upstream mocked (MSW handlers from `mocks/`, or `vi.mock('@/lib/api/client')`):
success, invalid input, and one mapped upstream error (timeout, 404 or invalid shape). No real network calls.

### Checklist
- [ ] Method, path, body and response match the specification.
- [ ] Zod covers params, query, body and the upstream response.
- [ ] Every error path returns a known code.
- [ ] Upstream calls have a timeout and the specified caching.
