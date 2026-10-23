'use client'

import { RouteErrorView } from '../../features/shell/route-states'

/** Errors inside the signed-in shell: the v2 error state, with the shell kept around it. */
export default function AppError({ error, retry }: { error: unknown; retry: () => void }) {
  return <RouteErrorView error={error} onRetry={retry} inShell />
}
