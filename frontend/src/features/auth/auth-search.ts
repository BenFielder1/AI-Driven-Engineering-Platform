/** /login search params (v2 route validateSearch). */
export interface LoginSearch {
  returnUrl?: string
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export function parseLoginSearch(search: Record<string, unknown>): LoginSearch {
  return { returnUrl: str(search.returnUrl), error: str(search.error), reason: str(search.reason) }
}

/** /register search params. */
export function parseRegisterSearch(search: Record<string, unknown>): { returnUrl?: string } {
  return { returnUrl: str(search.returnUrl) }
}
