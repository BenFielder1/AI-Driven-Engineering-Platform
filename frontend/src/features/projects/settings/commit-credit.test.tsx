import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { bitbucketCommitEmailWarning } from '../../../lib/git-host'
import { gitSettings } from '../../../mocks/handlers/projects'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'

const LABEL = 'Credit people on commits'

async function openGit(overrides: Parameters<typeof gitSettings>[0] = {}) {
  const utils = await openOwnerSettings('git', { git: gitSettings(overrides) })
  await screen.findByRole('heading', { name: 'Git repository' })
  return utils
}

describe('Settings → Git: commit credit (FR-177)', () => {
  it('FR177_settings_toggle: on by default; turning it off saves only that setting', async () => {
    const { user, project } = await openGit({ provider: 'github_pat', repoUrl: 'https://github.com/acme/inventory', hasToken: true })

    const box = screen.getByRole('checkbox', { name: LABEL })
    expect(box).toBeChecked()
    await user.click(box)

    await waitFor(() => {
      expect(project.git.commitCreditEnabled).toBe(false)
    })
    expect(requestsTo('PATCH', '/settings/git')).toHaveLength(1)
    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: LABEL })).not.toBeChecked()
    })
  })

  it('FR177_settings_off_shown: a project with credit off shows it off', async () => {
    await openGit({ commitCreditEnabled: false })

    expect(screen.getByRole('checkbox', { name: LABEL })).not.toBeChecked()
  })

  it('FR177_bitbucket_warning: only a Bitbucket project shows the warning', async () => {
    await openGit({ provider: 'github_pat', repoUrl: 'https://github.com/acme/inventory' })

    expect(screen.getByRole('checkbox', { name: LABEL })).toBeInTheDocument()
    expect(screen.queryByText(bitbucketCommitEmailWarning)).not.toBeInTheDocument()
  })

  it('FR177_bitbucket_warning_shown: a Bitbucket project shows the exact copy', async () => {
    await openGit({ provider: 'bitbucket', repoFullName: 'acme/inventory' })

    expect(screen.getByText(bitbucketCommitEmailWarning)).toBeInTheDocument()
    expect(bitbucketCommitEmailWarning).toBe(
      "Bitbucket has no private commit address, so this adds the person's Bitbucket email to the repository's history.",
    )
  })
})
