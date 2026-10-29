import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { bitbucketDb, configureBitbucketConsumer, seedBitbucketConnection, seedBitbucketRepo } from '../../mocks/handlers/bitbucket'
import { renderRoute } from '../../test/render'
import { bitbucketProjectCreationAvailable } from './schemas'
import { WIZARD_DRAFT_KEY } from './wizard-draft'
import { repoHostLabelOf, repoVcsOf } from './wizard-stack'

/** Signs in, optionally connects Bitbucket (with `repos` repositories), and walks the wizard to the Repository step. */
async function openRepositoryStep({ connect = true, consumer = false, repos = 0 } = {}) {
  const me = signInAs({ systemRole: 'user' })
  if (consumer) {
    configureBitbucketConsumer()
  }
  const connection = connect ? seedBitbucketConnection(me.id) : null
  for (let i = 0; i < repos; i++) {
    if (connection) {
      seedBitbucketRepo(connection.id, { slug: `app-${String(i + 1).padStart(2, '0')}` })
    }
  }
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await screen.findByRole('heading', { level: 2, name: /Step 1 of 8: Source/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 2 of 8: Project idea/ })
  await user.type(screen.getByLabelText('Project name'), 'Todo app')
  await user.type(screen.getByLabelText('Your idea'), 'A shared todo list.')
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 3 of 8: Repository/ })
  return { user, connection }
}

describe('Wizard → Repository on Bitbucket (FR-135)', () => {
  it('FR135_hidden_without_bitbucket: no Bitbucket option (or notice) without a consumer or a connection', async () => {
    await openRepositoryStep({ connect: false })

    await screen.findByRole('radio', { name: 'Local repository' })
    expect(screen.queryByRole('radio', { name: 'Bitbucket' })).not.toBeInTheDocument()
    expect(screen.queryByText(/Bitbucket projects arrive in the next AIDE update/)).not.toBeInTheDocument()
  })

  it('FR135_consumer_without_connection: the option shows with the notice and a way to Connections that keeps the wizard', async () => {
    const { user } = await openRepositoryStep({ connect: false, consumer: true })

    await user.click(await screen.findByRole('radio', { name: 'Bitbucket' }))
    expect(await screen.findByText('Bitbucket projects arrive in the next AIDE update')).toBeInTheDocument()
    const link = await screen.findByRole('link', { name: 'Connect Bitbucket under Connections' })
    await user.click(link)

    expect(sessionStorage.getItem(WIZARD_DRAFT_KEY)).toContain('"gitMode":"bitbucket"')
  })

  it('FR135_picker_in_wizard: picks a repository with search and paging; Create is hidden while projects can not bind it', async () => {
    const { user, connection } = await openRepositoryStep({ repos: 32 })

    await user.click(await screen.findByRole('radio', { name: 'Bitbucket' }))
    expect(await screen.findByText('Bitbucket projects arrive in the next AIDE update')).toBeInTheDocument()
    expect(await screen.findByLabelText('Bitbucket connection')).toHaveValue(connection?.id)

    const list = await screen.findByRole('list', { name: 'Bitbucket repositories' })
    expect(within(list).getAllByRole('radio')).toHaveLength(30)
    await user.click(screen.getByRole('button', { name: 'Load more repositories' }))
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'Bitbucket repositories' })).getAllByRole('radio')).toHaveLength(32)
    })
    await user.type(screen.getByLabelText('Search Bitbucket repositories'), 'app-07')
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'Bitbucket repositories' })).getAllByRole('radio')).toHaveLength(1)
    })
    await user.click(screen.getByRole('radio', { name: 'acme/app-07' }))
    expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/app-07')
    expect(screen.getByLabelText('Default branch')).toHaveValue('main')

    expect(bitbucketProjectCreationAvailable()).toBe(false)
    expect(screen.queryByRole('button', { name: 'Create Bitbucket repository' })).not.toBeInTheDocument()
    expect(bitbucketDb.createdRepos).toEqual([])
  })

  it('FR135_blocked_until_binding: Next on the Bitbucket option says AIDE cannot create Bitbucket projects yet', async () => {
    const { user } = await openRepositoryStep({ repos: 1 })

    await user.click(await screen.findByRole('radio', { name: 'Bitbucket' }))
    await user.click(await screen.findByRole('radio', { name: 'acme/app-01' }))
    await user.click(screen.getByRole('button', { name: 'Next' }))

    expect(
      await screen.findAllByText("AIDE can't create projects on Bitbucket yet. Choose another option to continue."),
    ).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 2, name: /Step 3 of 8: Repository/ })).toBeInTheDocument()
  })

  it('FR135_switch_mode_clears_pick: leaving Bitbucket clears the picked repository and its id', async () => {
    const { user } = await openRepositoryStep({ repos: 1 })

    await user.click(await screen.findByRole('radio', { name: 'Bitbucket' }))
    await user.click(await screen.findByRole('radio', { name: 'acme/app-01' }))
    expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/app-01')

    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
    await user.click(screen.getByRole('radio', { name: 'Bitbucket' }))
    expect(await screen.findByText('No repository selected yet.')).toBeInTheDocument()
    expect(await screen.findByRole('radio', { name: 'acme/app-01' })).not.toBeChecked()

    // The draft saved on the way to Connections is the form's values: both the path and the Bitbucket id are gone.
    await user.click(screen.getByRole('link', { name: 'Connect Bitbucket under Connections' }))
    const draft = JSON.parse(sessionStorage.getItem(WIZARD_DRAFT_KEY) ?? '{}') as { values?: Record<string, unknown> }
    expect(draft.values?.repoFullName).toBe('')
    expect(draft.values?.bitbucketRepoId).toBe('')
  })

  it('FR135_stack_vcs: a Bitbucket repository fixes the VCS layer to vcs-bitbucket', () => {
    expect(repoVcsOf('bitbucket')).toBe('vcs-bitbucket')
    expect(repoHostLabelOf('bitbucket')).toBe('Bitbucket')
  })
})
