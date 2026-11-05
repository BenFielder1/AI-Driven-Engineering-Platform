/** v6 FR-210, FR-211: the marketing routes (route group `(marketing)`); `/` is the marketing Home for visitors. */
export const marketingPaths = [
  '/',
  '/product',
  '/how-it-works',
  '/integrations',
  '/self-host',
  '/security',
  '/faq',
  '/changelog',
] as const

/** Routes a visitor can open signed out: the marketing site and the auth pages. Everything else is an app route. */
export const publicPaths = [...marketingPaths, '/login', '/register'] as const

/**
 * Every v2 route pattern (SPEC v2 §7.2 + the v2 TanStack route tree), in TanStack `$param` form as used by `Link to`
 * and nav-config. FR-95: each must have a matching `src/app` page (src/test/route-parity.test.ts).
 */
export const appPaths = [
  ...publicPaths,
  // v6 FR-207: Home, the signed-in landing page.
  '/home',
  '/projects',
  '/projects/new',
  '/projects/$projectId',
  '/projects/$projectId/activity',
  // v5 FR-182: the Analysis page.
  '/projects/$projectId/analysis',
  '/projects/$projectId/artifacts',
  '/projects/$projectId/artifacts/$type',
  '/projects/$projectId/deployments',
  '/projects/$projectId/execution',
  '/projects/$projectId/migrations',
  '/projects/$projectId/planning',
  '/projects/$projectId/refinement',
  '/projects/$projectId/release',
  '/projects/$projectId/review',
  '/projects/$projectId/runs',
  '/projects/$projectId/settings',
  '/projects/$projectId/settings/$section',
  '/projects/$projectId/specification',
  '/account',
  '/account/connections',
  '/connections',
  '/notifications',
  '/approvals/$token',
  '/admin',
  '/admin/dead-letters',
  '/admin/events',
  '/admin/integrations',
  '/admin/runs',
  '/admin/users',
] as const

/** A route pattern the app serves, e.g. `/projects/$projectId/review`. */
export type AppPath = (typeof appPaths)[number]

/** `/projects/$projectId` → `/projects/[projectId]` (the src/app folder form). */
export function toAppRouterPattern(path: string): string {
  return path.replace(/\$([A-Za-z0-9_]+)/g, '[$1]')
}
