import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedPullRequest } from '../../mocks/handlers/execution'
import type { StoredProject } from '../../mocks/handlers/projects'
import { problem } from '../../mocks/handlers/problem'
import { seedRelease, seedReleasePhase } from '../../mocks/handlers/release'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'

const PR_URL = 'https://bitbucket.org/acme/todo-app/pull-requests/3'
const TAG_URL = 'https://bitbucket.org/acme/todo-app/src/v0.1.0'
const NOTES = '## What changed\n\n- Shared lists\n- <script>alert(1)</script> stays text\n\n[Docs](javascript:alert(1))'

function releasedOn(provider: 'bitbucket' | 'github_app', notes: string | null) {
  return seedProjectAs('owner', (p: StoredProject) => {
    seedReleasePhase(p)
    seedPullRequest(p, {
      provider,
      number: 3,
      url: provider === 'bitbucket' ? PR_URL : 'https://github.com/acme/inventory/pull/3',
    })
    p.status = 'released'
    const version = p.releaseVersions[0]
    if (version) {
      version.status = 'released'
    }
    seedRelease(p, {
      tag: 'v0.1.0',
      pullRequestUrl: provider === 'bitbucket' ? PR_URL : 'https://github.com/acme/inventory/pull/3',
      githubReleaseUrl: provider === 'bitbucket' ? TAG_URL : 'https://github.com/acme/inventory/releases/tag/v0.1.0',
      notes,
    })
  })
}

describe('Release page on Bitbucket (FR-140)', () => {
  it('FR140_bitbucket_release_tag_and_notes: links "Release tag v0.1.0" to the tag page and shows the full notes safely', async () => {
    const project = releasedOn('bitbucket', NOTES)
    await renderRoute(`/projects/${project.id}/release`)

    const section = await screen.findByRole('region', { name: 'Release tag' })
    await waitFor(() => {
      expect(within(section).getByRole('link', { name: 'Release tag v0.1.0' })).toHaveAttribute('href', TAG_URL)
    })
    const notes = await within(section).findByRole('article', { name: 'Release notes for v0.1.0' })
    expect(within(notes).getByRole('heading', { name: 'What changed' })).toBeInTheDocument()
    expect(within(notes).getByText('Shared lists')).toBeInTheDocument()
    // Raw HTML is never rendered, and unsafe URLs are removed.
    expect(notes.querySelector('script')).toBeNull()
    expect(within(notes).getByText('Docs').closest('a')?.getAttribute('href') ?? '').not.toMatch(/javascript:/)

    // The release panel names the tag page, not a host release.
    const panel = document.getElementById('release')
    expect(panel).not.toBeNull()
    if (panel) {
      expect(within(panel).getByRole('link', { name: 'View the tag on Bitbucket' })).toHaveAttribute('href', TAG_URL)
    }
  })

  it('FR140_notes_not_fetched_for_host_releases: a GitHub release links its Release and never asks for AIDE notes', async () => {
    let notesRequests = 0
    server.use(
      http.get('*/api/projects/:id/releases/:releaseId/notes', () => {
        notesRequests += 1
        return problem(404, 'NOT_FOUND', 'Not expected.')
      }),
    )
    const project = releasedOn('github_app', 'Fixed the login bug.')
    await renderRoute(`/projects/${project.id}/release`)

    const section = await screen.findByRole('region', { name: 'GitHub Release' })
    await within(section).findByRole('link', { name: 'View the GitHub Release for v0.1.0' })
    expect(within(section).queryByTestId('release-notes')).not.toBeInTheDocument()
    expect(notesRequests).toBe(0)
  })

  it('FR140_notes_error: notes that fail to load show an error with Try again; the tag link stays', async () => {
    server.use(
      http.get('*/api/projects/:id/releases/:releaseId/notes', () => problem(404, 'NOT_FOUND', 'No such release.')),
    )
    const project = releasedOn('bitbucket', NOTES)
    await renderRoute(`/projects/${project.id}/release`)

    const section = await screen.findByRole('region', { name: 'Release tag' })
    expect(await within(section).findByText("The release notes couldn't load")).toBeInTheDocument()
    expect(within(section).getByRole('link', { name: 'Release tag v0.1.0' })).toHaveAttribute('href', TAG_URL)
  })

  it('FR140_no_notes: without notes there is no notes block', async () => {
    const project = releasedOn('bitbucket', null)
    await renderRoute(`/projects/${project.id}/release`)

    const section = await screen.findByRole('region', { name: 'Release tag' })
    await within(section).findByRole('link', { name: 'Release tag v0.1.0' })
    expect(within(section).queryByTestId('release-notes')).not.toBeInTheDocument()
  })
})
