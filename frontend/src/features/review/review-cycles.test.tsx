import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedOptimizationPlan } from '../../mocks/handlers/execution'
import type { StoredProject } from '../../mocks/handlers/projects'
import { categoryScores, seedComplianceReport, seedReviewPhase } from '../../mocks/handlers/review'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'

async function openReview(project: StoredProject, search = '') {
  const utils = renderRoute(`/projects/${project.id}/review${search}`)
  await screen.findByRole('heading', { level: 2, name: 'Review' })
  await screen.findByRole('region', { name: /^Review cycles/ })
  return { ...utils, user: userEvent.setup() }
}

/** Three cycles with different scores: 70%, 82%, 93%. */
function threeCycles() {
  return seedProjectAs('owner', (p) => {
    seedReviewPhase(p)
    seedComplianceReport(p, { categoryScores: categoryScores(0.7), decision: 'review_required', driftLevel: 'moderate' })
    seedComplianceReport(p, { categoryScores: categoryScores(0.82), decision: 'review_required', driftLevel: 'low' })
    seedComplianceReport(p, { categoryScores: categoryScores(0.93), decision: 'release_eligible' })
  })
}

const securityRow = () =>
  within(within(screen.getByRole('region', { name: 'Category breakdown' })).getByRole('table', { name: /^Compliance score per category/ })).getByRole('row', {
    name: /^Security/,
  })
const point = (cycle: number) => {
  const chart = within(screen.getByRole('region', { name: 'Compliance trend' })).getByRole('img', { name: /Compliance score by review cycle/ })
  const circle = chart.querySelector(`circle[data-cycle="${String(cycle)}"]`)
  if (!circle) {
    throw new Error(`no point for cycle ${String(cycle)}`)
  }
  return circle
}

