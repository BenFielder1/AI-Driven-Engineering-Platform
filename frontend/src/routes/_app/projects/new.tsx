import { createFileRoute } from '@tanstack/react-router'

import { NewProjectWizard } from '../../../features/projects/new-project-wizard'

export const Route = createFileRoute('/_app/projects/new')({
  component: NewProjectWizard,
})
