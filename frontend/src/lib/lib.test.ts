import { describe, expect, it } from 'vitest'

import { ApiError, errorCodes, errorMessage, errorMessages, toApiError } from './errors'
import { canInProject, canViewAdmin, isWriter, projectCapabilities } from './permissions'
import { safeReturnUrl } from './return-url'

// The backend's error-code list, read at test time so the two sides can't drift (CLAUDE.md rule 4).
const errorCodesSource = Object.values(
  import.meta.glob<string>('../../../backend/src/Aide.Domain/Common/ErrorCodes.cs', {
    query: '?raw',
    import: 'default',
    eager: true,
  }),
)[0]

describe('errors.ts', () => {
  it('maps every endpoint and run errorCode from ErrorCodes.cs to copy', () => {
    expect(errorCodesSource).toBeDefined()
    const source = (errorCodesSource ?? '').split('public static class Guardian')[0] ?? ''
    const backendCodes = [...source.matchAll(/public const string \w+ = "([A-Z_]+)";/g)].map((m) => m[1])
    expect(backendCodes.length).toBeGreaterThan(20)
    expect([...errorCodes].sort()).toEqual([...backendCodes].sort())
  })

  it('normalises ProblemDetails and falls back to the status when errorCode is unknown', () => {
    const fromBody = toApiError({ errorCode: 'EMAIL_TAKEN', title: 'x' }, new Response(null, { status: 409 }))
    expect(fromBody.errorCode).toBe('EMAIL_TAKEN')
    const fromStatus = toApiError(undefined, new Response(null, { status: 404 }))
    expect(fromStatus.errorCode).toBe('NOT_FOUND')
    expect(errorMessage(fromStatus)).toBe(errorMessages.NOT_FOUND)
  })

  it('RATE_LIMITED copy uses Retry-After when present', () => {
    const err = toApiError({ errorCode: 'RATE_LIMITED' }, new Response(null, { status: 429, headers: { 'Retry-After': '1' } }))
    expect(errorMessage(err)).toBe('Too many attempts. Try again in 1 second.')
    expect(errorMessage(new ApiError({ status: 429, errorCode: 'RATE_LIMITED' }))).toBe(errorMessages.RATE_LIMITED)
  })

  it('network failures get connection copy', () => {
    expect(errorMessage(new TypeError('Failed to fetch'))).toMatch(/couldn't reach the server/)
  })
})

describe('permissions.ts (SPEC §2)', () => {
  const admin = { systemRole: 'admin' as const }
  const user = { systemRole: 'user' as const }

  it('FR-3: only admins can view the admin area', () => {
    expect(canViewAdmin(admin)).toBe(true)
    expect(canViewAdmin(user)).toBe(false)
    expect(canViewAdmin(null)).toBe(false)
  })

  it('owners can do everything, contributors write but not decide, viewers only view', () => {
    expect(canInProject(user, 'owner', 'deleteProject')).toBe(true)
    expect(canInProject(user, 'contributor', 'startExecution')).toBe(true)
    expect(canInProject(user, 'contributor', 'decideGates')).toBe(false)
    expect(canInProject(user, 'contributor', 'rollback')).toBe(false)
    expect(canInProject(user, 'viewer', 'view')).toBe(true)
    expect(canInProject(user, 'viewer', 'createDraft')).toBe(false)
    expect(canInProject(user, null, 'view')).toBe(false)
    expect(projectCapabilities.viewer).toEqual(['view'])
  })

  it('admins can do everything on every project', () => {
    expect(canInProject(admin, null, 'deleteProject')).toBe(true)
  })

  it('writers are owners and contributors', () => {
    expect(isWriter('owner')).toBe(true)
    expect(isWriter('contributor')).toBe(true)
    expect(isWriter('viewer')).toBe(false)
  })
})

describe('safeReturnUrl', () => {
  it.each([
    ['/account', '/account'],
    ['/projects?page=2', '/projects?page=2'],
    ['//evil.example.com', '/projects'],
    ['/\\evil.example.com', '/projects'],
    ['https://evil.example.com', '/projects'],
    ['/login', '/projects'],
    ['/api/auth/logout', '/projects'],
    [undefined, '/projects'],
  ])('%s → %s', (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected)
  })
})
