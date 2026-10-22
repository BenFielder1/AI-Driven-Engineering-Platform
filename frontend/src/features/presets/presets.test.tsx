import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages, errorMessages } from '../../lib/errors'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb, seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedRun } from '../../mocks/handlers/runs'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const bodiesOf = (method: string, suffix: string) =>
  projectsDb.requests.filter((r) => r.method === method && r.path.endsWith(suffix)).map((r) => r.body)

function owner() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

/** Pins `presetId` on the project's current (only) release version. */
function pin(p: StoredProject, presetId: string | null, name: string | null = null) {
  for (const v of p.releaseVersions) {
    v.presetId = presetId
    v.presetVersion = presetId ? '1.0.0' : null
    v.presetName = name
  }
}

/** Source (scratch) → idea → Local repository → Version → the Stack step. */
async function wizardToStack() {
  owner()
  renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await next(user)
  await stepHeading(/Step 2 of 8: Project idea/)
  await user.type(screen.getByLabelText('Project name'), 'Storefront')
  await user.type(screen.getByLabelText('Your idea'), 'Sell posters online.')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  return user
}

/** Stack → Configuration → Review & create → Create. */
async function finishWizard(user: User) {
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  await next(user)
  await stepHeading(/Step 8 of 8: Review & create/)
  const summary = screen.getByRole('region', { name: 'Stack' })
  await user.click(screen.getByRole('button', { name: 'Create project' }))
  await screen.findByRole('heading', { level: 1, name: 'Storefront' })
  return summary
}

describe('Wizard → Stack step (FR-74)', () => {
  it('FR74_wizard_stack_step_shows_preset_cards_and_sends_presetId', async () => {
    const user = await wizardToStack()

    // "No preset (general)" is the default; each card shows name, version, description, stack chips and targets.
    expect(screen.getByRole('radio', { name: 'No preset (general)' })).toBeChecked()
    const card = await screen.findByRole('radio', { name: /Next\.js frontend/ })
    expect(card).toHaveAccessibleName('Next.js frontend 1.0.0')
    expect(card).toHaveAccessibleDescription(/Frontend web app on Next\.js/)
    const stack = screen.getByRole('list', { name: 'Next.js frontend stack' })
    expect(within(stack).getByText('Tailwind')).toBeInTheDocument()
    expect(screen.getAllByText('Deploys to: Vercel')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /Next\.js \+ Supabase/ })).toBeInTheDocument()

    // Picking a preset shows its specialist agents and extra spec sections.
    await user.click(card)
    const brings = await screen.findByRole('region', { name: 'What the preset brings' })
    expect(await within(brings).findByText('UI engineer')).toBeInTheDocument()
    expect(within(brings).getByText('ui-engineer')).toBeInTheDocument()
    expect(within(brings).getByText('Routes & Rendering Strategy')).toBeInTheDocument()

    const summary = await finishWizard(user)
    expect(summary).toHaveTextContent('Next.js frontend 1.0.0')
    expect(bodiesOf('POST', '/api/projects')[0]).toMatchObject({ presetId: 'nextjs-frontend' })
    // The new project's header carries the preset chip.
    const header = screen.getByRole('heading', { level: 1, name: 'Storefront' }).closest('header') ?? document.body
    expect(await within(header).findByTestId('preset-chip')).toHaveTextContent('Next.js frontend 1.0.0')
  })

  it('FR74_wizard_no_preset_omits_presetId', async () => {
    const user = await wizardToStack()
    await screen.findByRole('radio', { name: /Next\.js frontend/ })

    const summary = await finishWizard(user)

    expect(summary).toHaveTextContent('No preset (general)')
    expect(bodiesOf('POST', '/api/projects')[0]).not.toHaveProperty('presetId')
  })

  it('FR74_wizard_preset_not_found_returns_to_the_stack_step', async () => {
    const user = await wizardToStack()
    await user.click(await screen.findByRole('radio', { name: /Next\.js \+ Supabase/ }))
    server.use(http.post('*/api/projects', () => problem(400, 'PRESET_NOT_FOUND', 'Unknown preset.')))
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 5 of 8: Stack/)).toBeInTheDocument()
    expect(screen.getAllByText(contextualErrorMessages.createProject.PRESET_NOT_FOUND)).not.toHaveLength(0)
  })
})

