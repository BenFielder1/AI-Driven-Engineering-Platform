import { z } from 'zod'

/**
 * Server-only runtime environment of the web server (STACK.md "Frontend env"), validated with Zod at startup.
 * `BACKEND_URL` is read at runtime (never `NEXT_PUBLIC_*`), so one image serves Kubernetes (`http://api:8080`) and
 * Railway (`http://api.railway.internal:8080`). Imported by server.ts (Node type stripping): keep imports to `zod`.
 */
const serverEnvSchema = z.object({
  BACKEND_URL: z
    .url({ protocol: /^https?$/ })
    .default('http://localhost:5080')
    .transform((v) => v.replace(/\/+$/, '')),
  PORT: z.coerce.number().int().min(1).max(65535).optional(),
  /**
   * `true` only when an edge proxy in front of the web server always sets X-Forwarded-Proto and X-Forwarded-Host
   * (Railway). Then the /api proxy keeps them; otherwise (default, Kubernetes, dev loop) it overwrites any client-sent
   * values with this connection's protocol and Host header (FR-98).
   */
  TRUST_EDGE_FORWARDED: z
    .enum(['true', 'false'])
    .default('false')
    .transform((v) => v === 'true'),
})

export type ServerEnv = z.output<typeof serverEnvSchema>

/** Parses the server env; throws with a readable message when a value is invalid. */
export function parseServerEnv(env: Record<string, string | undefined>): ServerEnv {
  // Empty values (e.g. `BACKEND_URL=` in a manifest) mean "unset".
  const value = (name: string) => (env[name] === '' ? undefined : env[name])
  const result = serverEnvSchema.safeParse({
    BACKEND_URL: value('BACKEND_URL'),
    PORT: value('PORT'),
    TRUST_EDGE_FORWARDED: value('TRUST_EDGE_FORWARDED')?.toLowerCase(),
  })
  if (!result.success) {
    throw new Error(`Invalid web server environment: ${z.prettifyError(result.error)}`)
  }
  return result.data
}
