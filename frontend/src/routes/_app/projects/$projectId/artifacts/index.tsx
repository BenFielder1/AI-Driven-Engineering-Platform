import { createFileRoute } from '@tanstack/react-router'

import { ArtifactsPage } from '../../../../../features/artifacts/artifacts-page'

export const Route = createFileRoute('/_app/projects/$projectId/artifacts/')({
  component: ArtifactsPage,
})