describe('Projects list → preset chip and filter (FR-74, SPEC §7.2)', () => {
  it('FR74_projects_list_shows_the_preset_chip_and_filters_by_preset', async () => {
    owner()
    const withPreset = seedProject({ name: 'Storefront' })
    pin(withPreset, 'nextjs-frontend', 'Next.js frontend')
    seedProject({ name: 'Inventory' })
    const { router } = renderRoute('/projects')
    const user = userEvent.setup()

    // Below md the rows are cards (DataTable); each carries the chip.
    const card = (name: string) =>
      within(screen.getByRole('list', { name: 'Projects' }))
        .queryAllByRole('listitem')
        .find((li) => li.textContent.includes(name))
    await screen.findByRole('list', { name: 'Projects' })
    expect(within(card('Storefront') ?? document.body).getByTestId('preset-chip')).toHaveTextContent('Next.js frontend 1.0.0')
    expect(within(card('Inventory') ?? document.body).getByTestId('preset-chip')).toHaveTextContent('No preset')

    const filter = screen.getByLabelText('Preset')
    await screen.findByRole('option', { name: 'Next.js frontend' })
    await user.selectOptions(filter, 'nextjs-frontend')

    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ preset: 'nextjs-frontend' })
    })
    await waitFor(() => {
      expect(card('Inventory')).toBeUndefined()
    })
    expect(card('Storefront')).toBeDefined()

    // "No preset" sends preset=none: only projects without one.
    await user.selectOptions(filter, 'No preset')
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ preset: 'none' })
    })
    await waitFor(() => {
      expect(card('Storefront')).toBeUndefined()
    })
    expect(card('Inventory')).toBeDefined()
  })
})

describe('Settings → Stack: change preset (FR-74, SPEC §2)', () => {
  async function openGeneral(p: StoredProject) {
    renderRoute(`/projects/${p.id}/settings/general`)
    const section = await screen.findByRole('region', { name: 'Stack' })
    return { section, user: userEvent.setup() }
  }

  it('FR74_owner_changes_the_preset_until_the_plan_is_locked', async () => {
    owner()
    const p = seedProject({ name: 'Storefront' })
    const { section, user } = await openGeneral(p)
    expect(section).toHaveTextContent('No preset (general)')

    const select = within(section).getByLabelText('Preset')
    await within(section).findByRole('option', { name: 'Next.js + Supabase 1.0.0' })
    await user.selectOptions(select, 'nextjs-supabase')
    expect(await within(section).findByText('Database engineer')).toBeInTheDocument()
    await user.click(within(section).getByRole('button', { name: 'Change preset' }))

    expect(await screen.findByText('Preset changed to Next.js + Supabase.')).toBeInTheDocument()
    expect(bodiesOf('PATCH', '/preset')).toEqual([{ presetId: 'nextjs-supabase' }])
    await waitFor(() => {
      expect(within(section).getByTestId('preset-chip')).toHaveTextContent('Next.js + Supabase 1.0.0')
    })

    // Back to no preset: an explicit null.
    await user.selectOptions(select, '')
    await user.click(within(section).getByRole('button', { name: 'Change preset' }))
    await waitFor(() => {
      expect(bodiesOf('PATCH', '/preset')).toEqual([{ presetId: 'nextjs-supabase' }, { presetId: null }])
    })
  })

  it('FR74_preset_locked: a locked plan disables the change with the reason', async () => {
    owner()
    const p = seedProject({ name: 'Storefront', currentPhase: 'execution' })
    seedArtifact(p, { type: 'implementation_plan', content: '# Plan', status: 'locked' })
    const { section } = await openGeneral(p)

    const select = within(section).getByLabelText('Preset')
    await waitFor(() => {
      expect(select).toBeDisabled()
    })
    expect(select).toHaveAccessibleDescription(errorMessages.PRESET_LOCKED)
    expect(within(section).getByRole('button', { name: 'Change preset' })).toBeDisabled()
  })

  it('FR74_preset_locked: a 409 from the server shows the PRESET_LOCKED copy', async () => {
    owner()
    const p = seedProject({ name: 'Storefront' })
    server.use(http.patch('*/api/projects/:id/preset', () => problem(409, 'PRESET_LOCKED', 'Locked.')))
    const { section, user } = await openGeneral(p)

    await within(section).findByRole('option', { name: 'Next.js frontend 1.0.0' })
    await user.selectOptions(within(section).getByLabelText('Preset'), 'nextjs-frontend')
    await user.click(within(section).getByRole('button', { name: 'Change preset' }))

    await waitFor(() => {
      expect(within(section).getByLabelText('Preset')).toHaveAccessibleDescription(errorMessages.PRESET_LOCKED)
    })
    expect(within(section).getByLabelText('Preset')).toBeDisabled()
  })

  it('FR74_viewer_sees_the_preset_read_only', async () => {
    const p = seedProjectAs('viewer', (x) => {
      pin(x, 'nextjs-frontend', 'Next.js frontend')
    })
    const { section } = await openGeneral(p)

    expect(within(section).getByTestId('preset-chip')).toHaveTextContent('Next.js frontend 1.0.0')
    expect(section).toHaveTextContent('Only project Owners change the preset.')
    expect(within(section).queryByLabelText('Preset')).not.toBeInTheDocument()
    expect(within(section).queryByRole('button', { name: 'Change preset' })).not.toBeInTheDocument()
  })
})

