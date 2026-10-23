import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedArtifact } from '../../mocks/handlers/artifacts'
import { signInAs } from '../../mocks/handlers/auth'
import { seedExecution } from '../../mocks/handlers/execution'
import { projectsDb, seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { currentTimelineStep } from '../../test/overview'
import { currentHref, renderRoute } from '../../test/render'
import { contextualErrorMessages } from '../../lib/errors'

/** The project header (FR-54) around the project's h1. */
function projectHeader(name: string): HTMLElement {
  const header = screen.getByRole('heading', { level: 1, name }).closest('header')
  if (!header) {
    throw new Error('No project header')
  }
  return header
}

function owner() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

/** Released as 1.1.0 (merged), nothing in development. */
function releasedProject(overrides: Partial<StoredProject> = {}) {
  owner()
  const p = seedProject({ name: 'Shop', ...overrides })
  seedReleaseVersions(p, { released: '1.1.0', history: ['1.0.0'] })
  return p
}

async function openRelease(p: StoredProject) {
  const utils = await renderRoute(`/projects/${p.id}/release`)
  await screen.findAllByRole('heading', { level: 2, name: 'Release' })
  return { ...utils, user: userEvent.setup() }
}

describe('Start new version (FR-60, FR-61)', () => {
  it('FR60_start_version_dialog_validates_and_starts_development', async () => {
    const p = releasedProject()
    const { user } = await openRelease(p)

    await user.click(await screen.findByRole('button', { name: 'Start new version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Start a new version' })
    const version = within(dialog).getByLabelText('New version')
    // Prefilled with the next minor of the released version.
    expect(version).toHaveValue('1.2.0')
    expect(within(dialog).getByRole('region', { name: 'What happens' })).toHaveTextContent('based on the released 1.1.0')

    // FR-61 worked example: 1.0.5 isn't greater than 1.1.0 (live, before submitting).
    await user.clear(version)
    await user.type(version, '1.0.5')
    expect(await within(dialog).findByText('The version must be greater than the released version 1.1.0.')).toBeInTheDocument()
    expect(version).toHaveAttribute('aria-invalid', 'true')

    // Invalid semver.
    await user.clear(version)
    await user.type(version, '01.2.0')
    expect(await within(dialog).findByText(/Enter a version as MAJOR\.MINOR\.PATCH/)).toBeInTheDocument()

    // v1.2.0 is accepted and normalised to 1.2.0.
    await user.clear(version)
    await user.type(version, 'v1.2.0')
    await waitFor(() => {
      expect(version).not.toHaveAttribute('aria-invalid')
    })
    expect(within(dialog).getByRole('region', { name: 'What happens' })).toHaveTextContent('tagged v1.2.0')

    // The request is required.
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))
    expect(
      await within(dialog).findAllByText('Describe the new features, improvements or fixes for this version.'),
    ).not.toHaveLength(0)

    await user.type(within(dialog).getByLabelText('What should this version add or fix?'), 'Add CSV export')
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Start a new version' })).not.toBeInTheDocument()
    })
    const body = projectsDb.requests.find((r) => r.method === 'POST' && r.path.endsWith('/versions'))?.body
    expect(body).toEqual({ version: '1.2.0', request: 'Add CSV export' })
    expect(await screen.findByText(/Version 1\.2\.0 is in development/)).toBeInTheDocument()
    // The header now reads "Released 1.1.0" and "In development 1.2.0", in Refinement.
    const header = projectHeader('Shop')
    await waitFor(() => {
      expect(within(header).getByTestId('version-badges')).toHaveTextContent('In development 1.2.0')
    })
    expect(within(header).getByTestId('version-badges')).toHaveTextContent('Released 1.1.0')
    await waitFor(() => {
      expect(currentTimelineStep()).toHaveTextContent('Refinement')
    })
    expect(p.checkpoint).toBe(1)
  })

  it('FR61_server_errors_use_the_error_map', async () => {
    const p = releasedProject({ repoTags: ['v1.2.0'] })
    const { user } = await openRelease(p)

    await user.click(await screen.findByRole('button', { name: 'Start new version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Start a new version' })
    await user.type(within(dialog).getByLabelText('What should this version add or fix?'), 'Add CSV export')
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))

    expect(await within(dialog).findAllByText(contextualErrorMessages.startVersion.TAG_EXISTS)).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('New version')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR60_button_disabled_with_reason_while_a_version_is_in_development', async () => {
    owner()
    const p = seedProject({ name: 'Shop', currentPhase: 'release', status: 'releasing' })
    seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
    const { user } = await openRelease(p)

    const button = await screen.findByRole('button', { name: 'Start new version' })
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(button).toHaveAttribute('title', 'Version 1.2.0 is already in development.')
    expect(button).toHaveAccessibleDescription('Version 1.2.0 is already in development.')
    await user.click(button)
    expect(screen.queryByRole('dialog', { name: 'Start a new version' })).not.toBeInTheDocument()
  })

  it('FR60_button_disabled_with_reason_while_the_release_awaits_merge', async () => {
    owner()
    const p = seedProject({ name: 'Shop', currentPhase: 'release', status: 'releasing' })
    const rows = seedReleaseVersions(p, { released: '1.0.0', development: '1.1.0' })
    const awaiting = rows.find((v) => v.version === '1.1.0')
    if (awaiting) {
      awaiting.status = 'awaiting_merge'
    }
    await openRelease(p)

    const button = await screen.findByRole('button', { name: 'Start new version' })
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(button).toHaveAccessibleDescription(
      'The 1.1.0 release is waiting for its pull request to be merged. Merge it first.',
    )
  })

  it('FR60_button_disabled_with_reason_when_not_released', async () => {
    owner()
    const p = seedProject({ name: 'Shop', currentPhase: 'release', status: 'releasing' })
    seedReleaseVersions(p, { released: '1.0.0' })
    p.status = 'releasing'
    await openRelease(p)

    const button = await screen.findByRole('button', { name: 'Start new version' })
    expect(button).toHaveAccessibleDescription('The project isn’t released yet. Release the version in development first.')
  })

  it('FR60_overview_offers_start_new_version_when_released_and_hides_it_from_contributors', async () => {
    const p = releasedProject()
    await renderRoute(`/projects/${p.id}`)
    const card = await screen.findByRole('region', { name: '1.1.0 is released' })
    expect(within(card).getByRole('button', { name: 'Start new version' })).not.toHaveAttribute('aria-disabled')
  })
})

