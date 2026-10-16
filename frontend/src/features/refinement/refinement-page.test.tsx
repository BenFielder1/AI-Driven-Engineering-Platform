import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages, errorMessages } from '../../lib/errors'
import { problem } from '../../mocks/handlers/problem'
import type { StoredProject } from '../../mocks/handlers/projects'
import { failRefinementRound, finishRefinementRound, seedRefinementRound } from '../../mocks/handlers/refinement'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import type { ProjectRole } from '../../lib/permissions'

const questions = [
  { id: 'q1', question: 'Who uses the tracker?', reason: 'Shapes the roles.' },
  { id: 'q2', question: 'How many warehouses?', reason: null },
  { id: 'q3', question: 'Do you need barcode scanning?', reason: null },
]

function seedInRefinement(role: ProjectRole, setup?: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    p.currentPhase = 'refinement'
    setup?.(p)
  })
}

async function openRefinement(project: StoredProject) {
  const utils = renderRoute(`/projects/${project.id}/refinement`)
  await screen.findByRole('heading', { level: 2, name: 'Refinement' })
  await waitFor(() => {
    expect(screen.queryByText('Loading refinement…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

const draftArticle = (version: number) => screen.findByRole('article', { name: `Project request v${String(version)}` })

describe('Refinement page (FR-27)', () => {
  it('FR27_start_round_on_idea_then_shows_draft_and_questions', async () => {
    const project = seedInRefinement('contributor')
    const { user, live } = await openRefinement(project)

    expect(screen.getByRole('heading', { name: 'No project request yet' })).toBeInTheDocument()
    expect(screen.getByText(project.idea)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Start refinement' }))

    expect(await screen.findByText(/The Idea Refinement agent is about to start/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start refinement' })).not.toBeInTheDocument()
    const [request] = requestsTo('POST', '/refinement/run')
    expect(request?.body).toEqual({})

    finishRefinementRound(project, { draft: '# Inventory tracker\n\nTrack stock.', questions })
    live.latest.emit(projectEvent(project.id, 'run', { type: 'RUN_COMPLETED' }))

    const article = await draftArticle(1)
    expect(within(article).getByRole('heading', { name: 'Inventory tracker' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Project request' })).toHaveTextContent('v1')
    expect(screen.getByLabelText('Who uses the tracker?')).toHaveValue('')
    expect(screen.getByText('Shapes the roles.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run another round' })).toBeInTheDocument()
  })

  it('FR27_answer_some_questions_and_run_another_round', async () => {
    const project = seedInRefinement('owner', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
    })
    const { user } = await openRefinement(project)
    await draftArticle(1)

    await user.type(screen.getByLabelText('Who uses the tracker?'), 'Warehouse staff')
    await user.type(screen.getByLabelText('Do you need barcode scanning?'), '  Yes  ')
    await user.click(screen.getByRole('button', { name: 'Run another round' }))

    const round = await screen.findByRole('region', { name: 'Latest round' })
    expect(await within(round).findByText(/working|about to start/)).toBeInTheDocument()
    expect(within(round).getByRole('heading', { name: 'Answers sent with this round' })).toBeInTheDocument()
    expect(within(round).getByText('Warehouse staff')).toBeInTheDocument()

    const [request] = requestsTo('POST', '/refinement/run')
    expect(request?.body).toEqual({
      answers: [
        { questionId: 'q1', answer: 'Warehouse staff' },
        { questionId: 'q3', answer: 'Yes' },
      ],
    })
    // While the round runs, the questions are read-only and show the answers given.
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.getByText('These answers were sent with the round in progress.')).toBeInTheDocument()
  })

  it('FR27_answers_longer_than_the_limit_are_rejected_before_sending', async () => {
    const project = seedInRefinement('owner', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
    })
    const { user } = await openRefinement(project)
    await draftArticle(1)

    const field = screen.getByLabelText('Who uses the tracker?')
    await user.click(field)
    await user.paste('x'.repeat(4001))
    await user.click(screen.getByRole('button', { name: 'Run another round' }))

    expect(await screen.findAllByText('Answers are at most 4,000 characters.')).not.toHaveLength(0)
    expect(field).toHaveAttribute('aria-invalid', 'true')
    expect(requestsTo('POST', '/refinement/run')).toHaveLength(0)
  })

  it('FR27_failed_round_shows_the_error_and_allows_a_retry', async () => {
    const project = seedInRefinement('contributor', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
      seedRefinementRound(p, { status: 'running', answers: [{ questionId: 'q1', question: 'Who uses the tracker?', answer: 'Staff' }] })
      failRefinementRound(p, 'INVALID_OUTPUT: The reply was not JSON.')
    })
    await openRefinement(project)

    const alert = (await screen.findByText(errorMessages.INVALID_OUTPUT, { exact: false })).closest('[role="alert"]')
    expect(alert).toHaveTextContent(errorMessages.INVALID_OUTPUT)
    expect(alert).toHaveTextContent('The reply was not JSON.')
    expect(screen.getByRole('button', { name: 'Run another round' })).toBeInTheDocument()
  })

  it('FR27_cancelled_round_shows_its_reason_and_allows_another_round', async () => {
    const project = seedInRefinement('contributor', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
      seedRefinementRound(p, { status: 'cancelled', error: 'The project no longer allows a new draft: a release is in progress.' })
    })
    await openRefinement(project)

    const title = await screen.findByText(/The round was cancelled before it produced a draft/)
    const alert = title.parentElement
    expect(alert).toHaveTextContent('The project no longer allows a new draft: a release is in progress.')
    expect(screen.getByRole('button', { name: 'Run another round' })).toBeEnabled()
  })

  it('FR27_refinement_in_progress_error_has_copy', async () => {
    const project = seedInRefinement('owner')
    server.use(
      http.post('*/api/projects/:id/refinement/run', () =>
        problem(409, 'REFINEMENT_IN_PROGRESS', 'A refinement round is already queued or running.'),
      ),
    )
    const { user } = await openRefinement(project)

    await user.click(screen.getByRole('button', { name: 'Start refinement' }))

    expect(await screen.findByText(errorMessages.REFINEMENT_IN_PROGRESS)).toBeInTheDocument()
  })

  it('FR27_viewers_see_questions_without_answer_fields', async () => {
    const project = seedInRefinement('viewer', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
    })
    await openRefinement(project)
    await draftArticle(1)

    expect(screen.getByText(/Only Contributors and Owners can answer questions/)).toBeInTheDocument()
    expect(screen.getByText('How many warehouses?')).toBeInTheDocument()
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Run another round' })).not.toBeInTheDocument()
  })

  it('FR27_outside_the_refinement_phase_the_page_is_read_only', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedRefinementRound(p, { draft: '# Draft one', questions })
    })
    await openRefinement(project)
    await draftArticle(1)

    expect(screen.getByText(/Refinement rounds run only in the Refinement phase/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /round|refinement/i })).not.toBeInTheDocument()
    expect(contextualErrorMessages.startRefinement.WRONG_PHASE).toMatch(/Refinement phase/)
  })
})
