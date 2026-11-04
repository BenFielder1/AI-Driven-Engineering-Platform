// FR-197: copy that names the product says Tevrin (it said AIDE before v6); the assertions below use the new copy.
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { bitbucketCommitEmailWarning } from '../../lib/git-host'
import { authDb, signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

const LABEL = 'Credit me on commits made by Tevrin'

describe('Account: commit credit (FR-177)', () => {
  it('FR177_account_default_on: on by default, with the Bitbucket warning', async () => {
    signInAs()
    await renderRoute('/account')

    expect(await screen.findByRole('checkbox', { name: LABEL })).toBeChecked()
    expect(screen.getByText(bitbucketCommitEmailWarning)).toBeInTheDocument()
  })

  it('FR177_account_opt_out: unticking saves the opt-out', async () => {
    const user = signInAs()
    await renderRoute('/account')

    await userEvent.setup().click(await screen.findByRole('checkbox', { name: LABEL }))

    await waitFor(() => {
      expect(authDb.users.find((u) => u.id === user.id)?.commitCreditOptOut).toBe(true)
    })
    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: LABEL })).not.toBeChecked()
    })
  })

  it('FR177_account_opted_out: an opted-out user sees it off and can turn it back on', async () => {
    const user = signInAs({ commitCreditOptOut: true })
    await renderRoute('/account')

    const box = await screen.findByRole('checkbox', { name: LABEL })
    expect(box).not.toBeChecked()
    await userEvent.setup().click(box)

    await waitFor(() => {
      expect(authDb.users.find((u) => u.id === user.id)?.commitCreditOptOut).toBe(false)
    })
  })

  it('FR177_account_error: a failed save says so and leaves the setting as it was', async () => {
    signInAs()
    server.use(http.patch('*/api/auth/me', () => problem(400, 'VALIDATION', 'Say whether to credit you on commits.')))
    await renderRoute('/account')

    await userEvent.setup().click(await screen.findByRole('checkbox', { name: LABEL }))

    expect(await screen.findByText("Commit credit wasn't saved")).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: LABEL })).toBeChecked()
  })
})
