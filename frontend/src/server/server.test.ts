import { describe, expect, it } from 'vitest'

import { parseServerEnv } from '../lib/env'
import {
  clientResponseHeaders,
  isApiPath,
  isHubPath,
  peerAddress,
  problemBody,
  upstreamProblem,
  upstreamRequestHeaders,
  upstreamTarget,
} from './api-proxy'
import { errorLogLine, requestLogLine, shouldLogRequest } from './request-log'
import { cacheControlFor, contentSecurityPolicy, createNonce, pageSecurityHeaders } from './security-headers'

describe('FR-99 security headers', () => {
  it('FR-99: the CSP matches the spec, with the nonce and strict-dynamic; unsafe-eval only in dev', () => {
    expect(contentSecurityPolicy('abc', false)).toBe(
      "default-src 'self'; img-src 'self' data:; connect-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'nonce-abc' 'strict-dynamic'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
    )
    expect(contentSecurityPolicy('abc', true)).toContain("'strict-dynamic' 'unsafe-eval'")
  })

  it('FR-99: a fresh nonce per request', () => {
    const nonces = new Set(Array.from({ length: 20 }, createNonce))
    expect(nonces.size).toBe(20)
    expect([...nonces][0]).toMatch(/^[A-Za-z0-9+/]{22}==$/)
  })

  it('FR-99: the v2 nginx headers plus no-cache HTML', () => {
    expect(pageSecurityHeaders('n', false)).toMatchObject({
      'X-Content-Type-Options': 'nosniff',
      'Referrer-Policy': 'strict-origin-when-cross-origin',
      'X-Frame-Options': 'DENY',
      'Cache-Control': 'no-cache',
    })
  })

  it('FR-99: /_next/static assets are immutable for a year in production; HTML and everything else revalidates', () => {
    expect(pageSecurityHeaders('n', false, '/_next/static/chunks/app-1a2b.js')['Cache-Control']).toBe('public, max-age=31536000, immutable')
    expect(cacheControlFor('/_next/static/media/font.woff2', false)).toBe('public, max-age=31536000, immutable')
    expect(cacheControlFor('/_next/static/chunks/app.js', true)).toBe('no-cache')
    expect(cacheControlFor('/projects?x=/_next/static/', false)).toBe('no-cache')
    expect(cacheControlFor('/favicon.svg', false)).toBe('no-cache')
    expect(cacheControlFor('/healthz', false)).toBe('no-cache')
  })
})