describe('Start new version → preset select (FR-60, FR-74)', () => {
  async function openDialog(presetId: string | null) {
    owner()
    const p = seedProject({ name: 'Shop' })
    const [released] = seedReleaseVersions(p, { released: '1.1.0' })
    if (released && presetId) {
      released.presetId = presetId
      released.presetVersion = '1.0.0'
      released.presetName = 'Next.js frontend'
    }
    renderRoute(`/projects/${p.id}/release`)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Start new version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Start a new version' })
    await user.type(within(dialog).getByLabelText('What should this version add or fix?'), 'Add CSV export')
    return { dialog, user }
  }

  const start = async (dialog: HTMLElement, user: User) => {
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Start a new version' })).not.toBeInTheDocument()
    })
    return bodiesOf('POST', '/versions')[0]
  }

  it('FR74_start_version_keeps_the_preset_by_omitting_presetId', async () => {
    const { dialog, user } = await openDialog('nextjs-frontend')
    const select = within(dialog).getByLabelText('Tech stack preset')
    expect(select).toHaveDisplayValue('Keep the current preset (Next.js frontend 1.0.0)')

    expect(await start(dialog, user)).toEqual({ version: '1.2.0', request: 'Add CSV export' })
  })

  it('FR74_start_version_with_another_preset_or_none', async () => {
    const { dialog, user } = await openDialog('nextjs-frontend')
    const select = within(dialog).getByLabelText('Tech stack preset')
    await within(dialog).findByRole('option', { name: 'Next.js + Supabase 1.0.0' })
    // The current preset isn't offered twice; "No preset" is.
    expect(within(dialog).queryByRole('option', { name: 'Next.js frontend 1.0.0' })).not.toBeInTheDocument()
    await user.selectOptions(select, 'No preset (general)')

    expect(await start(dialog, user)).toEqual({ version: '1.2.0', request: 'Add CSV export', presetId: null })
  })

  it('FR74_start_version_picks_a_preset_for_a_project_without_one', async () => {
    const { dialog, user } = await openDialog(null)
    const select = within(dialog).getByLabelText('Tech stack preset')
    expect(select).toHaveDisplayValue('Keep the current preset (No preset (general))')
    await within(dialog).findByRole('option', { name: 'Next.js + Supabase 1.0.0' })
    await user.selectOptions(select, 'nextjs-supabase')

    expect(await start(dialog, user)).toEqual({ version: '1.2.0', request: 'Add CSV export', presetId: 'nextjs-supabase' })
  })
})

