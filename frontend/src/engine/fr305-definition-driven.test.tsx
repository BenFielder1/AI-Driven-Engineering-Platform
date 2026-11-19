import { fireEvent, screen, within } from '@testing-library/react'
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../mocks/handlers/auth'
import { fixtureReviewDefinition, sdlcDefinition } from '../mocks/handlers/pipeline-definitions'
import { seedFixtureRun } from '../mocks/handlers/pipeline-seeds'
import { seedProject } from '../mocks/handlers/projects'
import { renderRoute } from '../test/render'

const engineDir = import.meta.dirname

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) {
      return sources(path)
    }
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name) ? [path] : []
  })
}

/**
 * Names a pipeline defines: the SDLC's stage ids, artifact keys, gate types and agent keys, and the fixture pipeline's
 * own (its `draft` and `check` keys are left out: `draft` is also the generic artifact status).
 */
const packNames = [
  ...sdlcDefinition.stages.map((s) => s.id),
  ...sdlcDefinition.artifacts.map((a) => a.key),
  ...sdlcDefinition.stages.flatMap((s) => [s.gate?.type, s.agentKey]),
  'escalation',
  fixtureReviewDefinition.pipelineId,
  'summarise',
  'report_approval',
  'drafter',
  'checker',
  'summariser',
].filter((n): n is string => Boolean(n))

describe('FR-305: the engine UI renders from the definition and run data only', () => {
  it('FR305_no_pack_names_in_src_engine: no stage, artifact, gate or agent key appears as a string in the engine sources', () => {
    const files = sources(engineDir)
    expect(files.length).toBeGreaterThan(10)
    const found: string[] = []
    for (const file of files) {
      const text = readFileSync(file, 'utf8')
      for (const name of packNames) {
        // As a string literal or a property key ('spec_approval', "planning", `review`, spec_approval:).
        if (new RegExp(`['"\`]${name}['"\`]|\\b${name}\\s*:`).test(text)) {
          found.push(`${relative(engineDir, file)}: ${name}`)
        }
      }
    }
    expect(found).toEqual([])
  })

  it('FR305_fixture_pipeline_tab: the fixture-review run shows its stages, artifacts and gate from its definition', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    seedFixtureRun(project)
    await renderRoute(`/projects/${project.id}/pipeline`)

    const summary = await screen.findByTestId('run-summary')
    expect(within(summary).getByRole('heading', { name: 'Fixture review' })).toBeInTheDocument()
    expect(within(summary).getByText('Summarise')).toBeInTheDocument()

    const stages = screen.getByRole('region', { name: 'Stages' })
    const rows = (await within(stages).findAllByRole('row')).slice(1)
    expect(rows.map((r) => within(r).getByRole('rowheader').textContent)).toEqual(['Draft', 'Check', 'Summarise'])
    expect(rows.map((r) => r.dataset.state)).toEqual(['completed', 'completed', 'active'])

    const artifacts = within(screen.getByRole('region', { name: 'Artifacts' }))
    expect((await artifacts.findAllByRole('button', { name: /v\d/ })).map((b) => b.textContent)).toEqual(['Draftv1', 'Check reportv2'])
    // JSON artifacts render as JSON.
    expect(await artifacts.findByRole('group', { name: 'Draft v1 (JSON)' })).toHaveTextContent('"title": "Q3 summary"')

    const timeline = within(screen.getByRole('region', { name: 'Timeline' }))
    expect(await timeline.findByText('Report approval raised')).toBeInTheDocument()
    expect(timeline.getByText('Check → Summarise')).toBeInTheDocument()
    expect(timeline.getByText('validation_rejected: /points: too few')).toBeInTheDocument()

    const prompts = within(screen.getByRole('region', { name: 'Prompt versions' }))
    const checker = await prompts.findByRole('table', { name: 'checker' })
    const versions = within(checker).getAllByRole('row').slice(1)
    expect(versions.map((r) => within(r).getByRole('rowheader').textContent)).toEqual(['v1', 'v2'])
    expect(versions[0]).toHaveTextContent('33.3%')
    expect(versions[0]).toHaveTextContent('—')
  })

  it('FR305_fixture_replay: the replay steps through the fixture run with its definition titles', async () => {
    signInAs()
    const project = seedProject({ name: 'P' })
    const stored = seedFixtureRun(project)
    await renderRoute(`/projects/${project.id}/pipeline/replay?runId=${stored.run.id}`)

    const slider = await screen.findByRole('slider', { name: 'Timeline position' })
    expect(slider).toHaveAttribute('max', '12')
    fireEvent.change(slider, { target: { value: '6' } })
    const entry = screen.getByRole('article', { name: 'Timeline entry' })
    expect(within(entry).getByText('checker run in Check')).toBeInTheDocument()
    expect(within(entry).getByRole('group', { name: 'Output' })).toHaveTextContent('{"ok":false}')
    fireEvent.change(slider, { target: { value: '12' } })
    expect(within(screen.getByRole('article', { name: 'Timeline entry' })).getByText('Report approval raised')).toBeInTheDocument()
  })
})
