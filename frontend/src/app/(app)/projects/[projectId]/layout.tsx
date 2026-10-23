'use client'

import type { ReactNode } from 'react'

import { ProjectLayout } from '../../../../features/projects/project-layout'
import { useParams } from '../../../../lib/navigation'

/**
 * /projects/:id layout. FR-62: `?release=<semver>` picks a past release version (read by useSelectedReleaseVersion);
 * it stays on screen while moving between the project's pages (`retainedSearchParams` in src/lib/navigation).
 */
export default function ProjectRouteLayout({ children }: { children: ReactNode }) {
  const { projectId } = useParams<{ projectId: string }>()
  return <ProjectLayout projectId={projectId}>{children}</ProjectLayout>
}
