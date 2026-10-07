import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { authDb, signInAs } from '../mocks/handlers/auth'
import { problem } from '../mocks/handlers/problem'
import { server } from '../mocks/server'
import { api, readXsrfToken, XSRF_HEADER } from './client'

describe('API client antiforgery (NFR-SEC)', () => {
  it('NFR-SEC: fetches the XSRF-TOKEN cookie when missing and sends it as X-XSRF-TOKEN on mutations', async () => {
    signInAs()
    const seen: (string | null)[] = []
    server.use(
      http.post('*/api/auth/logout', ({ request }) => {
        seen.push(request.headers.get(XSRF_HEADER))
        return new HttpResponse(null, { status: 204 })
      }),
    )

    expect(readXsrfToken()).toBeNull()
    const { response } = await api.POST('/api/auth/logout')

    expect(response.status).toBe(204)
    expect(seen).toEqual(['xsrf-1'])
  })

  it('NFR-SEC: does not send the header on GET requests', async () => {
    document.cookie = 'XSRF-TOKEN=abc; path=/'
    let header: string | null = 'unset'
    server.use(
      http.get('*/api/auth/providers', ({ request }) => {
        header = request.headers.get(XSRF_HEADER)
        return HttpResponse.json({ github: false })
      }),
    )

    await api.GET('/api/auth/providers')

    expect(header).toBeNull()
  })

  it('NFR-SEC: on 403 ANTIFORGERY refreshes the token and retries once, with the same body', async () => {
    signInAs()
    document.cookie = 'XSRF-TOKEN=stale; path=/'
    const attempts: { header: string | null; body: unknown }[] = []
    server.use(
      http.post('*/api/me/tokens', async ({ request }) => {
        attempts.push({ header: request.headers.get(XSRF_HEADER), body: await request.json() })
        if (request.headers.get(XSRF_HEADER) !== authDb.validXsrf) {
          return problem(403, 'ANTIFORGERY', 'Missing token.')
        }
        return HttpResponse.json(
          { id: '00000000-0000-7000-8000-000000000001', name: 'cli', token: 'aide_pat_x', expiresAt: null },
          { status: 201 },
        )
      }),
    )

    const { data, response } = await api.POST('/api/me/tokens', { body: { name: 'cli' } })

    expect(response.status).toBe(201)
    expect(data?.name).toBe('cli')
    expect(attempts).toEqual([
      { header: 'stale', body: { name: 'cli' } },
      { header: 'xsrf-1', body: { name: 'cli' } },
    ])
  })

  it('NFR-SEC: retries only once, then returns the 403', async () => {
    signInAs()
    let calls = 0
    server.use(
      http.post('*/api/auth/logout', () => {
        calls += 1
        return problem(403, 'ANTIFORGERY', 'Missing token.')
      }),
    )

    const { error, response } = await api.POST('/api/auth/logout')

    expect(response.status).toBe(403)
    expect(error).toMatchObject({ errorCode: 'ANTIFORGERY' })
    expect(calls).toBe(2)
  })

  it('NFR-SEC: a plain 403 FORBIDDEN is not retried', async () => {
    document.cookie = 'XSRF-TOKEN=abc; path=/'
    let calls = 0
    server.use(
      http.post('*/api/auth/logout', () => {
        calls += 1
        return problem(403, 'FORBIDDEN', 'No.')
      }),
    )

    await api.POST('/api/auth/logout')

    expect(calls).toBe(1)
  })
})
