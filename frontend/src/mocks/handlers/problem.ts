import { HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import type { ErrorCode } from '../../lib/errors'

/** A ProblemDetails response as the API sends it (errorCode + optional validation errors). */
export function problem(
  status: number,
  errorCode: ErrorCode,
  title: string,
  init: { errors?: Record<string, string[]>; details?: string[]; headers?: Record<string, string> } = {},
): Response {
  // A plain Response, so handlers can mix it with typed success bodies.
  return HttpResponse.json<Schemas['HttpValidationProblemDetails']>(
    { status, title, errorCode, traceId: '00-test-00', ...(init.errors ? { errors: init.errors } : {}),
      ...(init.details ? { details: init.details } : {}),
    },
    { status, headers: { 'Content-Type': 'application/problem+json', ...init.headers } },
  )
}
