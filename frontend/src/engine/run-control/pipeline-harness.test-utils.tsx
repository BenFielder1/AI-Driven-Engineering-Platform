import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'

import type { ProjectRole } from '../../lib/permissions'
import { resetRunControlDb, runControlDefinitionHandlers } from '../../mocks/handlers/engine-run-control'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderWithProviders } from '../../test/render'
import { PipelineView } from '../pages/pipeline-view'
import type { PipelineAccess } from './access'

/** The engine Pipeline tab as a host renders it, with `?run=` held in state (landing on a started run sets it). */
function Harness({ projectId, access }: { projectId: string; access: PipelineAccess }) {
  const [runId, setRunId] = useState<string | undefined>(undefined)
  return (
    <PipelineView
      projectId={projectId}
      runId={runId}
      onRunChange={setRunId}
      replayLink={() => null}
      runLink={(id, label) => (
        <button
          type="button"
          onClick={() => {
            setRunId(id)
          }}
        >
          {label}
        </button>
      )}
      documentTitle="Pipeline"
      access={access}
    />
  )
}

/** The 0.8.0 registry (engine-controlled pipelines, input schemas, triggers) and a clean run-control store. */
export function installRunControlMocks() {
  resetRunControlDb()
  server.use(...runControlDefinitionHandlers)
}

/** A signed-in non-admin member of a new project (the first seeded user is an admin, so one is seeded first). */
export function memberOf(role: ProjectRole, project: Partial<StoredProject> = {}) {
  seedUser({ email: 'admin@example.com', name: 'Admin' })
  const user = signInAs({ email: 'member@example.com', name: 'Member' })
  const stored = seedProject({ name: 'Project', ...project, members: [{ userId: user.id, role }] })
  return { user, project: stored, access: { role, userId: user.id } satisfies PipelineAccess }
}

export async function renderPipeline(projectId: string, access: PipelineAccess) {
  const utils = renderWithProviders(<Harness projectId={projectId} access={access} />)
  await screen.findByRole('heading', { level: 2, name: 'Pipeline' })
  return { ...utils, user: userEvent.setup() }
}
