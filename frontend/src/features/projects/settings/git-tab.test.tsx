import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { authDb } from '../../../mocks/handlers/auth'
import { configureGitHubApp, connectGitHub, githubDb, INSTALL_URL } from '../../../mocks/handlers/github'
import { gitSettings, projectsDb } from '../../../mocks/handlers/projects'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'

const TOKEN = 'ghp_newsecrettoken456'

async function openGit(hasToken = true, overrides: Parameters<typeof gitSettings>[0] = {}) {
  const utils = await openOwnerSettings('git', {
    git: gitSettings({ provider: 'github_pat', repoUrl: 'https://github.com/acme/inventory', hasToken, ...overrides }),
  })
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

describe('Settings → Git: mode and GitHub App (FR-83)', () => {
  it('FR83_pat_settings_show_mode_and_hasToken_without_a_value', async () => {
    await openGit(true)

    const summary = screen.getByRole('region', { name: 'Connection' })
    expect(within(summary).getByTestId('git-mode')).toHaveTextContent('Personal access token')
    expect(within(summary).getByTestId('git-has-token')).toHaveTextContent('Set')
    expect(within(summary).getByText('https://github.com/acme/inventory')).toBeInTheDocument()
    // No token value anywhere: the API only says hasToken, and no input holds a value.
    for (const input of screen.queryAllByLabelText(/token/i, { selector: 'input' })) {
      expect(input).toHaveValue('')
    }
    expect(screen.queryByRole('button', { name: 'Switch to GitHub App' })).not.toBeInTheDocument()
  })

  it('FR83_switch_to_app: an Owner of a PAT project the App can access switches it, and the PAT is dropped', async () => {
    const { user, project } = await openGit(true, { appInstalledOnRepo: true })
    connectGitHub(authDb.currentUserId ?? '')
    expect(within(screen.getByRole('region', { name: 'Connection' })).getByText('Can access this repository')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Switch to GitHub App' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Switch to the GitHub App?' })
    await user.click(within(dialog).getByRole('button', { name: 'Switch to GitHub App' }))

    expect(await screen.findByText('The project now uses the GitHub App.')).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByTestId('git-mode')).toHaveTextContent('GitHub App')
    })
    expect(requestsTo('POST', '/settings/git/switch-to-app')).toHaveLength(1)
    expect(project.git).toMatchObject({ mode: 'github_app', repoFullName: 'acme/inventory', hasToken: false })
    expect(screen.queryByRole('button', { name: 'Switch to GitHub App' })).not.toBeInTheDocument()
    expect(screen.queryByText('Access token:')).not.toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Connection' })).getByText('acme/inventory')).toBeInTheDocument()
  })

  it('FR83_switch_to_app: GITHUB_APP_NOT_INSTALLED shows the copy and the install link', async () => {
    configureGitHubApp()
    const { user, project } = await openGit(true, { appInstalledOnRepo: true })
    connectGitHub(authDb.currentUserId ?? '')
    // The App lost access between loading and switching.
    project.git.appInstalledOnRepo = false

    await user.click(screen.getByRole('button', { name: 'Switch to GitHub App' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Switch to GitHub App' }))

    expect(
      await screen.findByText("The AIDE GitHub App can't access this repository. Install it on the repository, then try again."),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Install AIDE on acme/ })).toHaveAttribute('href', INSTALL_URL)
  })

  it('FR83_app_project: shows the repository and App status; the mode select offers no way onto the App', async () => {
    await openGit(false, { provider: 'github_app', repoFullName: 'acme/todo-app' })

    const summary = screen.getByRole('region', { name: 'Connection' })
    expect(within(summary).getByTestId('git-mode')).toHaveTextContent('GitHub App')
    expect(within(summary).getByText('acme/todo-app')).toBeInTheDocument()
    expect(within(summary).getByText('Installed on the repository')).toBeInTheDocument()
    expect(screen.queryByLabelText('Repository URL')).not.toBeInTheDocument()
    expect(screen.queryByText('Access token:')).not.toBeInTheDocument()
  })

  it('FR83_pat_project: the mode select has no GitHub App option', async () => {
    await openGit()
    const select = screen.getByLabelText('Mode')
    expect(within(select).queryByRole('option', { name: 'GitHub App' })).not.toBeInTheDocument()
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['Personal access token', 'Local repository'])
  })

  it('FR83_switch_requires_a_connection: GITHUB_NOT_CONNECTED links to Connections', async () => {
    configureGitHubApp()
    const { user } = await openGit(true, { appInstalledOnRepo: true })

    await user.click(screen.getByRole('button', { name: 'Switch to GitHub App' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Switch to GitHub App' }))

    expect(await screen.findByText(/Connect your GitHub account first \(under Connections\)/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to Connections' })).toHaveAttribute('href', '/connections')
  })

  it('FR83_switch_requires_push: FORBIDDEN explains that write access to the repository is needed', async () => {
    const { user } = await openGit(true, { appInstalledOnRepo: true })
    connectGitHub(authDb.currentUserId ?? '')
    githubDb.noPushRepos.add('acme/inventory')

    await user.click(screen.getByRole('button', { name: 'Switch to GitHub App' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Switch to GitHub App' }))

    expect(
      await screen.findByText('Only project Owners with write access to the repository on GitHub can switch it to the GitHub App.'),
    ).toBeInTheDocument()
  })
})
