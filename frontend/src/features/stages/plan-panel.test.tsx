import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { PlanPanel } from './plan-panel'

const REASON = 'frontend/vercel.json was missing from the plan'

function planContent(steps: readonly Record<string, unknown>[]) {
  return `# Plan\n\n\`\`\`aide-plan\n${JSON.stringify({ format: 1, steps }, null, 2)}\n\`\`\`\n`
}

describe('Plan panel provenance (FR-244)', () => {
  const content = planContent([
    { number: 1, title: 'Scaffold', agent: 'frontend-engineer', files: [{ path: 'frontend/package.json', action: 'create' }], dependencies: [] },
    {
      number: 2,
      title: 'Vercel config',
      agent: 'deploy-engineer',
      files: [{ path: 'frontend/vercel.json', action: 'create' }],
      dependencies: [1],
      addedBy: 'tevrin',
      addedReason: REASON,
    },
    { number: 3, title: 'Docs', files: [{ path: 'README.md', action: 'modify' }], dependencies: [1], addedBy: 'other', addedReason: 'not ours' },
  ])

  function rows() {
    const table = screen.getByRole('table', { name: /Plan steps/ })
    return within(table).getAllByRole('row').slice(1)
  }

  it('FR244_an_added_step_shows_the_badge_and_its_reason', () => {
    render(<PlanPanel content={content} />)
    const added = rows()[1]
    if (!added) {
      throw new Error('missing row')
    }
    expect(within(added).getByText('Added by Tevrin')).toBeVisible()
    // The reason is visible text, not hover-only, and reads with the badge as one unit ("Added by Tevrin: <reason>").
    expect(within(added).getByText(REASON)).toBeVisible()
    expect(within(added).getByTestId('plan-step-added-by-tevrin')).toHaveTextContent(`Added by Tevrin: ${REASON}`)
  })

  it('FR244_an_agent_written_step_shows_no_badge', () => {
    render(<PlanPanel content={content} />)
    const agentStep = rows()[0]
    if (!agentStep) {
      throw new Error('missing row')
    }
    expect(within(agentStep).queryByText('Added by Tevrin')).not.toBeInTheDocument()
    expect(screen.getAllByText('Added by Tevrin')).toHaveLength(1)
  })

  it('FR244_addedBy_other_shows_no_badge_or_reason', () => {
    render(<PlanPanel content={content} />)
    const other = rows()[2]
    if (!other) {
      throw new Error('missing row')
    }
    expect(within(other).queryByText('Added by Tevrin')).not.toBeInTheDocument()
    expect(other).not.toHaveTextContent('not ours')
  })
})
