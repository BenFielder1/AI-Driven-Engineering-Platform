import { createFileRoute } from '@tanstack/react-router'

import { ExecutionPage } from '../../../../features/execution/execution-page'

export const Route = createFileRoute('/_app/projects/$projectId/execution')({
  component: ExecutionPage,
})
