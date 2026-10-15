import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { ApiError, errorMessage } from '../../lib/errors'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { openOverviewAs } from '../../test/overview'
import { openOwnerSettings, requestsTo } from '../../test/project-settings'

const budgetCopy = 'This project has used its token budget — an Owner can raise it in Settings.'

function budgetCard() {
  const section = screen.getByRole('heading', { name: 'Token budget' }).closest('section')
  if (!section) {
    throw new Error('No token budget card')
  }
  return section
}

describe('Token budget (FR-26)', () => {
  it('FR26_ meter: the overview shows tokens used against the budget as text and as a meter', async () => {
    await openOverviewAs('viewer', { tokenBudget: 100_000, tokensUsed: 25_000 })

    const card = budgetCard()
    expect(within(card).getByText('25,000 of 100,000 tokens used (25%)')).toBeInTheDocument()
    expect(within(card).getByText('75,000 tokens left.')).toBeInTheDocument()
    const meter = within(card).getByRole('meter', { name: 'Token budget used' })
    expect(meter).toHaveAttribute('aria-valuenow', '25000')
    expect(meter).toHaveAttribute('aria-valuemax', '100000')
    expect(meter).toHaveAttribute('aria-valuetext', '25,000 of 100,000 tokens used (25%)')
    expect(within(card).queryByText(budgetCopy)).not.toBeInTheDocument()
  })

  it('FR26_ meter: nearly used up is said in words, not only by colour', async () => {
    await openOverviewAs('viewer', { tokenBudget: 100_000, tokensUsed: 95_500 })

    const card = budgetCard()
    expect(within(card).getByText('· Almost used up')).toBeInTheDocument()
    expect(within(card).getByRole('meter')).toHaveAttribute(
      'aria-valuetext',
      '95,500 of 100,000 tokens used (95%), almost used up',
    )
  })

  it('FR26_ no budget: a project without a budget says so', async () => {
    await openOverviewAs('viewer', { tokenBudget: null, tokensUsed: 4_200 })

    const card = budgetCard()
    expect(within(card).getByText('No budget')).toBeInTheDocument()
    expect(within(card).getByText(/4,200 tokens used/)).toBeInTheDocument()
    expect(within(card).queryByRole('meter')).not.toBeInTheDocument()
  })

  it('FR26_ exhausted: budget 100k with 100k used shows the BUDGET_EXCEEDED copy, and Owners get a link to raise it', async () => {
    const { project } = await openOverviewAs('owner', { tokenBudget: 100_000, tokensUsed: 100_000 })

    const card = budgetCard()
    expect(within(card).getByText('100,000 of 100,000 tokens used (100%)')).toBeInTheDocument()
    expect(within(card).getByText('· Used up')).toBeInTheDocument()
    expect(within(card).getByText(budgetCopy)).toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'Raise the token budget' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/settings/general`,
    )
  })

  it('FR26_ exhausted: Contributors see the copy but no Owner-only link', async () => {
    await openOverviewAs('contributor', { tokenBudget: 100_000, tokensUsed: 120_000 })

    const card = budgetCard()
    expect(within(card).getByText('120,000 of 100,000 tokens used (100%)')).toBeInTheDocument()
    expect(within(card).getByRole('meter')).toHaveAttribute('aria-valuenow', '100000')
    expect(within(card).getByText(budgetCopy)).toBeInTheDocument()
    expect(within(card).queryByRole('link', { name: 'Raise the token budget' })).not.toBeInTheDocument()
  })

  it('FR26_ copy: BUDGET_EXCEEDED (409) from starting work maps to the budget copy', async () => {
    expect(errorMessage(new ApiError({ status: 409, errorCode: 'BUDGET_EXCEEDED' }))).toBe(budgetCopy)

    server.use(
      http.post('*/api/projects/:id/phase/advance', () =>
        problem(409, 'BUDGET_EXCEEDED', 'The project has used its token budget.'),
      ),
    )
    const { user } = await openOverviewAs('owner', { tokenBudget: 100_000, tokensUsed: 100_000 })
    await user.click(screen.getByRole('button', { name: 'Advance to Specification' }))

    const lifecycle = screen.getByRole('heading', { name: 'Next action' }).closest('section')
    if (!lifecycle) {
      throw new Error('No lifecycle card')
    }
    expect(await within(lifecycle).findByText(budgetCopy)).toBeInTheDocument()
  })

  it('FR26_ raise: an Owner raises the budget in Settings → General', async () => {
    const { user, project } = await openOwnerSettings('general', { tokenBudget: 100_000, tokensUsed: 100_000 })

    const field = await screen.findByLabelText('Token budget')
    expect(field).toHaveAccessibleDescription(expect.stringContaining('100,000 used so far') as string)
    await user.clear(field)
    await user.type(field, '250000')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      expect(requestsTo('PATCH', `/api/projects/${project.id}`)).toHaveLength(1)
    })
    expect(requestsTo('PATCH', `/api/projects/${project.id}`)[0]?.body).toEqual({ tokenBudget: 250_000 })
    expect(project.tokenBudget).toBe(250_000)
  })
})
