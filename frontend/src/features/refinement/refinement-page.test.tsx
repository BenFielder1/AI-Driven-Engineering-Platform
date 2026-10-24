import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { contextualErrorMessages, errorMessages } from '../../lib/errors'
import { problem } from '../../mocks/handlers/problem'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { makeReleaseVersion, type StoredProject } from '../../mocks/handlers/projects'
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
  const utils = await renderRoute(`/projects/${project.id}/refinement`)
  await screen.findByRole('heading', { level: 2, name: 'Refinement' })
  await waitFor(() => {
    expect(screen.queryByText('Loading refinement…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

const draftArticle = (version: number) => screen.findByRole('article', { name: `Project request v${String(version)}` })

describe('Refinement page (FR-27)', () => {
  it('FR55_FR27_every_round_is_listed_newest_first_with_its_questions_and_the_answers_given', async () => {
    const project = seedInRefinement('owner', (p) => {
      seedRefinementRound(p, { draft: '# v1', questions: questions.slice(0, 2) })
      seedRefinementRound(p, {
        draft: '# v2',
        answers: [{ questionId: 'q1', question: 'Who uses the tracker?', answer: 'Warehouse staff.' }],
        questions: [{ id: 'q9', question: 'Which ERP?', reason: null }],
      })
    })
    const { user } = await openRefinement(project)

    const history = screen.getByRole('list', { name: 'Refinement rounds, newest first' })
    const items = within(history).getAllByTestId('refinement-round')
    expect(items).toHaveLength(2)
    const [latest, first] = items
    if (!latest || !first) {
      throw new Error('rounds')
    }
    // The latest round is expanded; older rounds are collapsed.
    expect(latest.querySelector('details')).toHaveAttribute('open')
    expect(first.querySelector('details')).not.toHaveAttribute('open')
    expect(within(latest).getByText('Round 2')).toBeInTheDocument()
    expect(within(latest).getByText('Which ERP?')).toBeVisible()
    expect(within(latest).getByText('Warehouse staff.')).toBeInTheDocument()

    await user.click(within(first).getByText('Round 1'))
    expect(first.querySelector('details')).toHaveAttribute('open')
    expect(within(first).getByText('Who uses the tracker?')).toBeInTheDocument()
    expect(within(first).getByText('How many warehouses?')).toBeInTheDocument()
    // Round 1's question carries the answer round 2 sent for it; the unanswered one has none.
    expect(within(first).getAllByText('Your answer:', { exact: false })).toHaveLength(1)
    expect(within(first).getByText(/draft v1/)).toBeInTheDocument()
    // Read-only: no answer fields in the history.
    expect(within(history).queryByRole('textbox')).not.toBeInTheDocument()
  })

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
    expect(within(screen.getByRole('region', { name: 'Clarifying questions' })).getByText('Shapes the roles.')).toBeInTheDocument()
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
    expect(within(screen.getByRole('region', { name: 'Clarifying questions' })).getByText('How many warehouses?')).toBeInTheDocument()
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

describe('Refinement page → codebase summary (FR-70)', () => {
  const analystRunId = '0192a7e2-0000-7000-8000-00000000abcd'

  /** Released 1.1.0, 1.2.0 in development from base tag v1.1.0, in Refinement, with the analyst in `status`. */
  function seedIteration(status: Schemas['CodebaseSummaryState'], setup?: (p: StoredProject) => void) {
    return seedInRefinement('owner', (p) => {
      const development = makeReleaseVersion({
        version: '1.2.0',
        baseTag: 'v1.1.0',
        baseCommitSha: 'abc1234def5678',
        request: 'Add CSV export',
      })
      p.releaseVersions = [makeReleaseVersion({ version: '1.1.0', status: 'released' }), development]
      p.analyst = {
        [development.id]: { status, runId: status === 'queued' ? null : analystRunId, artifactId: null, artifactVersion: null },
      }
      setup?.(p)
    })
  }

  it('FR70_summary_shown_locked_and_generated_by_the_analyst', async () => {
    const project = seedIteration('completed', (p) => {
      seedArtifact(p, {
        type: 'codebase_summary',
        status: 'locked',
        content: '# Codebase summary\n\n## Overview\n\nA shop built with Express.',
      })
    })
    await openRefinement(project)

    const section = await screen.findByRole('region', { name: 'Codebase summary' })
    expect(await within(section).findByText('Locked')).toBeInTheDocument()
    expect(within(section).getByText(/Generated by the Repository Analyst/)).toBeInTheDocument()
    const article = within(section).getByRole('article', { name: 'Codebase summary v1' })
    expect(within(article).getByRole('heading', { name: 'Overview' })).toBeInTheDocument()
    expect(within(article).getByText('A shop built with Express.')).toBeInTheDocument()
    expect(within(section).getByRole('link', { name: 'View the Repository Analyst run' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/runs?run=${analystRunId}`,
    )
  })

  it.each(['queued', 'running'] as const)('FR70_summary_being_generated_while_the_analyst_is_%s', async (status) => {
    const project = seedIteration(status)
    await openRefinement(project)

    const section = await screen.findByRole('region', { name: 'Codebase summary' })
    expect(await within(section).findByRole('status')).toHaveTextContent('Summary being generated…')
    expect(within(section).getByRole('status')).toHaveTextContent(/waits up to 10 minutes/)
  })

  it.each([
    ['failed', 'The Repository Analyst failed'],
    ['timed_out', 'The codebase summary took too long'],
  ] as const)('FR70_analyst_%s_explains_refinement_continued_and_links_the_run', async (status, title) => {
    const project = seedIteration(status)
    await openRefinement(project)

    const section = await screen.findByRole('region', { name: 'Codebase summary' })
    expect(await within(section).findByText(title)).toBeInTheDocument()
    expect(within(section).getByText(/refinement (no longer waits for it and )?continued without it/)).toBeInTheDocument()
    expect(within(section).getByRole('link', { name: 'View the Repository Analyst run' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/runs?run=${analystRunId}`,
    )
  })

  it('FR70_no_summary_without_existing_code', async () => {
    const project = seedInRefinement('owner')
    await openRefinement(project)

    const section = await screen.findByRole('region', { name: 'Codebase summary' })
    expect(await within(section).findByText(/this version starts without existing code/)).toBeInTheDocument()
    expect(within(section).queryByText('Summary being generated…')).not.toBeInTheDocument()
  })
})
