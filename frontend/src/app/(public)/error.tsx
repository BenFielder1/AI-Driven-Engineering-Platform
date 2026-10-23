'use client'

import { RouteErrorView } from '../../features/shell/route-states'

export default function PublicError({ error, retry }: { error: unknown; retry: () => void }) {
  return <RouteErrorView error={error} onRetry={retry} />
}
