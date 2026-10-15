import { createFileRoute } from '@tanstack/react-router'

import { ReleaseStagePage } from '../../../../features/release/release-stage-page'

export const Route = createFileRoute('/_app/projects/$projectId/release')({
  component: ReleaseStagePage,
})
