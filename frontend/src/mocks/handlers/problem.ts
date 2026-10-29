import { HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import type { ErrorCode } from '../../lib/errors'

/** A ProblemDetails response as the API sends it (errorCode + optional validation errors, details and reason). */
export function problem(
  status: number,
  errorCode: ErrorCode,
  title: string,
  init: {
    errors?: Record<string, string[]>
    details?: string[]
    reason?: string
    /** GITHUB_APP_NOT_INSTALLED extension (FR-81, FR-83). */
    installUrl?: string
    /** STACK_INCOMPATIBLE extension (FR-116). */
    violations?: Schemas['HttpValidationProblemDetails']['violations']
    /** CONNECTION_SCOPE_INSUFFICIENT extension for Bitbucket tokens (FR-134). */
    unverifiableScopes?: string[]
    headers?: Record<string, string>
  } = {},
): Response {
  // A plain Response, so handlers can mix it with typed success bodies.
  return HttpResponse.json<Schemas['HttpValidationProblemDetails']>(
    {
      status,
      title,
      errorCode,
      traceId: '00-test-00',
      ...(init.errors ? { errors: init.errors } : {}),
      ...(init.details ? { details: init.details } : {}),
      ...(init.reason ? { reason: init.reason } : {}),
      ...(init.installUrl ? { installUrl: init.installUrl } : {}),
      ...(init.violations ? { violations: init.violations } : {}),
      ...(init.unverifiableScopes ? { unverifiableScopes: init.unverifiableScopes } : {}),
    },
    { status, headers: { 'Content-Type': 'application/problem+json', ...init.headers } },
  )
}