describe('Version badges, switcher and read-only past versions (FR-62)', () => {
  function developing() {
    owner()
    const p = seedProject({ name: 'Shop', currentPhase: 'specification', status: 'active', checkpoint: 2 })
    const rows = seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0', history: ['1.0.0'] })
    const past = rows.find((v) => v.version === '1.1.0')
    const current = rows.find((v) => v.version === '1.2.0')
    if (!past || !current) {
      throw new Error('seed')
    }
    seedArtifact(p, { type: 'technical_specification', content: '# Spec of 1.1.0', status: 'locked', releaseVersionId: past.id })
    seedArtifact(p, { type: 'technical_specification', content: '# Spec of 1.2.0', releaseVersionId: current.id })
    return { p, past, current }
  }

  it('FR62_header_and_sidebar_show_both_badges', async () => {
    const { p } = developing()
    await renderRoute(`/projects/${p.id}`)

    await screen.findByRole('heading', { level: 1, name: 'Shop' })
    const header = projectHeader('Shop')
    const badges = await within(header).findByTestId('version-badges')
    expect(badges).toHaveTextContent('Released 1.1.0')
    expect(badges).toHaveTextContent('In development 1.2.0')
    expect(within(header).getByText('1.2.0 · checkpoint 2')).toBeInTheDocument()
    const sidebar = screen.getByRole('complementary', { name: 'Sidebar' })
    expect(await within(sidebar).findByTestId('version-badges')).toHaveTextContent('Released 1.1.0In development 1.2.0')
  })

  it('FR62_not_released_badge_and_no_development_badge_when_none', async () => {
    owner()
    const fresh = seedProject({ name: 'Fresh' })
    await renderRoute(`/projects/${fresh.id}`)
    await screen.findByRole('heading', { level: 1, name: 'Fresh' })
    const header = projectHeader('Fresh')
    const badges = await within(header).findByTestId('version-badges')
    expect(badges).toHaveTextContent('Not released')
    expect(badges).toHaveTextContent('In development 0.1.0')
  })

  it('FR62_version_switcher_lists_versions_and_keeps_the_pick_across_pages', async () => {
    const { p } = developing()
    const { router } = await renderRoute(`/projects/${p.id}`)
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Next action' })

    await user.click(await screen.findByRole('button', { name: /^Release version: 1\.2\.0 \(in development\)/ }))
    const items = await screen.findAllByRole('menuitem')
    expect(items.map((i) => i.textContent)).toEqual([
      expect.stringContaining('1.2.0In development · current'),
      expect.stringContaining('1.1.0Released'),
      expect.stringContaining('1.0.0Released'),
    ])
    await user.click(screen.getByRole('menuitem', { name: /^1.1.0/ }))

    await waitFor(() => {
      expect(currentHref(router)).toBe(`/projects/${p.id}?release=1.1.0`)
    })
    expect(await screen.findByTestId('read-only-banner')).toHaveTextContent('Viewing 1.1.0 (released). Read-only')
    // The overview shows the past version, not the current next action.
    expect(screen.queryByRole('heading', { name: 'Next action' })).not.toBeInTheDocument()
    expect(await screen.findByRole('region', { name: 'Version 1.1.0' })).toBeInTheDocument()

    // Moving to another project page keeps the version.
    const sidebar = screen.getByRole('complementary', { name: 'Sidebar' })
    await user.click(within(sidebar).getByRole('link', { name: /Artifacts/ }))
    await waitFor(() => {
      expect(currentHref(router)).toBe(`/projects/${p.id}/artifacts?release=1.1.0`)
    })
    expect(await screen.findByTestId('read-only-banner')).toBeInTheDocument()

    // Back to the current version.
    await user.click(await screen.findByRole('button', { name: 'Back to the current version' }))
    await waitFor(() => {
      expect(currentHref(router)).toBe(`/projects/${p.id}/artifacts`)
    })
    expect(screen.queryByTestId('read-only-banner')).not.toBeInTheDocument()
  })

  it('FR62_past_version_pages_are_read_only_and_query_that_version', async () => {
    const { p } = developing()
    await renderRoute(`/projects/${p.id}/specification?release=1.1.0`)

    expect(await screen.findByTestId('read-only-banner')).toHaveTextContent('Viewing 1.1.0 (released). Read-only')
    expect(await screen.findByRole('article', { name: 'Technical specification v1' })).toHaveTextContent('Spec of 1.1.0')
    expect(screen.queryByRole('button', { name: 'Edit as new draft' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    // No next action in the header either.
    const header = projectHeader('Shop')
    expect(within(header).queryByRole('link', { name: /^(Approve|Advance)/ })).not.toBeInTheDocument()
  })

  it('FR62_current_version_stays_editable', async () => {
    const { p } = developing()
    await renderRoute(`/projects/${p.id}/specification`)

    expect(await screen.findByRole('article', { name: 'Technical specification v2' })).toHaveTextContent('Spec of 1.2.0')
    expect(screen.getByRole('button', { name: 'Edit as new draft' })).toBeInTheDocument()
    expect(screen.queryByTestId('read-only-banner')).not.toBeInTheDocument()
  })

  it('FR62_execution_of_a_past_version_hides_the_controls', async () => {
    owner()
    const p = seedProject({ name: 'Shop', currentPhase: 'execution', status: 'building' })
    seedExecution(p, 2)
    seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
    await renderRoute(`/projects/${p.id}/execution?release=1.1.0`)

    expect(await screen.findByTestId('read-only-banner')).toBeInTheDocument()
    await screen.findByRole('heading', { level: 2, name: 'Execution' })
    expect(screen.queryByRole('button', { name: /Start execution|Pause|Resume/ })).not.toBeInTheDocument()
  })
})
