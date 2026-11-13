// FR-197: copy that names the product says Tevrin (it said AIDE before 0.6.0); the assertions below use the new copy.
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { ProjectRole } from '../../lib/permissions'
import { contextualErrorMessages } from '../../lib/errors'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import type { StoredProject } from '../../mocks/handlers/projects'
import { categoryScores, seedComplianceReport, seedReviewPhase } from '../../mocks/handlers/review'
import { seedProjectAs } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'
import { escalationOptionsFor } from '../projects/labels'

function seedReview(role: ProjectRole, setup?: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    seedReviewPhase(p)
    setup?.(p)
  })
}

async function openReview(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/review`)
  await screen.findByRole('heading', { level: 2, name: 'Review' })
  await waitFor(() => {
    expect(screen.queryByText('Loading review…')).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

const region = (name: string | RegExp) => screen.getByRole('region', { name })
const statusRegion = () => region('Review status')
const latestRegion = () => region(/^Latest report/)

describe('Review page (FR-38–FR-41)', () => {
  it('FR41_trend_shows_three_points_for_three_cycles', async () => {
    const project = seedReview('owner', (p) => {
      seedComplianceReport(p, { categoryScores: categoryScores(0.82), decision: 'review_required', driftLevel: 'low' })
      seedComplianceReport(p, { categoryScores: categoryScores(0.86), decision: 'review_required', driftLevel: 'low' })
      seedComplianceReport(p, { categoryScores: categoryScores(0.93), decision: 'release_eligible' })
    })
    await openReview(project)

    const trend = region('Compliance trend')
    const chart = within(trend).getByRole('img', { name: /Compliance score by review cycle/ })
    expect(chart).toHaveAccessibleName(
      // FR-56: the latest cycle is selected by default.
      'Compliance score by review cycle Cycle 1: 82%, Cycle 2: 86%, Cycle 3: 93%. Threshold 90%. Cycle 3 is selected.',
    )
    expect([...chart.querySelectorAll('circle[data-cycle]')].map((c) => c.getAttribute('data-cycle'))).toEqual(['1', '2', '3'])
    // SPEC 7.1: points are coloured by decision.
    expect([...chart.querySelectorAll('circle[data-cycle]')].map((c) => c.getAttribute('data-decision'))).toEqual([
      'review_required',
      'review_required',
      'release_eligible',
    ])
    // The threshold is a dashed reference line labelled with its value.
    expect(chart.querySelector('.recharts-reference-line line[stroke-dasharray]')).not.toBeNull()

    // FR53_trend_chart: the data table is one toggle away ("View as table").
    await userEvent.setup().click(within(trend).getByRole('button', { name: 'View as table' }))
    const table = within(trend).getByRole('table', { name: 'Compliance score by review cycle, oldest first' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows.map((r) => within(r).getAllByRole('cell')[1]?.textContent)).toEqual([
      '82% (below threshold)',
      '86% (below threshold)',
      '93% (meets threshold)',
    ])
    expect(rows.map((r) => within(r).getAllByRole('cell')[2]?.textContent)).toEqual(['90%', '90%', '90%'])
    expect(rows.map((r) => within(r).getAllByRole('cell')[4]?.textContent)).toEqual(['Another cycle needed', 'Another cycle needed', 'Release eligible'])
    expect(within(statusRegion()).getByText('Cycle 3 of 3')).toBeInTheDocument()
  })

  it('FR41_trend_uses_each_reports_threshold', async () => {
    const project = seedReview('owner', (p) => {
      // Cycle 1 was decided against 85%, then the Owner raised the threshold to 90%.
      seedComplianceReport(p, { categoryScores: categoryScores(0.86), threshold: 0.85, decision: 'release_eligible' })
      p.complianceThreshold = 0.9
      seedComplianceReport(p, { categoryScores: categoryScores(0.88), threshold: 0.9, decision: 'review_required' })
    })
    await openReview(project)

    const trend = region('Compliance trend')
    const chart = within(trend).getByRole('img', { name: /Compliance score by review cycle/ })
    expect(chart).toHaveAccessibleName(
      'Compliance score by review cycle Cycle 1: 86% against a 85% threshold, Cycle 2: 88% against a 90% threshold. Cycle 2 is selected.',
    )
    // A changing threshold is drawn as a dashed step line, and each point is judged against its own report's threshold.
    expect(chart.querySelector('.recharts-line-curve[stroke-dasharray]')).not.toBeNull()
    expect(chart.querySelector('.recharts-reference-line')).toBeNull()
    expect([...chart.querySelectorAll('circle[data-cycle]')].map((c) => c.getAttribute('data-meets'))).toEqual(['true', 'false'])

    await userEvent.setup().click(within(trend).getByRole('button', { name: 'View as table' }))
    const rows = within(within(trend).getByRole('table')).getAllByRole('row').slice(1)
    expect(rows.map((r) => within(r).getAllByRole('cell')[1]?.textContent)).toEqual(['86% (meets threshold)', '88% (below threshold)'])
    expect(rows.map((r) => within(r).getAllByRole('cell')[2]?.textContent)).toEqual(['85%', '90%'])
  })

  it('FR41_category_breakdown_and_evidence', async () => {
    const project = seedReview('viewer', (p) => {
      const scores = categoryScores(0.9).map((c) =>
        c.category === 'security'
          ? {
              ...c,
              agentScore: 0.9,
              penalty: 0.2,
              score: 0.7,
              evidence: [
                { file: 'src/config.ts', reason: 'Hard-coded API key' },
                { file: null, reason: 'No rate limiting on login' },
              ],
            }
          : c,
      )
      seedComplianceReport(p, {
        categoryScores: scores,
        decision: 'review_required',
        violations: [
          { category: 'security', severity: 'critical', source: 'deterministic', message: 'Secret in source', file: 'src/config.ts', evidence: 'sk-…' },
          { category: 'testing', severity: 'minor', source: 'agent', message: 'No tests for export', file: null, evidence: null },
          { category: 'api_contracts', severity: 'major', source: 'deterministic', message: 'GET /items/{id} missing', file: null, evidence: null },
        ],
        missingFeatures: ['CSV export'],
        driftLevel: 'moderate',
        driftAreas: [{ area: 'Persistence', severity: 'major', description: 'Uses SQLite, spec says PostgreSQL' }],
      })
    })
    const { user } = await openReview(project)

    const breakdown = within(region('Category breakdown')).getByRole('table')
    const security = within(breakdown).getByRole('row', { name: /^Security/ })
    expect(within(security).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      '10%',
      '90%',
      '−20%',
      '70%',
      'Show evidence (2) for Security',
    ])
    expect(within(breakdown).getAllByRole('rowheader').map((h) => h.textContent)).toEqual([
      'Feature coverage',
      'API contracts',
      'Data model',
      'Architecture',
      'Security',
      'Testing',
    ])

    // Evidence is collapsed until asked for, and the toggle says which category it belongs to.
    expect(screen.queryByRole('list', { name: 'Evidence for Security' })).not.toBeInTheDocument()
    const toggle = within(security).getByRole('button', { name: 'Show evidence (2) for Security' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    const evidence = screen.getByRole('list', { name: 'Evidence for Security' })
    expect(within(evidence).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'src/config.ts— Hard-coded API key',
      'No rate limiting on login',
    ])

    // Drift level and areas.
    const drift = region('Drift from the specification')
    expect(within(drift).getByText('Moderate drift')).toBeInTheDocument()
    expect(within(within(drift).getByRole('list', { name: 'Drift areas' })).getByText('Persistence')).toBeInTheDocument()

    // Violations grouped by severity (critical first), with category, source and file.
    const violations = region('Violations (3)')
    expect(within(violations).getAllByRole('heading', { level: 4 }).map((h) => h.textContent)).toEqual([
      'Critical (1 violation)',
      'Major (1 violation)',
      'Minor (1 violation)',
    ])
    const critical = within(violations).getByRole('region', { name: 'Critical (1 violation)' })
    expect(within(critical).getByText('Secret in source')).toBeInTheDocument()
    expect(within(critical).getByText('Category: Security')).toBeInTheDocument()
    expect(within(critical).getByText('Source: Deterministic check')).toBeInTheDocument()
    expect(within(critical).getByText('src/config.ts')).toBeInTheDocument()
    expect(within(violations).getByText('Source: Code review agent')).toBeInTheDocument()

    expect(within(region('Missing features (1)')).getByText('CSV export')).toBeInTheDocument()
  })

  it('FR38_run_review_button_and_states', async () => {
    const project = seedReview('contributor')
    const { user, live } = await openReview(project)

    // Empty: no report yet.
    expect(screen.getByText('No review yet')).toBeInTheDocument()
    expect(within(statusRegion()).getByText('Not running')).toBeInTheDocument()
    expect(within(statusRegion()).getByText('No review cycle yet (up to 3 allowed).')).toBeInTheDocument()

    // Running: the 202 shows the pending state until the live event arrives, and the button can't be pressed again.
    await user.click(screen.getByRole('button', { name: 'Run review' }))
    expect(await within(statusRegion()).findByText(/Review cycle 1 running/)).toBeInTheDocument()
    expect(within(statusRegion()).getByText('Running')).toBeInTheDocument()
    expect(requestsTo('POST', '/review')).toHaveLength(1)
    const button = screen.getByRole('button', { name: 'Run review' })
    expect(button).toBeDisabled()
    expect(button).toHaveAccessibleDescription('A review is already running. Its result appears here when it finishes.')

    // Completed: the live review event refreshes /state and the report.
    seedComplianceReport(project, { categoryScores: categoryScores(0.93) })
    live.latest.emit(projectEvent(project.id, 'review', { type: 'REVIEW_COMPLETED' }))
    expect(await within(statusRegion()).findByText('Completed')).toBeInTheDocument()
    expect(await screen.findByRole('region', { name: 'Latest report: cycle 1, 0.1.0 · checkpoint 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run review' })).toBeEnabled()

    // Failed.
    project.state.review.status = 'failed'
    live.latest.emit(projectEvent(project.id, 'review', { type: 'REVIEW_FAILED' }))
    const failed = await within(statusRegion()).findByText('The last review failed')
    expect(failed).toBeInTheDocument()
    expect(within(statusRegion()).getByRole('link', { name: 'See the agent runs' })).toBeInTheDocument()
  })

  it('FR38_review_in_progress_and_wrong_phase', async () => {
    const project = seedReview('owner')
    const { user } = await openReview(project)

    // Someone else started a review after the page loaded.
    project.state.review.status = 'running'
    await user.click(screen.getByRole('button', { name: 'Run review' }))
    expect(await screen.findByText(contextualErrorMessages.startReview.REVIEW_IN_PROGRESS)).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Run review' })).toBeDisabled()
  })

  it('FR38_run_review_is_disabled_outside_the_review_phase', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'execution'
    })
    await openReview(project)

    const button = screen.getByRole('button', { name: 'Run review' })
    expect(button).toBeDisabled()
    expect(button).toHaveAccessibleDescription('Reviews run only in the Review phase; the project is in Execution.')
    expect(screen.getByText('Reviews start once execution completes and the project enters Review.')).toBeInTheDocument()
  })

  it('FR38_viewer_has_no_run_button', async () => {
    const project = seedReview('viewer', (p) => seedComplianceReport(p))
    await openReview(project)

    expect(screen.queryByRole('button', { name: 'Run review' })).not.toBeInTheDocument()
    expect(screen.getByText('You can follow reviews here. Only Contributors and Owners can run one.')).toBeInTheDocument()
    expect(latestRegion()).toBeInTheDocument()
  })
})

describe('Review decision copy (FR-39, FR-40, FR-14)', () => {
  it('FR39_decision_copy_release_eligible', async () => {
    const project = seedReview('owner', (p) => seedComplianceReport(p, { categoryScores: categoryScores(0.93) }))
    await openReview(project)

    const latest = latestRegion()
    expect(within(latest).getByText('93%')).toBeInTheDocument()
    expect(within(latest).getByText('meets the 90% threshold')).toBeInTheDocument()
    expect(within(latest).getByText('Release eligible')).toBeInTheDocument()
    expect(
      within(latest).getByText('The score meets the threshold. A pre-release gate asks an Owner to approve the release.'),
    ).toBeInTheDocument()
    expect(within(latest).queryByText(/Why it escalated/)).not.toBeInTheDocument()
  })

  it('FR89_a_missing_vercel_json_is_a_deterministic_finding_with_its_file', async () => {
    const message =
      `vercel.json is missing: add it with git.deploymentEnabled { "tevrin/**": false, "aide/**": false } so Vercel doesn't deploy Tevrin branches on its own (FR-89).`
    const project = seedReview('owner', (p) => {
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.85),
        decision: 'review_required',
        violations: [{ category: 'architecture', severity: 'major', source: 'deterministic', message, file: 'vercel.json', evidence: null }],
      })
    })
    await openReview(project)

    const findings = region(/^Deterministic findings/)
    expect(within(findings).getByText(message)).toBeInTheDocument()
    expect(within(findings).getByText(/· vercel\.json$/)).toBeInTheDocument()
  })

  it('FR39_decision_copy_review_required_links_the_optimisation_plan_that_needs_approval', async () => {
    const project = seedReview('contributor', (p) => {
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.82),
        decision: 'review_required',
        optimizationPlanArtifactId: seedArtifact(p, { type: 'optimization_plan', content: '# Optimisation plan' }).id,
      })
    })
    await openReview(project)

    const latest = latestRegion()
    expect(within(latest).getByText('below the 90% threshold')).toBeInTheDocument()
    expect(within(latest).getByText('Another cycle needed')).toBeInTheDocument()
    expect(within(latest).getByText(/An Owner must approve it before execution re-enters/)).toBeInTheDocument()
    expect(within(latest).getByRole('link', { name: 'Open the optimisation plan' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/artifacts/optimization_plan?version=1`,
    )
  })

  it('FR39_decision_copy_escalation_names_the_reason', async () => {
    const project = seedReview('owner', (p) => {
      seedComplianceReport(p, { categoryScores: categoryScores(0.84), decision: 'review_required' })
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.83),
        decision: 'escalation_required',
        decisionReason: 'no_improvement',
      })
    })
    await openReview(project)

    const latest = latestRegion()
    expect(within(latest).getByText('Escalated to an Owner')).toBeInTheDocument()
    expect(within(latest).getByText('Why it escalated: No improvement since the last review cycle.')).toBeInTheDocument()
    expect(within(latest).getByText('The score did not improve on the previous review cycle.')).toBeInTheDocument()
  })

  it('FR39_escalation_note_copy', async () => {
    const project = seedReview('owner', (p) => {
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.7),
        decision: 'escalation_required',
        decisionReason: 'low_score',
        optimizationPlanArtifactId: seedArtifact(p, { type: 'optimization_plan', content: '# Optimisation plan' }).id,
      })
    })
    await openReview(project)

    const latest = latestRegion()
    expect(
      within(latest).getByText(/An Owner must decide the escalation gate first; Continue then lets an approved plan re-enter execution/),
    ).toBeInTheDocument()
    expect(within(latest).queryByText(/An Owner must approve it before execution re-enters/)).not.toBeInTheDocument()
  })

  it('FR39_no_optimization_plan_label', async () => {
    const project = seedReview('owner', (p) => {
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.84),
        decision: 'escalation_required',
        decisionReason: 'no_optimization_plan',
      })
    })
    await openReview(project)

    const latest = latestRegion()
    expect(
      within(latest).getByText('Why it escalated: Another cycle was needed but the review proposed no optimisation steps.'),
    ).toBeInTheDocument()
    expect(within(latest).getByText(/even after a corrective re-run the review proposed no optimisation steps/)).toBeInTheDocument()
    // FR-14: continue on this escalation runs another review, as there is no plan to execute.
    expect(escalationOptionsFor('no_optimization_plan').find((o) => o.value === 'continue')?.description).toMatch(
      /no optimisation plan to execute, so review runs again/,
    )
    expect(
      describeActivity({
        type: 'REVIEW_COMPLETED',
        payload: { cycle: 2, overallScore: 0.84, decision: 'escalation_required', decisionReason: 'no_optimization_plan' },
        summary: 'server summary',
      }),
    ).toBe('Review cycle 2 completed: score 84%, escalated to an Owner (another cycle was needed but the review proposed no optimisation steps)')
  })

  it('FR237_multi_line_evidence_renders_in_a_scrolling_pre_and_keeps_its_lines', async () => {
    const log = ['Run npm test', 'FAIL src/cart.test.ts', '  ● total › adds tax', '    Expected: 110', '    Received: 100'].join('\n')
    const project = seedReview('viewer', (p) => {
      seedComplianceReport(p, {
        decision: 'review_required',
        violations: [
          { category: 'testing', severity: 'major', source: 'deterministic', message: 'Required check "test" failed', file: null, evidence: log },
        ],
      })
    })
    await openReview(project)

    const violations = region('Violations (1)')
    const block = within(violations).getByLabelText('Evidence for Required check "test" failed')
    expect(block.tagName).toBe('PRE')
    expect(block.textContent).toBe(log)
    expect(block.textContent.split('\n')).toHaveLength(5)
    expect(block).toHaveAttribute('tabindex', '0')
    expect(block).toHaveClass('whitespace-pre', 'overflow-auto', 'max-h-64', 'font-mono')
  })

  it('FR237_single_line_evidence_stays_inline', async () => {
    const project = seedReview('viewer', (p) => {
      seedComplianceReport(p, {
        decision: 'review_required',
        violations: [
          { category: 'security', severity: 'critical', source: 'deterministic', message: 'Secret in source', file: 'src/config.ts', evidence: 'sk-…' },
        ],
      })
    })
    await openReview(project)

    const violations = region('Violations (1)')
    const evidence = within(violations).getByText('Evidence: sk-…')
    expect(evidence.tagName).toBe('SPAN')
    expect(violations.querySelector('pre')).toBeNull()
  })

  it('NFR-A11Y: review sections are labelled regions and the chart is a described image', async () => {
    const project = seedReview('owner', (p) => seedComplianceReport(p))
    await openReview(project)

    for (const name of [
      'Review status',
      'Latest report: cycle 1, 0.1.0 · checkpoint 1',
      'Category breakdown',
      'Compliance trend',
      'Drift from the specification',
      'Violations (0)',
      'Missing features (0)',
    ]) {
      expect(region(name)).toBeInTheDocument()
    }
    expect(screen.getByRole('img', { name: /^Compliance score by review cycle Cycle 1: 93%/ })).toBeInTheDocument()
    const breakdown = within(region('Category breakdown')).getByRole('table')
    expect(breakdown).toHaveAccessibleName(/Compliance score per category/)
    const toggle = within(breakdown).getByRole('button', { name: 'Show evidence (1) for Security' })
    expect(document.getElementById(toggle.getAttribute('aria-controls') ?? '')).not.toBeNull()
  })
})

describe('Review activity copy (FR-50)', () => {
  const describe_ = (type: string, payload: Record<string, unknown>) =>
    describeActivity({ type, payload, summary: 'server summary' })

  it('FR38_ activity: review events are described from their payloads', () => {
    expect(describe_('REVIEW_STARTED', { cycle: 2 })).toBe('Review cycle 2 started')
    expect(describe_('REVIEW_COMPLETED', { cycle: 2, overallScore: 0.825, decision: 'review_required' })).toBe(
      'Review cycle 2 completed: score 82.5%, another cycle needed',
    )
    expect(
      describe_('REVIEW_COMPLETED', {
        cycle: 3,
        overallScore: 0.7,
        decision: 'escalation_required',
        decisionReason: 'low_score',
      }),
    ).toBe('Review cycle 3 completed: score 70%, escalated to an Owner (compliance score below 0.75)')
    expect(describe_('REVIEW_FAILED', { cycle: 1 })).toBe('Review cycle 1 failed')
    expect(describe_('REVIEW_COMPLETED', {})).toBe('Review completed')
  })
})

describe('Review report finding handling (FR-249, FR-250)', () => {
  it('FR250_report_shows_step_waived_and_refused_labels_next_to_findings', async () => {
    const project = seedReview('viewer', (p) => {
      seedComplianceReport(p, {
        decision: 'review_required',
        violations: [
          {
            category: 'testing',
            severity: 'major',
            source: 'deterministic',
            message: 'Endpoint GET /api/orders has no test',
            file: null,
            evidence: null,
            findingId: 'finding:endpoint-untested:GET /api/orders',
            handling: 'step',
            stepNumber: 2,
            waivedReason: null,
          },
          {
            category: 'architecture',
            severity: 'major',
            source: 'deterministic',
            message: 'Dependency "tsx" is not in the allowlist',
            file: 'package.json',
            evidence: null,
            findingId: 'finding:dependency:tsx',
            handling: 'waived',
            stepNumber: null,
            waivedReason: 'tsx is a dev dependency used only by scripts',
          },
          {
            category: 'security',
            severity: 'critical',
            source: 'deterministic',
            message: 'Secret in source',
            file: 'src/config.ts',
            evidence: null,
            findingId: 'finding:secret:src/config.ts',
            handling: 'waiver_refused',
            stepNumber: 3,
            waivedReason: null,
          },
        ],
      })
    })
    await openReview(project)

    const violations = region('Violations (3)')
    const items = within(violations).getAllByTestId('violation')
    const item = (message: string) => {
      const found = items.find((li) => li.textContent.includes(message))
      if (!found) {
        throw new Error(`No violation "${message}"`)
      }
      return found
    }

    const stepped = item('Endpoint GET /api/orders has no test')
    expect(within(stepped).getByTestId('finding-handling')).toHaveTextContent(/^Step 2$/)
    expect(within(stepped).getByText('finding:endpoint-untested:GET /api/orders').tagName).toBe('CODE')

    const refused = item('Secret in source')
    expect(within(refused).getByTestId('finding-handling')).toHaveTextContent(/^Waiver refused · Step 3$/)
    expect(within(refused).queryByText('Waived')).not.toBeInTheDocument()

    // FR-249: the waived finding has a neutral Waived badge and its reason, in its own group apart from the counted ones.
    const waived = item('Dependency "tsx" is not in the allowlist')
    expect(within(waived).getByText('Waived')).toBeInTheDocument()
    expect(within(waived).getByTestId('finding-handling')).toHaveTextContent(
      /^Waived: tsx is a dev dependency used only by scripts$/,
    )
    const waivedGroup = within(violations).getByRole('region', { name: 'Waived findings (1)' })
    expect(waivedGroup).toContainElement(waived)
    expect(waivedGroup).not.toContainElement(stepped)
    expect(within(violations).getByRole('region', { name: 'Major (1 violation)' })).toContainElement(stepped)
    expect(within(violations).getByRole('region', { name: 'Critical (1 violation)' })).toContainElement(refused)
  })

  it('FR250_finding_without_handling_shows_no_label', async () => {
    const project = seedReview('viewer', (p) => {
      seedComplianceReport(p, {
        decision: 'review_required',
        violations: [
          // A report stored before 0.6.1 part 2: no handling fields at all, and one with them explicitly null.
          { category: 'security', severity: 'critical', source: 'deterministic', message: 'Secret in source', file: 'src/config.ts', evidence: null },
          {
            category: 'testing',
            severity: 'minor',
            source: 'agent',
            message: 'Thin coverage of the cart',
            file: null,
            evidence: null,
            findingId: null,
            handling: null,
            stepNumber: null,
            waivedReason: null,
          },
        ],
      })
    })
    await openReview(project)

    const violations = region('Violations (2)')
    expect(within(violations).getAllByTestId('violation')).toHaveLength(2)
    expect(within(violations).queryByTestId('finding-handling')).not.toBeInTheDocument()
    expect(within(violations).queryByText(/^Id:/)).not.toBeInTheDocument()
    expect(within(violations).queryByRole('region', { name: /^Waived findings/ })).not.toBeInTheDocument()
    expect(within(violations).queryByText('Waived')).not.toBeInTheDocument()
  })
})
