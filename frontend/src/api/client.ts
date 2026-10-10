import createClient from 'openapi-fetch'

import type { components, paths } from './generated/schema'

export type Schemas = components['schemas']

/** NFR-SEC antiforgery: the API issues a readable `XSRF-TOKEN` cookie; mutations echo it in this header. */
export const XSRF_COOKIE = 'XSRF-TOKEN'
export const XSRF_HEADER = 'X-XSRF-TOKEN'

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE'])

export function readXsrfToken(): string | null {
  for (const part of document.cookie.split(';')) {
    const [rawName, ...rest] = part.trim().split('=')
    if (rawName === XSRF_COOKIE) {
      const value = rest.join('=')
      try {
        return value ? decodeURIComponent(value) : null
      } catch {
        return value || null
      }
    }
  }
  return null
}

/** GET /api/auth/antiforgery (sets or refreshes the XSRF-TOKEN cookie). Errors are swallowed: the mutation reports them. */
export async function refreshAntiforgeryToken(): Promise<void> {
  try {
    await api.GET('/api/auth/antiforgery')
  } catch {
    // Network failure: the following mutation surfaces the error to the user.
  }
}

async function isAntiforgeryRejection(response: Response): Promise<boolean> {
  if (response.status !== 403) {
    return false
  }
  try {
    const body: unknown = await response.clone().json()
    return typeof body === 'object' && body !== null && (body as { errorCode?: unknown }).errorCode === 'ANTIFORGERY'
  } catch {
    return false
  }
}

function withXsrfHeader(request: Request): Request {
  const token = readXsrfToken()
  if (!token) {
    return request
  }
  const headers = new Headers(request.headers)
  headers.set(XSRF_HEADER, token)
  return new Request(request, { headers })
}

/**
 * Every API request goes through here. Mutations carry X-XSRF-TOKEN (fetching the cookie first if it is missing);
 * a 403 `ANTIFORGERY` refreshes the token and retries exactly once. `fetch` is looked up per request so MSW applies.
 */
export async function apiFetch(request: Request): Promise<Response> {
  if (SAFE_METHODS.has(request.method.toUpperCase())) {
    return globalThis.fetch(request)
  }

  if (!readXsrfToken()) {
    await refreshAntiforgeryToken()
  }
  const retryCopy = request.clone()
  const response = await globalThis.fetch(withXsrfHeader(request))
  if (!(await isAntiforgeryRejection(response))) {
    return response
  }

  await refreshAntiforgeryToken()
  return globalThis.fetch(withXsrfHeader(retryCopy))
}

/**
 * The only way the frontend talks to the backend (CLAUDE.md rule 2).
 * Same-origin: generated paths already start with `/api`, so the base is the page origin.
 */
export const api = createClient<paths>({
  baseUrl: globalThis.location.origin,
  credentials: 'same-origin',
  fetch: apiFetch,
})
