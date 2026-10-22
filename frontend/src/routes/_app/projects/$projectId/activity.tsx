import { createFileRoute } from '@tanstack/react-router'

import { ActivityPage } from '../../../../features/activity/activity-page'

export const Route = createFileRoute('/_app/projects/$projectId/activity')({
  component: ActivityPage,
})
