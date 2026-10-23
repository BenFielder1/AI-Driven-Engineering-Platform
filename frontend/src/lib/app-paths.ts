/**
 * Every v2 route pattern (SPEC v2 §7.2 + the v2 TanStack route tree), in TanStack `$param` form as used by `Link to`
 * and nav-config. FR-95: each must have a matching `src/app` page (src/test/route-parity.test.ts).
 */
export const appPaths = [
  '/',
  '/login',
  '/register',
  '/projects',
  '/projects/new',
  '/projects/$projectId',
  '/projects/$projectId/activity',
  '/projects/$projectId/artifacts',
  '/projects/$projectId/artifacts/$type',
  '/projects/$projectId/deployments',
  '/projects/$projectId/execution',
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
