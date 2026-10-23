'use client'

import { RouteErrorView } from '../features/shell/route-states'

/** The v2 root error state ("Something went wrong" with Retry). */
export default function RootError({ error, retry }: { error: unknown; retry: () => void }) {
  return <RouteErrorView error={error} onRetry={retry} />
}
