import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { beforeEach, describe, expect, it } from 'vitest'

import { coachRunControlDefinition, runControlDb } from '../../mocks/handlers/engine-run-control'
import { seedCoachRun } from '../../mocks/handlers/pipeline-seeds'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { installRunControlMocks, memberOf, renderPipeline } from './pipeline-harness.test-utils'

const validLog = '{"sessions":[{"date":"2026-11-23","type":"tempo","intensity":"hard","rpe":7}]}'

beforeEach(() => {
  installRunControlMocks()
})

async function openStartRun() {
  const { project, access } = memberOf('contributor')
  const utils = await renderPipeline(project.id, access)
  await utils.user.click(await screen.findByRole('button', { name: 'Start run' }))
  const dialog = await screen.findByRole('dialog', { name: 'Start a run' })
  return { ...utils, project, dialog: within(dialog) }
}

describe('FR-362: Start run and Submit input', () => {
  it('FR362_coach_session_log_editor_renders_from_its_schema', async () => {
    const { dialog } = await openStartRun()
    expect(dialog.getByRole('combobox', { name: 'Pipeline' })).toHaveDisplayValue('Impulse coaching')
    // Only engine-controlled pipelines are offered (the SDLC and the 0.7.0 fixture are host-run).
    expect(dialog.getAllByRole('option', { name: /Impulse coaching|Fixture intake/ })).toHaveLength(2)
    expect(dialog.queryByRole('option', { name: 'Software delivery' })).not.toBeInTheDocument()
    expect(await dialog.findByRole('combobox', { name: 'Approval mode' })).toHaveDisplayValue('Default (Strict)')
    expect(dialog.getAllByRole('option', { name: /^(Strict|Advisory|Autonomous)$/ }).map((o) => o.textContent)).toEqual(['Strict', 'Advisory'])
    // session_log nests a list of sessions, so it gets the JSON editor with a guide built from the schema.
    expect(dialog.getByRole('textbox', { name: 'Session log (JSON)' })).toBeInTheDocument()
    const guide = dialog.getByRole('list', { name: 'Fields of session log' })
    expect(within(guide).getByText('sessions[].date')).toBeInTheDocument()
    expect(guide).toHaveTextContent('sessions[].date: date (YYYY-MM-DD) (required)')
    expect(guide).toHaveTextContent('sessions[].intensity: one of easy, moderate, hard, race')
    expect(dialog.getByLabelText('Upload a JSON file')).toHaveAttribute('type', 'file')
  })

  it('FR362_invalid_date_shows_the_schema_error_inline', async () => {
    const { dialog, user } = await openStartRun()
    const editor = await dialog.findByRole('textbox', { name: 'Session log (JSON)' })
    await user.click(editor)
    await user.paste('{"sessions":[{"date":"2026-11-31","type":"tempo"}]}')
    await user.click(dialog.getByRole('button', { name: 'Start run' }))

    expect(await dialog.findByText('sessions[0].date must be a date (YYYY-MM-DD)')).toBeInTheDocument()
    expect(editor).toHaveAttribute('aria-invalid', 'true')
    expect(editor).toHaveAccessibleDescription(/sessions\[0\]\.date must be a date/)
    expect(runControlDb.lastStart).toBeNull()
  })

  it('FR362_valid_submit_lands_on_the_new_run', async () => {
    const { dialog, user, project } = await openStartRun()
    await user.click(await dialog.findByRole('textbox', { name: 'Session log (JSON)' }))
    await user.paste(validLog)
    await user.selectOptions(dialog.getByRole('combobox', { name: 'Approval mode' }), 'advisory')
    await user.click(dialog.getByRole('button', { name: 'Start run' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Start a run' })).not.toBeInTheDocument()
    })
    expect(runControlDb.lastStart).toEqual({
      pipelineId: 'impulse-coach',
      approvalMode: 'advisory',
      inputs: { session_log: JSON.parse(validLog) as unknown },
    })
    const summary = await screen.findByTestId('run-summary')
    expect(within(summary).getByRole('heading', { name: 'Impulse coaching' })).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Run' })).toHaveDisplayValue(/^Impulse coaching/)
    expect(await screen.findByText('Impulse coaching started.')).toBeInTheDocument()
    expect(project.id).toBeTruthy()
  })

  it('FR362_an_input_left_blank_starts_a_run_that_waits_on_input', async () => {
    const { dialog, user } = await openStartRun()
    await dialog.findByRole('textbox', { name: 'Session log (JSON)' })
    await user.click(dialog.getByRole('button', { name: 'Start run' }))
    await waitFor(() => {
      expect(runControlDb.lastStart).toEqual({ pipelineId: 'impulse-coach', approvalMode: null, inputs: null })
    })
    expect(await screen.findByRole('button', { name: 'Submit input' })).toBeInTheDocument()
  })

  it('FR362_flat_schema_gets_a_generated_form_validated_from_the_schema', async () => {
    const { dialog, user } = await openStartRun()
    await user.selectOptions(dialog.getByRole('combobox', { name: 'Pipeline' }), 'fixture-intake')
    const summaryField = await dialog.findByRole('textbox', { name: 'Summary' })
    expect(dialog.getByRole('combobox', { name: 'Priority' })).toBeInTheDocument()
    expect(dialog.getByLabelText('Due date (optional)')).toHaveAttribute('type', 'date')
    expect(dialog.getByRole('textbox', { name: 'Estimate (hours) (optional)' })).toBeInTheDocument()
    expect(dialog.getByRole('checkbox', { name: 'Urgent' })).toBeInTheDocument()
    // No defaultApprovalMode and no list: any mode, defaulting to the project's.
    expect(dialog.getByRole('combobox', { name: 'Approval mode' })).toHaveDisplayValue('The project’s approval mode')

    await user.type(summaryField, 'Broken login')
    await user.type(dialog.getByRole('textbox', { name: 'Estimate (hours) (optional)' }), '-2')
    await user.click(dialog.getByRole('button', { name: 'Start run' }))
    expect(await dialog.findByText('Choose priority.')).toBeInTheDocument()
    expect(dialog.getByText('Estimate (hours) must be at least 0.')).toBeInTheDocument()
    expect(runControlDb.lastStart).toBeNull()

    await user.selectOptions(dialog.getByRole('combobox', { name: 'Priority' }), 'high')
    await user.clear(dialog.getByRole('textbox', { name: 'Estimate (hours) (optional)' }))
    await user.type(dialog.getByRole('textbox', { name: 'Estimate (hours) (optional)' }), '1.5')
    await user.click(dialog.getByRole('checkbox', { name: 'Urgent' }))
    await user.click(dialog.getByRole('button', { name: 'Start run' }))
    await waitFor(() => {
      expect(runControlDb.lastStart?.inputs).toEqual({ request: { summary: 'Broken login', priority: 'high', estimate: 1.5, urgent: true } })
    })
    expect(within(await screen.findByTestId('run-summary')).getByRole('heading', { name: 'Fixture intake' })).toBeInTheDocument()
  })

  it('FR362_flat_form_upload_error_is_announced_and_tied_to_the_picker', async () => {
    const { dialog, user } = await openStartRun()
    await user.selectOptions(dialog.getByRole('combobox', { name: 'Pipeline' }), 'fixture-intake')
    await dialog.findByRole('textbox', { name: 'Summary' })
    const picker = dialog.getByLabelText('Upload a JSON file')
    await user.upload(picker, new File(['{not json'], 'bad.json', { type: 'application/json' }))
    expect(await dialog.findByText('That file isn’t valid JSON.')).toHaveAttribute('aria-live', 'polite')
    expect(picker).toHaveAttribute('aria-invalid', 'true')
    expect(picker).toHaveAccessibleDescription('That file isn’t valid JSON.')

    await user.upload(picker, new File(['{"summary":"From file","priority":"low"}'], 'ok.json', { type: 'application/json' }))
    await waitFor(() => {
      expect(dialog.getByRole('textbox', { name: 'Summary' })).toHaveValue('From file')
    })
    expect(picker).not.toHaveAttribute('aria-invalid')
  })

  it('FR362_json_file_upload_fills_the_editor', async () => {
    const { dialog, user } = await openStartRun()
    await dialog.findByRole('textbox', { name: 'Session log (JSON)' })
    const file = new File([validLog], 'log.json', { type: 'application/json' })
    await user.upload(dialog.getByLabelText('Upload a JSON file'), file)
    await waitFor(() => {
      expect(dialog.getByRole('textbox', { name: 'Session log (JSON)' })).toHaveValue(validLog)
    })
  })

  it('FR362_api_errors_map_through_the_shared_copy_with_the_failing_paths', async () => {
    server.use(
      http.post('*/api/projects/:id/pipeline-runs', () =>
        problem(400, 'INPUT_INVALID', 'Invalid input.', { details: ['session_log: /sessions/0/rpe must be at most 10'] }),
      ),
    )
    const { dialog, user } = await openStartRun()
    await user.click(await dialog.findByRole('textbox', { name: 'Session log (JSON)' }))
    await user.paste(validLog)
    await user.click(dialog.getByRole('button', { name: 'Start run' }))
    const alert = await dialog.findByRole('alert')
    expect(await within(alert).findByText("Some inputs don't match their schema. Fix them and try again.")).toBeInTheDocument()
    expect(within(alert).getByText('session_log: /sessions/0/rpe must be at most 10')).toBeInTheDocument()

    server.use(http.post('*/api/projects/:id/pipeline-runs', () => problem(409, 'RUN_ALREADY_ACTIVE', 'Active.')))
    await user.click(dialog.getByRole('button', { name: 'Start run' }))
    expect(await within(alert).findByText('This project already has a run in progress for this pipeline.')).toBeInTheDocument()
  })

  it('FR362_viewer_sees_no_start_run_or_submit_input', async () => {
    const { project, access } = memberOf('viewer')
    seedCoachRun(project, { status: 'waiting_on_input' })
    await renderPipeline(project.id, access)
    expect(await screen.findByTestId('run-summary')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start run' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Submit input' })).not.toBeInTheDocument()
  })

  it('FR362_no_start_run_without_access_from_the_host', async () => {
    const { project } = memberOf('owner')
    await renderPipeline(project.id, { role: null, userId: null })
    expect(await screen.findByText('No runs yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start run' })).not.toBeInTheDocument()
  })

  it('FR362_no_submit_input_on_a_host_controlled_run', async () => {
    runControlDb.definitions = [{ ...coachRunControlDefinition, runControl: 'host' }]
    const { project, access } = memberOf('contributor')
    seedCoachRun(project, { status: 'waiting_on_input' })
    await renderPipeline(project.id, access)
    expect(await screen.findByTestId('run-summary')).toBeInTheDocument()
    expect(await screen.findByRole('region', { name: 'Feedback' })).toBeInTheDocument()
    await screen.findByRole('combobox', { name: 'Agent' })
    expect(screen.queryByRole('button', { name: 'Submit input' })).not.toBeInTheDocument()
  })

  it('FR362_submit_input_on_a_run_waiting_on_input', async () => {
    const { project, access } = memberOf('contributor')
    const stored = seedCoachRun(project, { status: 'waiting_on_input' })
    const { user } = await renderPipeline(project.id, access)
    await user.click(await screen.findByRole('button', { name: 'Submit input' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Submit input' }))
    const editor = dialog.getByRole('textbox', { name: 'Session log (JSON)' })

    await user.click(dialog.getByRole('button', { name: 'Submit input' }))
    expect(await dialog.findByText('Enter the session log as JSON.')).toBeInTheDocument()

    await user.click(editor)
    await user.paste(validLog)
    await user.click(dialog.getByRole('button', { name: 'Submit input' }))
    expect(await screen.findByText('Session log submitted. The run continues.')).toBeInTheDocument()
    expect(runControlDb.lastSubmit).toEqual({ runId: stored.run.id, artifactKey: 'session_log', content: JSON.parse(validLog) as unknown })
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Submit input' })).not.toBeInTheDocument()
    })
  })
})