describe('FR-98 api proxy headers', () => {
  it('FR-98: /api paths and hub paths', () => {
    expect(isApiPath('/api/projects?x=1')).toBe(true)
    expect(isApiPath('/api')).toBe(true)
    expect(isApiPath('/apiary')).toBe(false)
    expect(isHubPath('/api/hubs/projects?id=1')).toBe(true)
    expect(peerAddress('::ffff:10.0.0.7')).toBe('10.0.0.7')
  })

  it('FR-98: cookies, auth and XSRF pass through; XFF gets the peer appended; proto/host set when absent', () => {
    const headers = upstreamRequestHeaders(
      {
        host: 'aide.example',
        cookie: 'aide.auth=x',
        authorization: 'Bearer t',
        'x-xsrf-token': 'tok',
        connection: 'keep-alive, x-drop',
        'x-drop': '1',
        'keep-alive': 'timeout=5',
      },
      '10.0.0.7',
      false,
    )
    expect(headers).toMatchObject({
      host: 'aide.example',
      cookie: 'aide.auth=x',
      authorization: 'Bearer t',
      'x-xsrf-token': 'tok',
      'x-forwarded-for': '10.0.0.7',
      'x-forwarded-proto': 'http',
      'x-forwarded-host': 'aide.example',
    })
    expect(headers).not.toHaveProperty('connection')
    expect(headers).not.toHaveProperty('keep-alive')
    expect(headers).not.toHaveProperty('x-drop')
  })

  it('FR-98: with TRUST_EDGE_FORWARDED the edge values are kept and the peer appended to its X-Forwarded-For', () => {
    const headers = upstreamRequestHeaders(
      { host: 'web', 'x-forwarded-for': '203.0.113.9', 'x-forwarded-proto': 'https', 'x-forwarded-host': 'aide.up.railway.app' },
      '10.0.0.7',
      false,
      false,
      true,
    )
    expect(headers['x-forwarded-for']).toBe('203.0.113.9, 10.0.0.7')
    expect(headers['x-forwarded-proto']).toBe('https')
    expect(headers['x-forwarded-host']).toBe('aide.up.railway.app')
  })

  it('FR-98: without TRUST_EDGE_FORWARDED client-sent X-Forwarded-Proto/Host are overwritten from the connection', () => {
    const headers = upstreamRequestHeaders(
      { host: 'localhost:3000', 'x-forwarded-for': '203.0.113.9', 'x-forwarded-proto': 'https', 'x-forwarded-host': 'evil.example' },
      '10.0.0.7',
      false,
    )
    expect(headers['x-forwarded-for']).toBe('203.0.113.9, 10.0.0.7')
    expect(headers['x-forwarded-proto']).toBe('http')
    expect(headers['x-forwarded-host']).toBe('localhost:3000')
    expect(upstreamRequestHeaders({ 'x-forwarded-host': 'evil.example' }, '1.2.3.4', true)).not.toHaveProperty('x-forwarded-host')
  })

  it('FR-98: TRUST_EDGE_FORWARDED is off unless set to true', () => {
    expect(parseServerEnv({}).TRUST_EDGE_FORWARDED).toBe(false)
    expect(parseServerEnv({ TRUST_EDGE_FORWARDED: '' }).TRUST_EDGE_FORWARDED).toBe(false)
    expect(parseServerEnv({ TRUST_EDGE_FORWARDED: 'TRUE' }).TRUST_EDGE_FORWARDED).toBe(true)
    expect(() => parseServerEnv({ TRUST_EDGE_FORWARDED: 'yes' })).toThrow(/TRUST_EDGE_FORWARDED/)
  })

  it('FR-98: targets that resolve outside /api are rejected', () => {
    const backend = 'http://api:8080'
    expect(upstreamTarget('/api/projects?x=1', backend)?.href).toBe('http://api:8080/api/projects?x=1')
    expect(upstreamTarget('/api', backend)?.href).toBe('http://api:8080/api')
    expect(upstreamTarget('/api/../admin', backend)).toBeUndefined()
    expect(upstreamTarget('/api/%2e%2e/admin', backend)).toBeUndefined()
    expect(upstreamTarget('/api/..%2fadmin', backend)?.pathname).toBe('/api/..%2fadmin')
    expect(upstreamTarget(String.raw`/api\..\admin`, backend)).toBeUndefined()
    expect(upstreamTarget('/apiary', backend)).toBeUndefined()
  })

  it('FR-98: WebSocket upgrades keep Upgrade/Connection', () => {
    const headers = upstreamRequestHeaders({ host: 'web', upgrade: 'websocket', connection: 'Upgrade' }, '1.2.3.4', true, true)
    expect(headers).toMatchObject({ upgrade: 'websocket', connection: 'Upgrade', 'x-forwarded-proto': 'https' })
  })

  it('FR-98/FR-99: Set-Cookie passes through unchanged and nosniff is added', () => {
    const headers = clientResponseHeaders({ 'set-cookie': ['a=1; Path=/; HttpOnly', 'b=2'], 'transfer-encoding': 'chunked' })
    expect(headers['set-cookie']).toEqual(['a=1; Path=/; HttpOnly', 'b=2'])
    expect(headers['x-content-type-options']).toBe('nosniff')
    expect(headers).not.toHaveProperty('transfer-encoding')
  })

  it('FR-98: the upstream failure is a 502 UPSTREAM_ERROR problem', () => {
    expect(JSON.parse(problemBody(upstreamProblem))).toMatchObject({ status: 502, errorCode: 'UPSTREAM_ERROR' })
  })
})

describe('FR-100 request log', () => {
  it('FR-100: one JSON line with method, redacted path and Referer, status and duration', () => {
    const line = requestLogLine(
      { method: 'GET', url: '/approvals/abc123', status: 200, durationMs: 12.345, referer: 'https://aide.example/approvals/abc123#top' },
      new Date('2026-10-23T00:00:00Z'),
    )
    expect(JSON.parse(line)).toEqual({
      timestamp: '2026-10-23T00:00:00.000Z',
      level: 'info',
      msg: 'request',
      method: 'GET',
      path: '/approvals/[redacted]',
      status: 200,
      durationMs: 12.3,
      referer: 'https://aide.example/approvals/[redacted]#top',
    })
    expect(line).not.toContain('abc123')
  })

  it('FR-100: error lines never carry an approval token', () => {
    expect(errorLogLine('proxy', new Error('connect failed for /api/approvals/abc123?x=1'))).not.toContain('abc123')
  })

  it('FR-100: a signed-out approval visit (returnUrl in path and Referer) never logs the token', () => {
    const line = requestLogLine({
      method: 'GET',
      url: '/api/auth/github/login?returnUrl=%2Fapprovals%2Fabc123',
      status: 302,
      durationMs: 1,
      referer: 'https://aide.example/login?returnUrl=%2Fapprovals%2Fabc123',
    })
    expect(line).not.toContain('abc123')
  })

  it('skips static assets and the health probe', () => {
    expect(shouldLogRequest('/_next/static/chunks/a.js')).toBe(false)
    expect(shouldLogRequest('/healthz')).toBe(false)
    expect(shouldLogRequest('/projects')).toBe(true)
  })
})