describe('Planning → specialist agents and preset warnings (FR-76)', () => {
  it('FR76_planning_names_the_specialist_agents_and_lists_agent_scope_and_plan_order_warnings', async () => {
    const block = {
      format: 1,
      steps: [
        { number: 1, title: 'Scaffold', agent: 'project-scaffolder', covers: ['FR-1'], files: [{ path: 'package.json', action: 'create' }], dependencies: [] },
        { number: 2, title: 'Pages', agent: 'ui-engineer', covers: ['FR-2'], files: [{ path: 'lib/db.ts', action: 'create' }], dependencies: [1] },
        { number: 3, title: 'Odd', agent: 'ml-engineer', covers: [], files: [{ path: 'x.py', action: 'create' }], dependencies: [1] },
      ],
    }
    const content = `# Plan\n\n\`\`\`aide-plan\n${JSON.stringify(block)}\n\`\`\`\n`
    const project = seedProjectAs('viewer', (p) => {
      p.currentPhase = 'planning'
      pin(p, 'nextjs-frontend', 'Next.js frontend')
      seedArtifact(p, {
        type: 'implementation_plan',
        content,
        warnings: [
          { code: 'UNCOVERED_REQUIREMENTS', message: 'FR-3 is not covered.', ids: ['FR-3'] },
          { code: 'AGENT_SCOPE', message: 'Step 2 (ui-engineer) declares lib/db.ts outside its owned paths.', ids: ['2: lib/db.ts'] },
          { code: 'PLAN_ORDER', message: 'lib/ steps should come before app/ steps.', ids: ['2'] },
        ],
      })
    })
    renderRoute(`/projects/${project.id}/planning`)
    await screen.findByRole('heading', { level: 2, name: 'Planning' })

    const table = await screen.findByRole('table', { name: /Plan steps/ })
    const pages = within(table).getByRole('row', { name: /^2 Pages/ })
    expect(await within(pages).findByText('UI engineer')).toBeInTheDocument()
    expect(within(pages).getByText('ui-engineer')).toBeInTheDocument()
    expect(within(table).getByRole('row', { name: /^1 Scaffold/ })).toHaveTextContent('Project scaffolder')
    // A key the catalogue doesn't list is shown as the key.
    expect(within(table).getByRole('row', { name: /^3 Odd/ })).toHaveTextContent('ml-engineer')

    expect(screen.getByText('Uncovered requirements')).toBeInTheDocument()
    expect(screen.getByText("Files outside the agent's owned paths")).toBeInTheDocument()
    expect(screen.getByText(errorMessages.AGENT_SCOPE)).toBeInTheDocument()
    expect(screen.getByText('Step 2 (ui-engineer) declares lib/db.ts outside its owned paths.')).toBeInTheDocument()
    expect(screen.getByText('Plan order')).toBeInTheDocument()
    expect(screen.getByText(errorMessages.PLAN_ORDER)).toBeInTheDocument()
  })
})

describe('Admin → Integrations → Presets (FR-74)', () => {
  it('FR74_admin_presets_list_with_detail_sheet', async () => {
    signInAs({ systemRole: 'admin' })
    renderRoute('/admin/integrations')
    const user = userEvent.setup()
    const panel = await screen.findByRole('region', { name: 'Presets' })

    const list = await within(panel).findByRole('list', { name: 'Presets' })
    const frontend = within(list).getAllByRole('listitem').find((li) => li.textContent.includes('Next.js frontend'))
    if (!frontend) {
      throw new Error('No Next.js frontend preset')
    }
    expect(frontend).toHaveTextContent('1.0.0')
    expect(frontend).toHaveTextContent('Deploys to:Vercel')
    await waitFor(() => {
      expect(frontend).toHaveTextContent('Specialist agents:3')
    })

    await user.click(within(frontend).getByRole('button', { name: 'View details of Next.js frontend' }))
    const sheet = await screen.findByRole('dialog', { name: 'Next.js frontend 1.0.0' })
    expect(await within(sheet).findByText('API engineer (BFF)')).toBeInTheDocument()
    expect(within(sheet).getByRole('region', { name: 'Review checklist' })).toHaveTextContent('No secret env names')
    expect(within(sheet).getByRole('region', { name: 'Protected paths' })).toHaveTextContent('.github/workflows/**')
    expect(within(sheet).getByRole('region', { name: 'Extra specification sections' })).toHaveTextContent(
      'Routes & Rendering Strategy',
    )
  })
})

describe('Activity and runs (FR-74, FR-92)', () => {
  it('FR74_preset_changed_activity_summary', () => {
    expect(
      describeActivity({
        type: 'PRESET_CHANGED',
        summary: 'server',
        payload: { version: '1.2.0', fromPresetId: null, fromPresetVersion: null, presetId: 'nextjs-frontend', presetVersion: '1.0.0' },
      }),
    ).toBe('Preset of 1.2.0 changed from no preset to nextjs-frontend@1.0.0')
  })

  it('FR74_run_detail_records_the_preset', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, { agent: 'code_generator', agentKey: 'ui-engineer', presetId: 'nextjs-frontend', presetVersion: '1.0.0' })
    })
    renderRoute(`/projects/${project.id}/runs`)
    expect(await screen.findByTestId('run-preset')).toHaveTextContent('nextjs-frontend@1.0.0')
  })
})
