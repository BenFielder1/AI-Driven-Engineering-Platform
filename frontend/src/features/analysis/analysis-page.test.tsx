import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { ApiError, errorMessage, errorMessages } from '../../lib/errors'
import type { ProjectRole } from '../../lib/permissions'
import {
  failStackAnalysis,
  finishStackAnalysis,
  sampleProfile,
  seedInference,
  unknownBackendProfile,
  unsupportedBackendProfile,
} from '../../mocks/handlers/analysis'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { problem } from '../../mocks/handlers/problem'
import { makeReleaseVersion, type StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { parseStackProfile } from './stack-profile-format'

function seedAnalysis(role: ProjectRole, init: Parameters<typeof seedInference>[1] = {}, setup?: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    seedInference(p, init)
    setup?.(p)
  })
}

async function openAnalysis(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/analysis`)
  await screen.findByRole('heading', { level: 2, name: 'Analysis' })
  await waitFor(() => {
    expect(screen.queryByText('Loading the analysis…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

/** The body of the last profile save, parsed back (FR-183). */
function lastSavedProfile() {
  const body = requestsTo('POST', '/artifacts/stack_profile').at(-1)?.body as Schemas['CreateArtifactDraftRequest'] | undefined
  return parseStackProfile(body?.content ?? '')
}

const approveButton = () => screen.getByRole('button', { name: 'Approve' })

describe('Analysis page (FR-182)', () => {
  it('FR182_inference_version_shows_run_findings_structure_hosts_and_the_summary_in_order', async () => {
    const project = seedAnalysis('owner')
    await openAnalysis(project)

    const run = screen.getByRole('region', { name: 'Stack Analyst' })
    expect(within(run).getByText('Succeeded')).toBeInTheDocument()
    expect(within(run).getByRole('link', { name: 'View the Stack Analyst run' })).toBeInTheDocument()

    const findings = screen.getByRole('region', { name: 'Findings' })
    const frontend = within(findings).getByTestId('finding-frontend')
    expect(await within(frontend).findByText('Vue')).toBeInTheDocument()
    expect(within(frontend).getByText('frontend', { selector: 'dd' })).toBeInTheDocument()
    expect(within(frontend).getByText('High confidence')).toBeInTheDocument()
    expect(within(frontend).getByText('frontend/package.json')).toBeInTheDocument()
    expect(within(findings).getByTestId('finding-data')).toHaveTextContent('Supabase (BaaS)')

    const structure = screen.getByRole('table', { name: /Repository structure/ })
    expect(within(structure).getByRole('rowheader', { name: 'supabase/migrations' })).toBeInTheDocument()
    expect(within(structure).getByText('Migrations')).toBeInTheDocument()

    // FR-184: the detected database host is preselected and marked.
    const hosts = screen.getByRole('region', { name: 'Hosts' })
    expect(within(hosts).getByLabelText('Database host')).toHaveValue('supabase')
    expect(within(hosts).getByText(/Detected: found in the repository/)).toBeInTheDocument()

    const regions = screen.getAllByRole('region').map((r) => r.getAttribute('aria-labelledby'))
    const order = ['stack-analyst-title', 'profile-findings-title', 'profile-structure-title', 'profile-hosts-title', 'profile-actions-title', 'codebase-summary-title']
    expect(order.map((id) => regions.indexOf(id))).toEqual([...order.map((id) => regions.indexOf(id))].sort((x, y) => x - y))
    expect(approveButton()).toBeEnabled()
    // The locked codebase summary moved here from Refinement.
    const summary = screen.getByRole('region', { name: 'Codebase summary' })
    expect(await within(summary).findByRole('article', { name: 'Codebase summary v1' })).toBeInTheDocument()
  })

  it('FR182_non_inference_import_shows_the_summary_with_no_approve_action', async () => {
    const project = seedProjectAs('owner', (p) => {
      const development = makeReleaseVersion({ version: '1.2.0', baseTag: 'v1.1.0', baseCommitSha: 'abc1234def5678' })
      p.releaseVersions = [makeReleaseVersion({ version: '1.1.0', status: 'released' }), development]
      p.currentPhase = 'refinement'
      p.analyst = { [development.id]: { status: 'completed', runId: null, artifactId: null, artifactVersion: null } }
      seedArtifact(p, { type: 'codebase_summary', status: 'locked', content: '# Codebase summary\n\n## Overview\n\nAn Express shop.' })
    })
    await openAnalysis(project)

    expect(screen.getByText(/This version’s stack was chosen, not inferred|This version's stack was chosen, not inferred/)).toBeInTheDocument()
    const summary = screen.getByRole('region', { name: 'Codebase summary' })
    expect(await within(summary).findByText('An Express shop.')).toBeInTheDocument()
    expect(within(summary).getByText('Locked')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Stack Analyst' })).not.toBeInTheDocument()
  })

  it.each(['queued', 'running'] as const)('FR182_FR70_summary_being_generated_while_the_repository_analyst_is_%s', async (status) => {
    const project = seedAnalysis('owner', { summary: status, run: null })
    await openAnalysis(project)

    const summary = screen.getByRole('region', { name: 'Codebase summary' })
    expect(within(summary).getByRole('status')).toHaveTextContent('Summary being generated…')
    expect(within(summary).getByRole('status')).toHaveTextContent('The Stack Analyst runs when it’s done.')
    expect(screen.getByRole('region', { name: 'Stack Analyst' })).toHaveTextContent(/hasn.t started yet/)
  })

  it('FR182_FR70_failed_summary_explains_and_links_the_run', async () => {
    const project = seedAnalysis('owner', { summary: 'failed' })
    await openAnalysis(project)

    const summary = screen.getByRole('region', { name: 'Codebase summary' })
    expect(within(summary).getByText('The Repository Analyst failed')).toBeInTheDocument()
    expect(within(summary).getByRole('link', { name: 'View the Repository Analyst run' })).toBeInTheDocument()
  })

  it('FR182_agent_running_is_live_and_the_draft_arrives_with_the_run_event', async () => {
    const project = seedAnalysis('contributor', { run: 'running', profile: null })
    const { live } = await openAnalysis(project)

    const run = screen.getByRole('region', { name: 'Stack Analyst' })
    expect(within(run).getByRole('status')).toHaveTextContent('The Stack Analyst is reading the repository')
    expect(screen.queryByRole('region', { name: 'Findings' })).not.toBeInTheDocument()

    finishStackAnalysis(project, sampleProfile())
    live.latest.emit(projectEvent(project.id, 'run', { type: 'RUN_COMPLETED' }))

    expect(await screen.findByRole('region', { name: 'Findings' })).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Stack Analyst' })).getByText('Succeeded')).toBeInTheDocument()
    // A Contributor can't approve: the reason is visible.
    expect(approveButton()).toBeDisabled()
    expect(screen.getByTestId('approve-reason')).toHaveTextContent('Only project Owners can approve or reject the stack profile.')
  })

  it('FR182_viewer_sees_actions_disabled_with_reasons', async () => {
    const project = seedAnalysis('viewer', { profile: unknownBackendProfile() })
    await openAnalysis(project)

    const needs = screen.getByRole('region', { name: 'Needs your input' })
    expect(within(needs).queryByRole('combobox')).not.toBeInTheDocument()
    expect(within(needs).getByText('Only Contributors and Owners can answer these.')).toBeInTheDocument()
    expect(approveButton()).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Database host')).toBeDisabled()
  })

  it('FR182_locked_profile_is_read_only_after_advancing', async () => {
    const project = seedAnalysis('owner', { phase: 'refinement', profileStatus: 'locked' })
    await openAnalysis(project)

    expect(screen.getByText('The stack profile is locked')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Continue to Refinement' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Database host')).toBeDisabled()
  })
})

describe('Stack Analyst failure (FR-180)', () => {
  it('FR180_failed_run_offers_retry_and_fill_in_manually_from_the_detector', async () => {
    const project = seedAnalysis('owner', { run: 'running', profile: null })
    failStackAnalysis(project)
    const { user } = await openAnalysis(project)

    const run = screen.getByRole('region', { name: 'Stack Analyst' })
    const alert = within(run).getByRole('alert')
    expect(alert).toHaveTextContent('The Stack Analyst failed')
    expect(alert).toHaveTextContent(errorMessages.STACK_PROFILE_INVALID)

    await user.click(within(alert).getByRole('button', { name: 'Fill in manually' }))
    const form = await screen.findByRole('form', { name: 'Fill in the profile yourself' })
    // The detector found Supabase but no backend: the backend starts undecided.
    expect(within(form).getByLabelText('Data option')).toHaveValue('data-supabase')
    expect(within(form).getByLabelText('Backend option')).toHaveValue('')
    await user.selectOptions(within(form).getByLabelText('Backend option'), 'none')
    await user.click(within(form).getByRole('button', { name: 'Save as new draft' }))

    expect(await screen.findByText(/Saved as draft v1/)).toBeInTheDocument()
    const saved = lastSavedProfile()
    expect(saved?.document.layers.backend).toMatchObject({ status: 'none', option: null })
    expect(requestsTo('POST', '/artifacts/stack_profile').at(-1)?.body).toMatchObject({ baseVersion: null })
  })

  it('FR180_retry_queues_the_analyst_again', async () => {
    const project = seedAnalysis('contributor', { run: 'running', profile: null })
    failStackAnalysis(project)
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText(/The Stack Analyst is about to start/)).toBeInTheDocument()
    expect(requestsTo('POST', '/analysis/run')).toHaveLength(1)
  })

  it('FR180_retry_error_uses_the_one_error_map', async () => {
    const project = seedAnalysis('owner', { run: 'running', profile: null })
    failStackAnalysis(project)
    server.use(http.post('*/api/projects/:id/analysis/run', () => problem(409, 'BUDGET_EXCEEDED', 'Budget used up.')))
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText("We couldn't start the Stack Analyst")).toBeInTheDocument()
  })
})

describe('Needs your input (FR-181)', () => {
  it('FR181_backend_unknown_is_listed_first_and_approve_is_disabled_with_the_reason', async () => {
    const project = seedAnalysis('owner', { profile: unknownBackendProfile() })
    await openAnalysis(project)

    const needs = screen.getByRole('region', { name: 'Needs your input' })
    const list = within(needs).getByRole('list', { name: 'Layers that need your input' })
    const item = within(list).getByTestId('needs-input-backend')
    expect(item).toHaveTextContent('Backend: Unknown')
    expect(item).toHaveTextContent('The agent asks: Is the server/ folder the backend')
    // The first form control on the page is the answer picker.
    const firstControl = document.querySelector('section[aria-labelledby="analysis-title"]')?.querySelector('select, input, textarea')
    expect(firstControl).toBe(within(item).getByLabelText('Backend option'))

    // §4.27 worked example 2: Approve disabled with the reason as text; hosts wait for the layers.
    expect(approveButton()).toBeDisabled()
    expect(screen.getByTestId('approve-reason')).toHaveTextContent('Answer these before approving: backend.')
    expect(screen.getByLabelText('Database host')).toBeDisabled()
    expect(within(screen.getByRole('region', { name: 'Hosts' })).getByText(/Resolve the frontend, backend and data layers first/)).toBeInTheDocument()
  })

  it('FR181_STACK_PROFILE_INCOMPLETE_copy_names_the_layers', () => {
    const error = new ApiError({ status: 409, errorCode: 'STACK_PROFILE_INCOMPLETE', details: ['backend', 'data'] })
    expect(errorMessage(error, 'decideStackProfile')).toBe('Answer these before approving: Backend, Data.')
    expect(errorMessage(new ApiError({ status: 400, errorCode: 'STACK_INFER_NO_CODE' }))).toBe(
      'This repository has no code to analyse. Choose a stack instead.',
    )
  })

  it('FR181_answering_a_blank_saves_a_new_draft_and_unlocks_approve', async () => {
    const project = seedAnalysis('owner', { profile: unknownBackendProfile() })
    const { user } = await openAnalysis(project)

    const item = screen.getByTestId('needs-input-backend')
    await user.selectOptions(within(item).getByLabelText('Backend option'), 'be-express')
    const folder = within(item).getByLabelText('Backend folder')
    await user.clear(folder)
    await user.type(folder, 'server')
    await user.click(screen.getByRole('button', { name: 'Save answers as a new draft' }))

    expect(await screen.findByText(/Saved as draft v2/)).toBeInTheDocument()
    const saved = lastSavedProfile()
    expect(saved?.document.layers.backend).toMatchObject({ option: 'be-express', path: 'server', status: 'user' })
    expect(saved?.document.questions).toEqual([])
    expect(requestsTo('POST', '/artifacts/stack_profile').at(-1)?.body).toMatchObject({ baseVersion: 1 })
    await waitFor(() => {
      expect(screen.queryByRole('region', { name: 'Needs your input' })).not.toBeInTheDocument()
    })
  })

  it('FR181_unsupported_backend_continue_with_no_stack_locks_the_profile_as_generalist', async () => {
    const project = seedAnalysis('owner', { profile: unsupportedBackendProfile() })
    const { user } = await openAnalysis(project)

    const item = screen.getByTestId('needs-input-backend')
    expect(item).toHaveTextContent('Django isn’t in the stack catalog')
    // §4.27 worked example 4: not approvable as it is.
    expect(approveButton()).toBeDisabled()

    const needs = screen.getByRole('region', { name: 'Needs your input' })
    await user.click(within(needs).getByRole('button', { name: 'Continue with No stack (general)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Continue with No stack (general)?' })
    await user.click(within(dialog).getByRole('button', { name: 'Continue with No stack' }))

    expect(await screen.findByText(/approved with No stack \(general\)/)).toBeInTheDocument()
    expect(requestsTo('POST', '/approve').at(-1)?.body).toEqual({ comment: null, generalist: true })
    // Worked example 5: locked with generalist: true, no composition, and Refinement can start.
    const locked = project.artifacts.filter((a) => a.type === 'stack_profile').find((a) => a.status === 'locked')
    expect(parseStackProfile(locked?.content ?? '')?.document.generalist).toBe(true)
    expect(await screen.findByRole('button', { name: 'Continue to Refinement' })).toBeEnabled()
  })
})

describe('Edit, approve, reject (FR-183)', () => {
  it('FR183_edit_is_a_form_and_saves_a_new_draft_authored_by_the_user', async () => {
    const project = seedAnalysis('contributor')
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Edit' }))
    const form = screen.getByRole('form', { name: 'Edit the stack profile' })
    expect(within(form).queryByRole('textbox', { name: /markdown/i })).not.toBeInTheDocument()
    const folder = within(form).getByLabelText('Frontend folder')
    await user.clear(folder)
    await user.type(folder, 'web')
    await user.selectOptions(within(form).getByLabelText('Frontend host'), 'deploy-vercel')
    await user.click(within(form).getByRole('button', { name: 'Save as new draft' }))

    expect(await screen.findByText(/Saved as draft v2/)).toBeInTheDocument()
    const saved = lastSavedProfile()
    expect(saved?.narrative).toBe('A Vue single-page app on Supabase.')
    expect(saved?.document.layers.frontend).toMatchObject({ option: 'fe-vue', path: 'web' })
    expect(saved?.document.hosts.frontendHost).toBe('deploy-vercel')
  })

  it('FR183_server_field_errors_land_on_their_fields', async () => {
    const project = seedAnalysis('owner')
    server.use(
      http.post('*/api/projects/:id/artifacts/stack_profile', () =>
        problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
          errors: { 'layers.frontend.path': ["'web' is not a directory at the base commit."], 'structure[0].path': ['bad path'] },
        }),
      ),
    )
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Edit' }))
    const form = screen.getByRole('form', { name: 'Edit the stack profile' })
    await user.click(within(form).getByRole('button', { name: 'Save as new draft' }))

    expect(await within(form).findByText("'web' is not a directory at the base commit.", { selector: 'p' })).toBeInTheDocument()
    expect(within(form).getByLabelText('Frontend folder')).toHaveAttribute('aria-invalid', 'true')
    expect(within(form).getByText(/structure\[0\]\.path: bad path/)).toBeInTheDocument()
  })

  it('FR183_changing_the_backend_shows_the_R3_reason_on_the_data_layer', async () => {
    const project = seedAnalysis('owner', {
      profile: sampleProfile({
        layers: {
          backend: { option: 'be-express', path: 'api', mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
          data: { option: 'data-prisma', path: null, mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
        },
        hosts: { databaseHost: null, detected: [], originModel: null, local: null },
      }),
    })
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Edit' }))
    const form = screen.getByRole('form', { name: 'Edit the stack profile' })
    await user.selectOptions(within(form).getByLabelText('Backend option'), 'be-aspnet')
    expect(
      await within(form).findByText("Prisma isn't available with your other choices: Prisma needs Express, NestJS or Next.js full-stack (R3).", {
        selector: 'p',
      }),
    ).toBeInTheDocument()
  })

  it('FR183_approve_forced_through_returns_STACK_INCOMPATIBLE_with_the_reason', async () => {
    const project = seedAnalysis('owner', {
      profile: sampleProfile({
        layers: {
          backend: { option: 'be-aspnet', path: 'api', mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
          data: { option: 'data-prisma', path: null, mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
        },
        hosts: { databaseHost: null, detected: [], originModel: null, local: null },
      }),
    })
    const { user } = await openAnalysis(project)

    await user.click(approveButton())
    const dialog = await screen.findByRole('dialog', { name: 'Approve stack profile v1?' })
    await user.click(within(dialog).getByRole('button', { name: 'Approve v1' }))
    expect(await within(dialog).findByText(/These stack choices don't work together: Prisma needs Express, NestJS or Next.js full-stack/)).toBeInTheDocument()
  })

  it('FR183_reject_needs_feedback_and_re_runs_the_analyst', async () => {
    const project = seedAnalysis('owner')
    const { user } = await openAnalysis(project)

    await user.click(screen.getByRole('button', { name: 'Reject' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Reject stack profile v1?' })
    await user.click(within(dialog).getByRole('button', { name: 'Reject v1' }))
    expect(await within(dialog).findAllByText('Tell the Stack Analyst what to change.')).not.toHaveLength(0)
    expect(requestsTo('POST', '/reject')).toHaveLength(0)

    await user.type(within(dialog).getByLabelText('Feedback for the Stack Analyst'), 'The backend is in api/.')
    await user.click(within(dialog).getByRole('button', { name: 'Reject v1' }))
    expect(await screen.findByText(/rejected. The Stack Analyst runs again/)).toBeInTheDocument()
    expect(requestsTo('POST', '/reject').at(-1)?.body).toEqual({ comment: 'The backend is in api/.' })
    expect(await screen.findByText(/The Stack Analyst is about to start/)).toBeInTheDocument()
  })

  it('FR183_FR185_approve_pins_the_stack_then_continue_to_refinement', async () => {
    const project = seedAnalysis('owner')
    const { user, router } = await openAnalysis(project)

    await user.click(approveButton())
    const dialog = await screen.findByRole('dialog', { name: 'Approve stack profile v1?' })
    await user.click(within(dialog).getByRole('button', { name: 'Approve v1' }))
    expect(await screen.findByText(/v1 approved and locked/)).toBeInTheDocument()
    expect(project.stack?.composition?.frontend?.pack).toBe('fe-vue')
    expect(project.stackPacks).toContain('data-supabase')

    await user.click(await screen.findByRole('button', { name: 'Continue to Refinement' }))
    await waitFor(() => {
      expect(router.href).toBe(`/projects/${project.id}/refinement`)
    })
    expect(project.currentPhase).toBe('refinement')
  })
})

describe('Deployment on the page (FR-184, FR-185)', () => {
  it('FR184_railway_for_all_three_parts_shows_one_group_window_with_skip_for_now', async () => {
    const project = seedAnalysis('owner', {
      profile: sampleProfile({
        layers: {
          backend: { option: 'be-express', path: 'api', mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
          data: { option: 'data-prisma', path: null, mode: null, status: 'inferred', confidence: 'high', technology: null, evidence: [], candidates: [] },
        },
        hosts: { frontendHost: 'deploy-railway', backendHost: 'deploy-railway', databaseHost: 'railway-postgres', originModel: null, local: null, detected: [] },
      }),
      profileStatus: 'locked',
    })
    const { user } = await openAnalysis(project)

    const setup = await screen.findByRole('region', { name: 'Deployment setup' })
    const windows = await within(setup).findByRole('list', { name: 'Deployment setup windows' })
    const items = within(windows).getAllByRole('listitem').filter((li) => li.parentElement === windows)
    expect(items).toHaveLength(1)
    expect(within(windows).getByTestId('group-railway')).toBeInTheDocument()

    await user.click(within(windows).getByRole('button', { name: /^Skip for now/ }))
    expect(within(setup).getByText(/skipped for now/)).toBeInTheDocument()
    // Skipped setup doesn't block Continue (FR-185).
    expect(screen.getByRole('button', { name: 'Continue to Refinement' })).toBeEnabled()
  })
})
