Rules for Next.js App Router route handlers (`app/api/**/route.ts`) backed by Supabase: Zod validation, auth, RPC calls
and error mapping. The specification's API section gives each route's method, path, body, response and permissions.

### Shared helpers (`lib/api/`, created once)
```ts
// lib/api/errors.ts
import { NextResponse } from 'next/server';
import type { PostgrestError } from '@supabase/supabase-js';

export const ERROR_STATUS = {
  NOT_AUTHENTICATED: 401,
  INVALID_INPUT: 400,
  FORBIDDEN: 403,
  NOT_FOUND: 404,
  CONFLICT: 409,
  INTERNAL: 500,
  // + the specification's codes, e.g. NAME_TAKEN: 409, NOT_OWNER: 403
} as const;
export type ErrorCode = keyof typeof ERROR_STATUS;

export function apiError(code: ErrorCode, message?: string, details?: unknown) {
  return NextResponse.json({ error: { code, message: message ?? code, details } }, { status: ERROR_STATUS[code] });
}

// The ONLY place Postgres errors are interpreted.
export function fromPostgresError(e: PostgrestError) {
  if (e.code === 'P0001' && e.message in ERROR_STATUS) return apiError(e.message as ErrorCode);
  if (e.code === '23505') return apiError('CONFLICT', 'Already exists');
  if (e.code === '42501' || e.code === 'PGRST116') return apiError('NOT_FOUND');
  console.error(e);
  return apiError('INTERNAL', 'Something went wrong');
}

// lib/api/auth.ts
import type { SupabaseClient } from '@supabase/supabase-js';
export async function requireUser(supabase: SupabaseClient) {
  const { data: { user } } = await supabase.auth.getUser(); // verifies the JWT; never getSession()
  return user; // anonymous users are users
}
```

### Handler shape
```ts
// app/api/things/[id]/route.ts
import { NextResponse } from 'next/server';
import { z } from 'zod';
import { createClient } from '@/lib/supabase/server';
import { requireUser } from '@/lib/api/auth';
import { apiError, fromPostgresError } from '@/lib/api/errors';
import { toThing } from '@/lib/types';

const Params = z.object({ id: z.string().uuid() });
const Body = z.object({ name: z.string().trim().min(1).max(100) });

export async function PATCH(req: Request, ctx: { params: Promise<{ id: string }> }) {
  const params = Params.safeParse(await ctx.params);
  if (!params.success) return apiError('INVALID_INPUT', undefined, params.error.flatten());

  const supabase = await createClient();
  if (!(await requireUser(supabase))) return apiError('NOT_AUTHENTICATED');

  const body = Body.safeParse(await req.json().catch(() => null));
  if (!body.success) return apiError('INVALID_INPUT', undefined, body.error.flatten());

  const { data, error } = await supabase.from('things')
    .update({ name: body.data.name }).eq('id', params.data.id).select().single();
  if (error) return fromPostgresError(error);

  return NextResponse.json({ thing: toThing(data) });
}
```

### Rules
1. **User-scoped client only** (`lib/supabase/server.ts`). RLS and RPCs are the real permission check.
2. **Zod validates params, query and body** before the database is touched; normalise (trim, case) in the schema.
3. **Privileged writes go through RPCs** (`supabase.rpc('fn', args)`); never reimplement their checks or add a looser path.
4. **Errors only via `apiError` / `fromPostgresError`.** A new code goes into `ERROR_STATUS` and the UI's error copy together.
5. **Return domain types** (camelCase mappers from `lib/types.ts`), never raw rows.
6. **Business rules come from `lib/<domain>/`.**
7. **Thin handlers**, about 60 lines at most.
8. **Webhooks and cron** verify a signature or secret header, are idempotent, import `server-only`, and are the only
   place a server-only secret (e.g. `SUPABASE_SERVICE_ROLE_KEY`) may be used.
9. **No caching** of authenticated, user-specific routes.

### Tests
Per route, a Vitest test with a mocked Supabase client (`vi.mock('@/lib/supabase/server')`): success, invalid input,
unauthenticated, and one mapped database error.

### Checklist
- [ ] Method, path, body and response match the specification.
- [ ] Zod covers params, query and body.
- [ ] Every error path returns a known code.
- [ ] Works for every seeded role, including guests if the specification has them.
