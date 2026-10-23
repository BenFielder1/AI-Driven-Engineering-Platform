import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedArtifact } from '../../mocks/handlers/artifacts'
import { server } from '../../mocks/server'
import { openArtifacts, seedProjectAs, specV1, specV2 } from '../../test/artifacts'
import { renderRoute } from '../../test/render'

function card(name: string) {
  const heading = within(screen.getByRole('list', { name: 'Artifact types' })).getByRole('heading', { name })
  const section = heading.closest('section')
  if (!section) {
    throw new Error(`No card for ${name}`)
  }
  return section
}

describe('Artifacts page (FR-16, FR-20, FR-21)', () => {
  it('FR-21: shows a card per type with the latest version, status and integrity, and empty types', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifact(p, { type: 'project_request', content: '# Request', status: 'locked' })
      seedArtifact(p, { type: 'technical_specification', content: specV1, status: 'locked' })
      seedArtifact(p, { type: 'technical_specification', content: specV2 })
    })
    await openArtifacts(project)

    const request = card('Project request')
    expect(within(request).getByText('Version 1')).toBeInTheDocument()
    expect(within(request).getByText('Approved')).toBeInTheDocument()
    expect(within(request).getByText('Integrity verified')).toBeInTheDocument()

    const spec = card('Technical specification')
    expect(within(spec).getByText('Version 2')).toBeInTheDocument()
    expect(within(spec).getByText('Draft')).toBeInTheDocument()
    expect(within(spec).getByRole('link', { name: 'Open technical specification' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/artifacts/technical_specification`,
    )

    for (const name of ['Implementation plan', 'Optimisation plan']) {
      expect(within(card(name)).getByText('No versions yet')).toBeInTheDocument()
      expect(within(card(name)).queryByRole('link')).not.toBeInTheDocument()
    }
  })

  it('FR-20: a corrupted latest version is clearly flagged on its card', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'implementation_plan', content: '# Plan', status: 'locked', integrity: 'corrupted' })
    })
    await openArtifacts(project)

    const plan = card('Implementation plan')
    expect(within(plan).getByText('Integrity check failed')).toBeInTheDocument()
    expect(within(plan).getByText('Corrupted')).toBeInTheDocument()
    // Agent-authored and the latest version: the server requested regeneration.
    expect(within(plan).getByText(/no longer matches the stored SHA-256 hash.*regeneration was requested/)).toBeInTheDocument()
    // The read raised the integrity alert (FR-20).
    expect(project.events.some((e) => e.type === 'INTEGRITY_ALERT' && e.category === 'integrity')).toBe(true)
  })

  it('FR-20: a corrupted human edit says to replace it, not that regeneration was requested', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'implementation_plan', content: '# Plan', integrity: 'corrupted', authorUserId: p.members[0]?.userId ?? '' })
    })
    await openArtifacts(project)

    const plan = card('Implementation plan')
    expect(within(plan).getByText(/no longer matches the stored SHA-256 hash.*Edit and save a new draft to replace it\./)).toBeInTheDocument()
    expect(within(plan).queryByText(/regeneration/)).not.toBeInTheDocument()
  })

  it('shows an error with retry when the artifacts fail to load', async () => {
    const project = seedProjectAs('owner')
    server.use(http.get('*/api/projects/:id/artifacts', () => new HttpResponse(null, { status: 500 })))
    await renderRoute(`/projects/${project.id}/artifacts`)
    const user = userEvent.setup()

    expect(await screen.findByText("The artifacts couldn't load", {}, { timeout: 5000 })).toBeInTheDocument()
    server.resetHandlers()
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('list', { name: 'Artifact types' })).toBeInTheDocument()
  })
})