describe('Review cycles (FR-56)', () => {
  it('FR56_lists_every_cycle_newest_first_with_score_threshold_drift_decision_and_date', async () => {
    const project = threeCycles()
    await openReview(project)

    const table = screen.getByRole('table', { name: /^Review cycles, newest first/ })
    const [, ...rows] = within(table).getAllByRole('row')
    const [newest] = rows
    if (!newest) {
      throw new Error('no cycle rows')
    }
    expect(rows.map((r) => within(r).getByRole('button').textContent)).toEqual(['Cycle 3', 'Cycle 2', 'Cycle 1'])
    expect(rows[2]).toHaveTextContent('70%')
    expect(rows[2]).toHaveTextContent('90%')
    expect(rows[2]).toHaveTextContent('Moderate drift')
    expect(rows[2]).toHaveTextContent('Another cycle needed')
    // The latest cycle is selected by default.
    expect(within(newest).getByRole('button')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('region', { name: /^Latest report: cycle 3/ })).toBeInTheDocument()
  })

  it('FR56_selecting_cycle_1_shows_its_category_scores_and_highlights_point_1', async () => {
    const project = threeCycles()
    const { user, router } = await openReview(project)
    expect(securityRow()).toHaveTextContent('93%')
    expect(point(3)).toHaveAttribute('data-selected', 'true')

    await user.click(screen.getByRole('button', { name: 'Cycle 1' }))

    expect(await screen.findByRole('region', { name: /^Cycle 1 report/ })).toBeInTheDocument()
    await waitFor(() => {
      expect(securityRow()).toHaveTextContent('70%')
    })
    expect(point(1)).toHaveAttribute('data-selected', 'true')
    expect(point(3)).toHaveAttribute('data-selected', 'false')
    expect(router.state.location.search).toMatchObject({ cycle: 1 })
    expect(screen.getByRole('button', { name: 'Cycle 1' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('FR56_clicking_a_trend_point_selects_that_cycle', async () => {
    const project = threeCycles()
    const { router } = await openReview(project)

    fireEvent.click(point(2))

    expect(await screen.findByRole('region', { name: /^Cycle 2 report/ })).toBeInTheDocument()
    expect(router.state.location.search).toMatchObject({ cycle: 2 })
    await waitFor(() => {
      expect(point(2)).toHaveAttribute('data-selected', 'true')
    })
  })

  it('FR56_the_cycle_in_the_url_is_shown', async () => {
    const project = threeCycles()
    await openReview(project, '?cycle=2')
    expect(await screen.findByRole('region', { name: /^Cycle 2 report/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cycle 2' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('FR56_detail_has_deterministic_findings_regressions_escalation_reason_and_the_plans_run', async () => {
    let planId = ''
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
      seedOptimizationPlan(p, 2, 1, undefined, 1)
      planId = p.artifacts.find((a) => a.type === 'optimization_plan')?.id ?? ''
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.72),
        decision: 'escalation_required',
        decisionReason: 'low_score',
        optimizationPlanArtifactId: planId,
        violations: [
          { category: 'security', severity: 'critical', source: 'deterministic', message: 'Secret in source', file: 'src/config.ts', evidence: null },
          { category: 'testing', severity: 'minor', source: 'agent', message: 'Few tests', file: null, evidence: null },
        ],
        regressions: [{ requirementId: 'FR-3', category: 'feature_coverage', description: 'Search no longer filters.', evidence: 'src/search.ts' }],
      })
    })
    await openReview(project)

    const report = screen.getByRole('region', { name: /^Latest report: cycle 1/ })
    expect(within(report).getByText('Why it escalated: Compliance score below 0.75.')).toBeInTheDocument()
    expect(within(report).getByRole('link', { name: 'See the execution run it produced' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/execution#run-${planId}`,
    )
    const deterministic = screen.getByRole('region', { name: 'Deterministic findings (1)' })
    expect(deterministic).toHaveTextContent('Secret in source')
    expect(deterministic).not.toHaveTextContent('Few tests')
    expect(screen.getByRole('region', { name: 'Regressions (1)' })).toHaveTextContent('FR-3: Search no longer filters.')
  })

  it('FR56_category_scores_chart_has_a_table_view_with_the_penalty', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
      const scores = categoryScores(0.9).map((c) => (c.category === 'security' ? { ...c, agentScore: 0.9, penalty: 0.2, score: 0.7 } : c))
      seedComplianceReport(p, { categoryScores: scores })
    })
    const { user } = await openReview(project)

    const chartCard = screen.getByRole('region', { name: 'Category scores' })
    expect(within(chartCard).getByRole('img', { name: /Security: agent 90%, penalty 20%, final 70%/ })).toBeInTheDocument()
    await user.click(within(chartCard).getByRole('button', { name: 'View as table' }))
    const row = within(within(chartCard).getByRole('table', { name: 'Score per category' })).getByRole('row', { name: /^Security/ })
    expect(row).toHaveTextContent('−20%')
    expect(row).toHaveTextContent('70%')
  })

  it('FR56_repeated_cycle_numbers_after_a_rollback_are_told_apart_by_report_id', async () => {
    let older = ''
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
      // Cycle 1, then a rollback restarts the count: cycle 1 again, then cycle 2.
      older = seedComplianceReport(p, { cycle: 1, categoryScores: categoryScores(0.7), decision: 'review_required' }).id
      seedComplianceReport(p, { cycle: 1, categoryScores: categoryScores(0.82), decision: 'review_required' })
      seedComplianceReport(p, { cycle: 2, categoryScores: categoryScores(0.93), decision: 'release_eligible' })
    })
    const { user, router } = await openReview(project)

    const table = screen.getByRole('table', { name: /^Review cycles, newest first/ })
    const [, ...rows] = within(table).getAllByRole('row')
    expect(rows.map((r) => within(r).getByRole('button').textContent)).toEqual(['Cycle 2', 'Cycle 1', 'Cycle 1'])
    const [, , oldest] = rows
    if (!oldest) {
      throw new Error('no rows')
    }

    await user.click(within(oldest).getByRole('button', { name: 'Cycle 1' }))
    await waitFor(() => {
      expect(securityRow()).toHaveTextContent('70%')
    })
    // The cycle repeats, so the URL names the exact report too.
    expect(router.state.location.search).toMatchObject({ cycle: 1, report: older })
    expect(within(oldest).getByRole('button')).toHaveAttribute('aria-pressed', 'true')
    const chart = within(screen.getByRole('region', { name: 'Compliance trend' })).getByRole('img', { name: /Compliance score by review cycle/ })
    expect(chart.querySelectorAll('circle[data-selected="true"]')).toHaveLength(1)
  })
})
