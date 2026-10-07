import createClient from 'openapi-fetch'

import type { components, paths } from './generated/schema'

/**
 * The only way the frontend talks to the backend (CLAUDE.md rule 2).
 * Same-origin: generated paths already start with `/api`, so the base is the page origin.
 * `fetch` is looked up per request so test interceptors (MSW) apply.
 */
export const api = createClient<paths>({
  baseUrl: globalThis.location.origin,
  credentials: 'same-origin',
  fetch: (request) => globalThis.fetch(request),
})

export type Schemas = components['schemas']
