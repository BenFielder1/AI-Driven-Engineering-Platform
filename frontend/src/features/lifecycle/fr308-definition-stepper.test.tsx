import { screen, waitFor, within } from '@testing-library/react'
import { delay, http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { seedInference } from '../../mocks/handlers/analysis'
import { sdlcDefinition } from '../../mocks/handlers/pipeline-definitions'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { fallbackSdlcStages, sdlcStagesFromDefinition } from './sdlc-stages'
import { adjacentStages } from './stage-nav'
import { stepperStages } from './stage-stepper'

const DEFINITION_ROUTE = '*/api/pipelines/:pipelineId/definitions/:hash'

/** The SDLC definition with other titles, to prove the stepper reads them. */
const retitled: Schemas['PipelineDefinitionDto'] = {
  ...sdlcDefinition,
  stages: sdlcDefinition.stages.map((s) =>
    s.id === 'specification' ? { ...s, title: 'Spec writing' } : s.id === 'planning' ? { ...s, title: 'Plan making' } : s,
  ),
}

const stepperLabels = (stepper: HTMLElement) =>
  within(stepper)
    .getAllByRole('link')
    .map((l) => l.firstChild?.nextSibling?.textContent)

const sixStages = ['Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release']

describe('FR-308: the stage stepper reads the SDLC definition', () => {
  it('FR308_stepper_titles_from_definition: titles come from the definition, routes stay the same', async () => {
    server.use(http.get(DEFINITION_ROUTE, () => HttpResponse.json(retitled)))
    const project = seedProjectAs('owner')
    await renderRoute(`/projects/${project.id}/refinement`)

    const stepper = await screen.findByTestId('stage-stepper')
    await waitFor(() => {
      expect(stepperLabels(stepper)).toEqual(['Refinement', 'Spec writing', 'Plan making', 'Execution', 'Review', 'Release'])
    })
    expect(within(stepper).getByRole('link', { name: /^Spec writing/ })).toHaveAttribute('href', `/projects/${project.id}/specification`)
    expect(screen.getByRole('link', { name: 'Next stage: Spec writing' })).toHaveAttribute('href', `/projects/${project.id}/specification`)
  })

  it('FR308_fallback_while_loading: the hard-coded list shows until the definition arrives', async () => {
    server.use(
      http.get(DEFINITION_ROUTE, async () => {
        await delay('infinite')
        return HttpResponse.json(retitled)
      }),
    )
    const project = seedProjectAs('owner')
    await renderRoute(`/projects/${project.id}/refinement`)
    const stepper = await screen.findByTestId('stage-stepper')
    expect(stepperLabels(stepper)).toEqual(sixStages)
    expect(await screen.findByRole('link', { name: 'Next stage: Specification' })).toBeInTheDocument()
  })

  it('FR308_fallback_on_error: a definition that fails to load keeps the hard-coded list', async () => {
    server.use(http.get(DEFINITION_ROUTE, () => problem(404, 'NOT_FOUND', 'The pipeline definition was not found.')))
    const project = seedProjectAs('owner')
    await renderRoute(`/projects/${project.id}/refinement`)
    const stepper = await screen.findByTestId('stage-stepper')
    await waitFor(() => {
      expect(stepperLabels(stepper)).toEqual(sixStages)
    })
  })

  it('FR308_analysis_first: a project in analysis shows Analysis first, as in 0.6.1', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedInference(p)
    })
    await renderRoute(`/projects/${project.id}/analysis`)
    const stepper = await screen.findByTestId('stage-stepper')
    await waitFor(() => {
      expect(stepperLabels(stepper)).toEqual(['Analysis', ...sixStages])
    })
    expect(within(stepper).getAllByRole('link')[0]).toHaveAttribute('aria-current', 'page')
  })

  it('FR308_stage_order_from_definition: order and membership follow the definition; unknown stages are left out', () => {
    const reordered: Schemas['PipelineDefinitionDto'] = {
      ...sdlcDefinition,
      stages: [
        ...sdlcDefinition.stages.filter((s) => s.id !== 'review'),
        { ...sdlcDefinition.stages[0], id: 'publish', title: 'Publish' } as Schemas['PipelineStageDefinitionDto'],
      ],
    }
    const stages = sdlcStagesFromDefinition(reordered)
    expect(stages.map((s) => s.stage)).toEqual(['analysis', 'refinement', 'specification', 'planning', 'execution', 'release'])
    expect(adjacentStages('execution', { stages })).toEqual({ previous: 'planning', next: 'release' })
    expect(adjacentStages('analysis', { stages })).toEqual({ previous: null, next: 'refinement' })
    expect(adjacentStages('refinement', { analysis: true, stages }).previous).toBe('analysis')

    expect(sdlcStagesFromDefinition(undefined)).toBe(fallbackSdlcStages)
    expect(sdlcStagesFromDefinition({ ...sdlcDefinition, stages: [] })).toBe(fallbackSdlcStages)
  })

  it('FR308_stepper_states: each stage keeps its state from the project; Analysis shows only when the statuses list it', () => {
    const statuses = [
      { stage: 'refinement' as const, state: 'done' as const, needsAction: 0 },
      { stage: 'specification' as const, state: 'current' as const, needsAction: 1 },
    ]
    const stages = stepperStages(sdlcStagesFromDefinition(retitled), statuses)
    expect(stages.map((s) => [s.stage, s.title, s.state, s.needsAction])).toEqual([
      ['refinement', 'Refinement', 'done', 0],
      ['specification', 'Spec writing', 'current', 1],
      ['planning', 'Plan making', 'upcoming', 0],
      ['execution', 'Execution', 'upcoming', 0],
      ['review', 'Review', 'upcoming', 0],
      ['release', 'Release', 'upcoming', 0],
    ])
    expect(stepperStages(fallbackSdlcStages, [{ stage: 'analysis', state: 'current', needsAction: 0 }])[0]?.stage).toBe('analysis')
  })
})
