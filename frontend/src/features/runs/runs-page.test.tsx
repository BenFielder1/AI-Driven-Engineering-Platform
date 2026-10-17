import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { ProjectRole } from '../../lib/permissions'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedRun } from '../../mocks/handlers/runs'
import { server } from '../../mocks/server'
import { projectEvent } from '../../test/fake-live'
import { currentHref, renderRoute } from '../../test/render'

function seedProjectAs(role: ProjectRole, setup?: (p: StoredProject) => void): StoredProject {
  const members: { userId: string; role: ProjectRole }[] = []
  if (role !== 'owner') {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    members.push({ userId: owner.id, role: 'owner' })
  }
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  members.push({ userId: me.id, role })
  const project = seedProject({ currentPhase: 'specification', members })
  setup?.(project)
  return project
}

/** /projects/:id/runs, waiting until the first page has loaded. */
async function openRuns(project: StoredProject, query = '') {
  const utils = renderRoute(`/projects/${project.id}/runs${query}`)
  // hidden: a deep-linked run opens the modal sheet, which hides the page behind it from assistive tech.
  await screen.findByRole('heading', { name: 'Agent runs', hidden: true })
  await waitFor(() => {
    expect(screen.queryByText('Loading runs…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup(), project }
}

function table() {
  return screen.getByRole('table', { name: /Agent runs, newest first/ })
}

/** Body rows (the header row is excluded). */
function rows() {
  return within(table()).getAllByRole('row').slice(1)
}

function rowFor(agentLabel: string, index = 0) {
  const row = rows().filter((r) => within(r).queryByRole('button', { name: new RegExp(`^${agentLabel} run`) }))[index]
  if (!row) {
    throw new Error(`No row for ${agentLabel}`)
  }
  return row
}

describe('Agent runs page (FR-23)', () => {
  it('FR23_ table: lists runs newest first with agent, model, provider, tokens, cost, duration and status', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, { agent: 'idea_refinement', model: 'claude-sonnet-5-5', inputTokens: 812, outputTokens: 1_530, costEstimate: 0.0169, durationMs: 9_400 })
      seedRun(p, { agent: 'spec_generator' })
      seedRun(p, { agent: 'plan_generator', provider: 'fake', model: 'fake-1', status: 'running', inputTokens: 0, outputTokens: 0, costEstimate: 0, durationMs: 0 })
    })
    await openRuns(project)

    expect(screen.getByText('Showing 1–3 of 3 runs')).toBeInTheDocument()
    const headers = within(table()).getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toEqual([
      'Agent',
      'Specialist',
      'Preset',
      'Model',
      'Provider',
      'Credential',
      'Version',
      'Tokens in / out',
      'Tool calls',
      'Est. cost',
      'Duration',
      'Status',
      'Started',
    ])
    expect(rows().map((r) => within(r).getByRole('button').textContent)).toEqual([
      expect.stringMatching(/^Plan generator/),
      expect.stringMatching(/^Spec generator/),
      expect.stringMatching(/^Idea refinement/),
    ])

    const spec = rowFor('Spec generator')
    expect(within(spec).getByText('claude-opus-5-5')).toBeInTheDocument()
    expect(within(spec).getByText('Anthropic')).toBeInTheDocument()
    expect(within(spec).getByText('3,412 / 9,876')).toBeInTheDocument()
    expect(within(spec).getByText('$0.211')).toBeInTheDocument()
    expect(within(spec).getByText('48.3 s')).toBeInTheDocument()
    expect(within(spec).getByText('Succeeded')).toBeInTheDocument()

    expect(within(rowFor('Idea refinement')).getByText('$0.0169')).toBeInTheDocument()

    // §11 Q3: nothing streams; a running run just says so, with no partial counts.
    const running = rowFor('Plan generator')
    expect(within(running).getAllByText('Running…')).toHaveLength(2)
    expect(within(running).getByText('Fake (testing)')).toBeInTheDocument()
    // Tokens and cost, plus the empty specialist, preset and credential cells of a base-agent run with no recorded key.
    expect(within(running).getAllByText('—')).toHaveLength(5)
  })

  it('FR23_ filter: the agent filter shows only that agent and is kept in the URL', async () => {
    const project = seedProjectAs('contributor', (p) => {
      seedRun(p, { agent: 'idea_refinement' })
      seedRun(p, { agent: 'idea_refinement' })
      seedRun(p, { agent: 'spec_generator' })
    })
    const { user, router } = await openRuns(project)
    expect(rows()).toHaveLength(3)

    await user.selectOptions(screen.getByLabelText('Agent'), 'Idea refinement')

    await waitFor(() => {
      expect(screen.getByText('Showing 1–2 of 2 runs')).toBeInTheDocument()
    })
    expect(rows().every((r) => within(r).queryByText('Idea refinement'))).toBe(true)
    expect(currentHref(router)).toContain('agent=idea_refinement')

    await user.selectOptions(screen.getByLabelText('Agent'), 'Code review')
    expect(await screen.findByText('No code review runs yet')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Show all agents' }))
    expect(await screen.findByText('Showing 1–3 of 3 runs')).toBeInTheDocument()
    expect(screen.getByLabelText('Agent')).toHaveValue('')
  })

  it('FR23_ paging: 20 runs per page with previous and next', async () => {
    const project = seedProjectAs('viewer', (p) => {
      for (let i = 0; i < 25; i += 1) {
        seedRun(p, { agent: 'code_generator', provider: 'ollama', model: 'qwen2.5-coder' })
      }
    })
    const { user } = await openRuns(project)

    expect(screen.getByText('Showing 1–20 of 25 runs')).toBeInTheDocument()
    expect(rows()).toHaveLength(20)
    const pagination = screen.getByRole('navigation', { name: 'Pagination' })
    expect(within(pagination).getByText('Page 1 of 2')).toBeInTheDocument()
    expect(within(pagination).getByRole('button', { name: 'Previous page' })).toBeDisabled()

    await user.click(within(pagination).getByRole('button', { name: 'Next page' }))

    expect(await screen.findByText('Showing 21–25 of 25 runs')).toBeInTheDocument()
    expect(rows()).toHaveLength(5)
    expect(within(rows()[0] ?? table()).getByText('qwen2.5-coder')).toBeInTheDocument()
  })

  it('FR23_ drawer: a completed spec generation shows model, tokens, duration and the project request version it used', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, {
        agent: 'spec_generator',
        inputArtifacts: [{ type: 'project_request', version: 2 }],
        output: '# Inventory tracker: technical specification\n\n## 1. Overview',
      })
    })
    const { user } = await openRuns(project)
    const opener = within(rowFor('Spec generator')).getByRole('button')

    await user.click(opener)

    const drawer = await screen.findByRole('dialog', { name: 'Spec generator run' })
    expect(drawer).toHaveAttribute('aria-modal', 'true')
    expect(within(drawer).getByRole('button', { name: 'Close' })).toHaveFocus()

    const field = (term: string) => within(drawer).getByText(term, { selector: 'dt' }).nextElementSibling
    expect(field('Model')).toHaveTextContent('claude-opus-5-5')
    expect(field('Provider')).toHaveTextContent('Anthropic')
    expect(field('Phase')).toHaveTextContent('Specification')
    expect(field('Prompt template')).toHaveTextContent('Spec generator v1')
    expect(field('Attempts')).toHaveTextContent('1')
    expect(field('Input tokens')).toHaveTextContent('3,412')
    expect(field('Output tokens')).toHaveTextContent('9,876')
    expect(field('Total tokens')).toHaveTextContent('13,288')
    expect(field('Estimated cost')).toHaveTextContent('$0.211')
    expect(field('Duration')).toHaveTextContent('48.3 s')
    expect(within(drawer).getByRole('link', { name: 'Project request v2' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/artifacts/project_request?version=2`,
    )
    const output = await within(drawer).findByLabelText('Run output')
    expect(output.tagName).toBe('PRE')
    expect(output).toHaveTextContent('# Inventory tracker: technical specification')
    expect(within(drawer).queryByText(/truncated/)).not.toBeInTheDocument()

    // Focus is trapped, Escape closes and focus returns to the row that opened it.
    await user.tab({ shift: true })
    expect(drawer).toContainElement(document.activeElement as HTMLElement)
    await user.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(within(rowFor('Spec generator')).getByRole('button')).toHaveFocus()
  })

  it('FR23_ drawer: a failed run shows its mapped error, attempts and the feedback given to the agent', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, {
        agent: 'idea_refinement',
        status: 'failed',
        attempts: 4,
        error: 'PROVIDER_ERROR: anthropic returned 503 Service Unavailable.',
        userFeedback: 'Q1: Warehouses in three countries.',
        output: null,
        inputArtifacts: [],
      })
    })
    const { user } = await openRuns(project)

    await user.click(within(rowFor('Idea refinement')).getByRole('button'))

    const drawer = await screen.findByRole('dialog', { name: 'Idea refinement run' })
    expect(within(drawer).getByText('Failed')).toBeInTheDocument()
    expect(within(drawer).getByText('The model provider returned an error.')).toBeInTheDocument()
    expect(within(drawer).getByText('anthropic returned 503 Service Unavailable.')).toBeInTheDocument()
    expect(within(drawer).getByText('After 4 attempts.')).toBeInTheDocument()
    expect(within(drawer).getByText('Q1: Warehouses in three countries.')).toBeInTheDocument()
    expect(within(drawer).getByText('This run used no artifacts as input.')).toBeInTheDocument()
    expect(await within(drawer).findByText('This run produced no output.')).toBeInTheDocument()
  })

  it('FR23_ drawer: truncated output says so, and a running run says it is running', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, { agent: 'plan_generator', output: 'x'.repeat(200), outputTruncated: true })
      seedRun(p, { agent: 'code_review', status: 'running', output: null })
    })
    const { user } = await openRuns(project)

    await user.click(within(rowFor('Plan generator')).getByRole('button'))
    let drawer = await screen.findByRole('dialog', { name: 'Plan generator run' })
    expect(await within(drawer).findByText(/The output is truncated: only the first 200,000 characters are shown\./)).toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: 'Close' }))

    await user.click(within(rowFor('Code review')).getByRole('button'))
    drawer = await screen.findByRole('dialog', { name: 'Code review run' })
    expect(within(drawer).getByText('Running…', { selector: 'dd' })).toBeInTheDocument()
    expect(await within(drawer).findByText(/^Running… The output appears here when the run finishes/)).toBeInTheDocument()
    expect(within(drawer).getAllByText('Not known yet')).toHaveLength(4)
  })

  it('FR23_ FR25_ drawer: a running run with a failed attempt says it is retrying, not that it failed', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, {
        agent: 'spec_generator',
        status: 'running',
        attempts: 2,
        error: 'PROVIDER_ERROR: anthropic returned 503 Service Unavailable.',
        output: null,
      })
    })
    const { user } = await openRuns(project)

    await user.click(within(rowFor('Spec generator')).getByRole('button'))
    const drawer = await screen.findByRole('dialog', { name: 'Spec generator run' })
    expect(within(drawer).getByText('Last attempt failed — retrying')).toBeInTheDocument()
    expect(within(drawer).getByText('The model provider returned an error.')).toBeInTheDocument()
    expect(within(drawer).getByText('anthropic returned 503 Service Unavailable.')).toBeInTheDocument()
    expect(within(drawer).getByText('2 attempts so far.')).toBeInTheDocument()
    expect(within(drawer).queryByText('The run failed')).not.toBeInTheDocument()
    expect(within(drawer).queryByRole('heading', { name: 'Error' })).not.toBeInTheDocument()
  })

  it('FR23_ drawer: opening pushes a history entry so Back closes it; Close returns to the list entry', async () => {
    let id = ''
    const project = seedProjectAs('viewer', (p) => {
      id = seedRun(p, { agent: 'code_generator' }).id
    })
    const { user, router } = await openRuns(project)
    const listHref = currentHref(router)
    const listIndex = router.history.location.state.__TSR_index

    // Back closes the drawer.
    await user.click(within(rowFor('Code generator')).getByRole('button'))
    await screen.findByRole('dialog', { name: 'Code generator run' })
    expect(currentHref(router)).toContain(`run=${id}`)
    expect(router.history.location.state.__TSR_index).toBe(listIndex + 1)
    router.history.back()
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(currentHref(router)).toBe(listHref)

    // Close (and Escape) go back to the list entry rather than stacking another one.
    await user.click(within(rowFor('Code generator')).getByRole('button'))
    const drawer = await screen.findByRole('dialog', { name: 'Code generator run' })
    await user.click(within(drawer).getByRole('button', { name: 'Close' }))
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(currentHref(router)).toBe(listHref)
    expect(router.history.location.state.__TSR_index).toBe(listIndex)

    await user.click(within(rowFor('Code generator')).getByRole('button'))
    await screen.findByRole('dialog', { name: 'Code generator run' })
    await user.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(router.history.location.state.__TSR_index).toBe(listIndex)
  })

  it('FR23_ drawer: closing a deep-linked run replaces the entry instead of leaving the page', async () => {
    let id = ''
    const project = seedProjectAs('viewer', (p) => {
      id = seedRun(p, { agent: 'code_generator' }).id
    })
    const { user, router } = await openRuns(project, `?run=${id}`)
    const drawer = await screen.findByRole('dialog', { name: 'Code generator run' })
    const index = router.history.location.state.__TSR_index

    await user.click(within(drawer).getByRole('button', { name: 'Close' }))
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(currentHref(router)).toBe(`/projects/${project.id}/runs`)
    expect(router.history.location.state.__TSR_index).toBe(index)
  })

  it('FR23_ drawer: opens from a link to a run that is not on the current page', async () => {
    let id = ''
    const project = seedProjectAs('viewer', (p) => {
      id = seedRun(p, { agent: 'code_generator' }).id
      for (let i = 0; i < 20; i += 1) {
        seedRun(p, { agent: 'code_review' })
      }
    })
    await openRuns(project, `?run=${id}`)

    const drawer = await screen.findByRole('dialog', { name: 'Code generator run' })
    expect(within(drawer).getByText('Code generator', { selector: 'dd' })).toBeInTheDocument()
  })

  it('FR23_ states: empty, error, and an unknown run', async () => {
    const empty = seedProjectAs('viewer')
    const first = await openRuns(empty)
    expect(screen.getByText('No agent runs yet')).toBeInTheDocument()
    first.unmount()

    server.use(http.get('*/api/projects/:id/runs', () => problem(500, 'VALIDATION', 'boom')))
    const failing = await openRuns(empty)
    expect(await screen.findByRole('alert')).toHaveTextContent("The runs couldn't load")
    failing.unmount()
    server.resetHandlers()

    await openRuns(empty, '?run=00000000-0000-7000-8000-00000000ffff')
    const drawer = await screen.findByRole('dialog', { name: 'Agent run' })
    expect(await within(drawer).findByText("The run couldn't load")).toBeInTheDocument()
  })

  it('FR23_ live: a run event refreshes the list without reloading (FR-46)', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, { agent: 'spec_generator', status: 'running', output: null })
    })
    const { live } = await openRuns(project)
    await waitFor(() => {
      expect(live.latest.joined(project.id)).toBe(1)
    })
    expect(within(rowFor('Spec generator')).getByText('Running…', { selector: 'span' })).toBeInTheDocument()

    const run = project.runs[0]
    if (run) {
      Object.assign(run, { status: 'succeeded', completedAt: new Date().toISOString() })
    }
    live.latest.emit(projectEvent(project.id, 'run', { type: 'RUN_COMPLETED' }))

    await waitFor(() => {
      expect(within(rowFor('Spec generator')).getByText('Succeeded')).toBeInTheDocument()
    })
  })
})

describe('Agent runs page (SPEC §7.2, v2 columns)', () => {
  it('FR74_runs_table_shows_the_preset_as_id_at_version', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, { agent: 'code_generator', agentKey: 'ui-engineer', presetId: 'nextjs-frontend', presetVersion: '1.0.0' })
      seedRun(p, { agent: 'plan_generator' })
    })
    await openRuns(project)

    expect(within(rowFor('Code generator')).getByTestId('run-preset')).toHaveTextContent('nextjs-frontend@1.0.0')
    expect(within(rowFor('Plan generator')).getByTestId('run-preset')).toHaveTextContent('—')
  })

  it('FR63_FR71_runs_table_shows_specialist_credential_tool_calls_and_version_checkpoint', async () => {
    const project = seedProjectAs('viewer', (p) => {
      p.checkpoint = 4
      seedRun(p, {
        agent: 'code_generator',
        agentKey: 'frontend-engineer',
        toolCallCount: 12,
        credentialLabel: 'Team key (••••a1b2)',
        checkpoint: 4,
      })
    })
    const { user } = await openRuns(project)

    const head = within(table()).getAllByRole('columnheader').map((h) => h.textContent)
    expect(head).toEqual(expect.arrayContaining(['Specialist', 'Credential', 'Version', 'Tool calls']))
    const row = rowFor('Code generator')
    expect(row).toHaveTextContent('frontend-engineer')
    expect(row).toHaveTextContent('Team key (••••a1b2)')
    expect(within(row).getByTestId('run-version')).toHaveTextContent('0.1.0 · checkpoint 4')
    expect(row).toHaveTextContent('12')

    await user.click(within(row).getByRole('button', { name: /^Code generator run/ }))
    const sheet = await screen.findByRole('dialog', { name: 'Code generator run' })
    expect(within(sheet).getByText('Specialist').nextElementSibling).toHaveTextContent('frontend-engineer')
    expect(within(sheet).getByText('Credential').nextElementSibling).toHaveTextContent('Team key (••••a1b2)')
    expect(within(sheet).getByText('Repository tool calls').nextElementSibling).toHaveTextContent('12')
  })

  it('FR70_FR71_run_details_list_tool_calls_with_refusals_and_warnings', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedRun(p, {
        agent: 'repository_analyst',
        toolCallCount: 2,
        toolCalls: [
          { tool: 'read_file', arguments: { path: 'src/index.ts' }, bytes: 2048, truncated: false, error: null },
          { tool: 'read_file', arguments: { path: '../../etc/passwd' }, bytes: 0, truncated: false, error: 'UNSAFE_PATH' },
        ],
        warnings: ['tools_unsupported'],
      })
    })
    const { user } = await openRuns(project)

    const row = rowFor('Repository Analyst')
    await user.click(within(row).getByRole('button', { name: /^Repository Analyst run/ }))
    const sheet = await screen.findByRole('dialog', { name: 'Repository Analyst run' })
    const tools = await within(sheet).findByRole('table', { name: 'Repository tool calls of this run' })
    const rows = within(tools).getAllByRole('row').slice(1)
    expect(rows[0]).toHaveTextContent('{"path":"src/index.ts"}')
    expect(rows[0]).toHaveTextContent('Complete')
    expect(rows[1]).toHaveTextContent('Refused: the path is outside the repository or hidden (UNSAFE_PATH)')
    expect(within(sheet).getByRole('heading', { name: 'Repository tool calls (2)' })).toBeInTheDocument()
    expect(within(sheet).getByText(/doesn't support tool use/)).toBeInTheDocument()
  })
})
