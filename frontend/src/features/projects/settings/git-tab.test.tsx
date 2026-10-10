import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { projectsDb } from '../../../mocks/handlers/projects'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'

const TOKEN = 'ghp_newsecrettoken456'

const savedGit = {
  provider: 'github',
  repoUrl: 'https://github.com/acme/inventory',
  defaultBranch: 'main',
  branchName: null,
  hasToken: true,
} as const

async function openGit(hasToken = true) {
  const utils = await openOwnerSettings('git', { git: { ...savedGit, hasToken } })
  await screen.findByRole('heading', { name: 'Git repository' })
  return utils
}

describe('Settings → Git: test connection (FR-10)', () => {
  it('FR-10: a passing test shows "Connection OK" and each check', async () => {
    const { user } = await openGit()

    await user.click(screen.getByRole('button', { name: 'Test connection' }))

    expect(await screen.findByText('Connection OK')).toBeInTheDocument()
    const checks = screen.getByRole('list', { name: 'Connection checks' })
    expect(within(checks).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Repository reachableYes',
      'Write accessYes',
      'Default branch existsYes',
    ])
  })

  it('FR-10: a failing test shows "Connection failed", the reason and the failed checks', async () => {
    projectsDb.gitTestResult = {
      ok: false,
      reachable: true,
      writable: false,
      defaultBranchExists: true,
      reason: 'The token does not have write access to acme/inventory.',
    }
    const { user } = await openGit()

    await user.click(screen.getByRole('button', { name: 'Test connection' }))

    expect(await screen.findByText('Connection failed')).toBeInTheDocument()
    expect(screen.getByText('The token does not have write access to acme/inventory.')).toBeInTheDocument()
    const checks = screen.getByRole('list', { name: 'Connection checks' })
    expect(within(checks).getByText('Write access').parentElement).toHaveTextContent('No')
    // Announced politely.
    expect(screen.getByText('Connection failed').closest('[role="status"]')).not.toBeNull()
  })

  it('FR-10: testing does not save the form (unsaved edits stay unsaved)', async () => {
    const { user, project } = await openGit()
    const repo = screen.getByLabelText('Repository URL')
    await user.clear(repo)
    await user.type(repo, 'https://github.com/acme/other')

    await user.click(screen.getByRole('button', { name: 'Test connection' }))
    await screen.findByText('Connection OK')

    expect(requestsTo('POST', '/settings/git/test')).toHaveLength(1)
    expect(requestsTo('PATCH', '/settings/git')).toHaveLength(0)
    expect(project.git.repoUrl).toBe('https://github.com/acme/inventory')
    expect(repo).toHaveValue('https://github.com/acme/other')
  })
})

describe('Settings → Git: access token (FR-9)', () => {
  it('FR-9: a saved token shows only "Set"; saving without touching it keeps it (no token sent)', async () => {
    const { user, project } = await openGit()

    expect(screen.getByText('Access token:').parentElement).toHaveTextContent('Access token: Set')
    expect(screen.queryByLabelText(/access token/i, { selector: 'input' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Save Git settings' }))

    expect(await screen.findByText('Git settings saved.')).toBeInTheDocument()
    const [patch] = requestsTo('PATCH', '/settings/git')
    expect(patch?.body).not.toHaveProperty('token')
    expect(patch?.body).not.toHaveProperty('clearToken')
    expect(project.git.hasToken).toBe(true)
  })

  it('FR-9: Replace opens a focused password field and sends the new token, which is never shown', async () => {
    const { user } = await openGit()

    await user.click(screen.getByRole('button', { name: 'Replace access token' }))
    const input = screen.getByLabelText('New access token')
    expect(input).toHaveFocus()
    expect(input).toHaveAttribute('type', 'password')
    await user.type(input, TOKEN)
    await user.click(screen.getByRole('button', { name: 'Save Git settings' }))

    expect(await screen.findByText('Git settings saved.')).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/git')[0]?.body).toMatchObject({ token: TOKEN })
    expect(screen.queryByLabelText('New access token')).not.toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(TOKEN)
  })

  it('FR-9: Clear marks the token for removal, can be undone, and sends clearToken', async () => {
    const { user, project } = await openGit()

    await user.click(screen.getByRole('button', { name: 'Clear access token' }))
    expect(screen.getByText('will be removed when you save')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Keep access token' }))
    expect(screen.getByRole('button', { name: 'Replace access token' })).toHaveFocus()

    await user.click(screen.getByRole('button', { name: 'Clear access token' }))
    await user.click(screen.getByRole('button', { name: 'Save Git settings' }))

    await waitFor(() => {
      expect(requestsTo('PATCH', '/settings/git')[0]?.body).toMatchObject({ clearToken: true })
    })
    expect(project.git.hasToken).toBe(false)
    expect(await screen.findByText('Access token (optional)')).toBeInTheDocument()
  })

  it('FR-9: invalid fields are listed in the error summary, linked to labelled, invalid inputs', async () => {
    const { user } = await openGit()
    const repo = screen.getByLabelText('Repository URL')
    await user.clear(repo)
    await user.type(repo, 'not a url')
    await user.clear(screen.getByLabelText('Default branch'))

    await user.click(screen.getByRole('button', { name: 'Save Git settings' }))

    const repoLink = await screen.findByRole('link', { name: 'Enter a URL like https://github.com/owner/repo.' })
    expect(repoLink).toHaveAttribute('href', '#git-repo-url')
    expect(screen.getByRole('link', { name: 'Enter the default branch, such as "main".' })).toHaveAttribute(
      'href',
      '#git-default-branch',
    )
    expect(repo).toHaveAttribute('id', 'git-repo-url')
    expect(repo).toHaveAttribute('aria-invalid', 'true')
    expect(repo).toHaveAccessibleDescription(expect.stringContaining('Enter a URL like') as string)
    expect(requestsTo('PATCH', '/settings/git')).toHaveLength(0)
  })
})
