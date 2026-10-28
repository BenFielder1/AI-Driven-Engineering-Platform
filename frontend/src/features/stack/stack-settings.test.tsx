import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { sampleComposition } from '../../mocks/handlers/stack-catalog'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'

function ownedWithStack(overrides: Partial<StoredProject> = {}) {
  signInAsOwner()
  const composition = sampleComposition()
  return seedProject({ stack: { composition }, stackPacks: ['fe-react-vite', 'be-aspnet', 'data-efcore-postgres', 'vcs-github'], ...overrides })
}

describe('Settings → Stack (FR-117)', () => {
  it('FR117_settings_stack_shows_the_composition_per_layer_with_pack_and_catalog_versions', async () => {
    const project = ownedWithStack()
    await openSettings('stack', project)

    const current = await screen.findByRole('region', { name: 'Current stack' })
    await waitFor(() => {
      expect(current).toHaveTextContent('React + Vite (frontend)')
    })
    expect(current).toHaveTextContent('ASP.NET Core (backend)')
    expect(current).toHaveTextContent('EF Core + PostgreSQL')
    expect(current).toHaveTextContent('Render Postgres')
    expect(current).toHaveTextContent('Backend engineer')
    expect(within(current).getByRole('list', { name: 'Pack versions' })).toHaveTextContent('ASP.NET Core 1.0.0+aide.1')
    expect(current).toHaveTextContent('Catalog 2026.10.1')
    expect(screen.getByRole('button', { name: 'Change stack' })).toBeInTheDocument()
  })

  it('FR117_an_Owner_changes_the_stack_with_the_same_substeps_pre_filled', async () => {
    const project = ownedWithStack()
    const { user } = await openSettings('stack', project)
    await user.click(await screen.findByRole('button', { name: 'Change stack' }))

    expect(await screen.findByRole('radio', { name: 'React + Vite' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Next layer' }))
    await screen.findByRole('heading', { level: 3, name: 'Backend' })
    expect(screen.getByRole('radio', { name: 'ASP.NET Core' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Next layer' }))
    await user.click(screen.getByRole('button', { name: 'Next layer' }))
    await screen.findByRole('heading', { level: 3, name: 'Version control' })
    // R5: the GitHub repository fixes the VCS layer.
    expect(screen.getByText(/It can only be the repository’s host/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Next layer' }))
    await screen.findByRole('heading', { level: 3, name: 'Deployment' })
    await user.click(within(screen.getByRole('group', { name: 'Frontend host' })).getByRole('radio', { name: 'Netlify' }))
    await user.click(screen.getByRole('button', { name: 'Save stack' }))

    expect(await screen.findByText('Stack saved.')).toBeInTheDocument()
    const put = requestsTo('PUT', '/stack')
    expect(put).toHaveLength(1)
    expect(put[0]?.body).toMatchObject({ frontend: { pack: 'fe-react-vite' }, deploy: { frontendHost: 'deploy-netlify' } })
    // The server sets the VCS from the repository.
    expect(put[0]?.body).not.toHaveProperty('vcs')
    expect(await screen.findByRole('region', { name: 'Current stack' })).toHaveTextContent('Netlify')
  })

  it('FR117_a_locked_stack_says_why_and_offers_no_change', async () => {
    const project = ownedWithStack({ stack: { composition: sampleComposition(), planLocked: true } })
    await openSettings('stack', project)

    expect(await screen.findByText(errorMessages.STACK_LOCKED)).toBeInTheDocument()
    expect(errorMessages.STACK_LOCKED).toBe(
      "The stack can't change after the implementation plan is locked. Start a new version to change it.",
    )
    await screen.findByRole('region', { name: 'Current stack' })
    expect(screen.queryByRole('button', { name: 'Change stack' })).not.toBeInTheDocument()
  })

  it('FR117_stack_change_warning_and_legacy_preset_note', async () => {
    signInAsOwner()
    const project = seedProject({
      stack: {
        composition: null,
        baseHasCode: true,
        layersChanged: ['backend', 'data'],
        legacyPreset: {
          presetId: 'nextjs-frontend',
          presetName: 'nextjs-frontend',
          presetVersion: '1.0.0',
          label: 'Preset nextjs-frontend@1.0.0 (retired, migrated)',
        },
      },
    })
    await openSettings('stack', project)

    expect(await screen.findByText('This version builds on existing code')).toBeInTheDocument()
    expect(screen.getByText('Changed from the base code: Backend, Data.')).toBeInTheDocument()
    expect(screen.getByTestId('legacy-preset')).toHaveTextContent('Preset nextjs-frontend@1.0.0 (retired, migrated)')
  })

  it('FR129_migrated_version_shows_the_server_legacy_label_and_an_unpinned_catalog_hash', async () => {
    signInAsOwner()
    // The FR-129 migration writes the equivalent composition with no catalog hash until the stack is re-pinned.
    const project = seedProject({
      stack: {
        composition: { ...sampleComposition(), catalogHash: null },
        catalogHash: null,
        legacyPreset: {
          presetId: 'nextjs-supabase',
          presetName: 'nextjs-supabase',
          presetVersion: '1.0.1',
          label: 'Preset nextjs-supabase@1.0.1 (retired, migrated)',
        },
      },
    })
    await openSettings('stack', project)

    expect(await screen.findByRole('region', { name: 'Current stack' })).toBeInTheDocument()
    // The label is the server's, as sent (not rebuilt from the name).
    expect(screen.getByTestId('legacy-preset')).toHaveTextContent('Preset nextjs-supabase@1.0.1 (retired, migrated)')
    expect(screen.getByTestId('catalog-hash-unpinned')).toHaveTextContent('no hash yet')
  })

  it('FR117_Contributors_and_Viewers_read_the_stack_without_changing_it', async () => {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const me = signInAs({ email: 'ada@example.com', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: me.id, role: 'viewer' },
      ],
      stack: { composition: sampleComposition() },
    })
    await openSettings('stack', project)

    expect(await screen.findByRole('region', { name: 'Current stack' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Change stack' })).not.toBeInTheDocument()
  })
})

describe('Start new version → Change stack (FR-117)', () => {
  it('FR117_start_version_change_stack_opens_the_substeps_pre_filled_and_sends_stack', async () => {
    signInAsOwner()
    const project = seedProject({ name: 'Shop', stack: { composition: sampleComposition() } })
    seedReleaseVersions(project, { released: '1.1.0', history: ['1.0.0'] })
    await renderRoute(`/projects/${project.id}/release`)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Start new version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Start a new version' })
    await user.type(within(dialog).getByLabelText('What should this version add or fix?'), 'Move to FastAPI')

    await user.click(await within(dialog).findByRole('button', { name: 'Change stack' }))
    expect(await within(dialog).findByRole('radio', { name: 'React + Vite' })).toBeChecked()
    await user.click(within(dialog).getByRole('button', { name: 'Next layer' }))
    await user.click(await within(dialog).findByRole('radio', { name: 'FastAPI' }))
    // The data layer needed ASP.NET Core: it is cleared, and the toast says why.
    expect(await screen.findByText('EF Core + PostgreSQL was cleared: it needs ASP.NET Core')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Next layer' }))
    await user.click(await within(dialog).findByRole('radio', { name: 'SQLAlchemy' }))
    await user.click(within(dialog).getByRole('button', { name: 'Next layer' }))
    await user.click(within(dialog).getByRole('button', { name: 'Next layer' }))
    await within(dialog).findByRole('heading', { level: 3, name: 'Deployment' })
    await user.click(within(dialog).getByRole('button', { name: 'Use this stack' }))

    expect(await within(dialog).findByTestId('start-version-stack')).toHaveTextContent('React + Vite · FastAPI · SQLAlchemy (changed)')
    // FR-129: presets are retired, so the dialog has no preset select.
    expect(within(dialog).queryByLabelText('Tech stack preset')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))

    await waitFor(() => {
      expect(requestsTo('POST', '/versions')).toHaveLength(1)
    })
    const body = requestsTo('POST', '/versions')[0]?.body
    expect(body).toMatchObject({ stack: { backend: { pack: 'be-fastapi' }, data: { pack: 'data-sqlalchemy' } } })
    expect(body).not.toHaveProperty('presetId')
  })
})

describe('Admin → Integrations → Stack catalog (M24, FR-113)', () => {
  it('FR113_admin_stack_catalog_lists_every_pack_with_source_version_and_load_status', async () => {
    signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'admin' })
    await renderRoute('/admin/integrations')
    const panel = await screen.findByRole('region', { name: 'Stack catalog' })

    expect(await within(panel).findByTestId('stack-catalog-version')).toHaveTextContent('2026.10.1')
    const rows = within(panel).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows[0]).toHaveTextContent('Frontend')
    expect(rows[0]).toHaveTextContent('React + Vite')
    expect(rows[0]).toHaveTextContent('fe-react-vite@1.0.0')
    expect(rows[0]).toHaveTextContent('1.0.0+aide.1')
    expect(rows[0]).toHaveTextContent('Loaded')
    expect(rows[2]).toHaveTextContent('Failed')
    expect(rows[2]).toHaveTextContent('pack.json: missing "layer".')
    expect(within(panel).getByText(/^sha256:5f1c/)).toBeInTheDocument()
  })
})

describe('Projects list → stack chips and pack filter (FR-117)', () => {
  it('FR117_projects_list_shows_stack_chips_and_filters_by_pack', async () => {
    signInAsOwner()
    seedProject({ name: 'Shop', stackPacks: ['fe-react-vite', 'be-aspnet'] })
    seedProject({ name: 'Blog', stackPacks: [] })
    const { router } = await renderRoute('/projects')
    const user = userEvent.setup()

    const chips = await screen.findAllByRole('list', { name: 'Shop stack' })
    await waitFor(() => {
      expect(chips[0]).toHaveTextContent('ASP.NET Core')
    })
    expect(screen.getAllByText('No stack').length).toBeGreaterThan(0)

    const filter = screen.getByLabelText('Stack pack')
    await waitFor(() => {
      expect(within(filter).getByRole('option', { name: 'Backend: ASP.NET Core' })).toBeInTheDocument()
    })
    await user.selectOptions(filter, 'be-aspnet')
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ pack: 'be-aspnet' })
    })
    await waitFor(() => {
      expect(screen.queryAllByText('Blog')).toHaveLength(0)
    })
    expect(screen.getAllByText('Shop').length).toBeGreaterThan(0)
  })
})

describe('Activity → STACK_CHANGED (FR-117, FR-155)', () => {
  it('FR117_activity_summary_names_the_changed_layers', () => {
    expect(describeActivity({ type: 'STACK_CHANGED', payload: { layersChanged: ['backend', 'deploy'] }, summary: 's' })).toBe(
      'Stack changed: Backend, Deployment',
    )
    expect(describeActivity({ type: 'STACK_CHANGED', payload: {}, summary: 's' })).toBe('Stack changed')
  })
})
