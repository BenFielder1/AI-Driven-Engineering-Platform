import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedDeploymentSettings, seedEnvVar, seedVercelConnection, VERCEL_INSTALL_URL, vercelDb } from '../../mocks/handlers/deployments'
import { problem } from '../../mocks/handlers/problem'
import { makeReleaseVersion, seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import type { ProjectRole } from '../../lib/permissions'

const supabaseVersion = () => [makeReleaseVersion({ version: '0.1.0', presetId: 'nextjs-supabase', presetVersion: '1.0.0' })]

/** Ada owns a nextjs-supabase project; `seed` runs with her id before anything loads. */
async function openAsOwner(seed?: (me: string, p: StoredProject) => void) {
  const me = signInAsOwner()
  const project = seedProject({ releaseVersions: supabaseVersion() })
  seed?.(me.id, project)
  const utils = await openSettings('deployment', project)
  await screen.findByRole('heading', { level: 2, name: 'Deployment' })
  return utils
}

const requiredStatus = (key: string) =>
  within(screen.getByRole('listitem', { name: `Required ${key}` })).getByTestId('required-env-status')

describe('Settings → Deployment (FR-88)', () => {
  it('FR88_required_env_flagged: a nextjs-supabase project lists the three Supabase variables, missing ones flagged', async () => {
    await openAsOwner((_me, p) => {
      seedDeploymentSettings(p)
      seedEnvVar(p, { key: 'NEXT_PUBLIC_SUPABASE_URL' })
    })
    const list = await screen.findByRole('list', { name: 'Required environment variables' })
    expect(within(list).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual([
      'Required NEXT_PUBLIC_SUPABASE_URL',
      'Required NEXT_PUBLIC_SUPABASE_ANON_KEY',
      'Required SUPABASE_SERVICE_ROLE_KEY',
    ])
    expect(requiredStatus('NEXT_PUBLIC_SUPABASE_URL')).toHaveTextContent('Set')
    expect(requiredStatus('NEXT_PUBLIC_SUPABASE_ANON_KEY')).toHaveTextContent('Missing')
    expect(requiredStatus('SUPABASE_SERVICE_ROLE_KEY')).toHaveTextContent('Missing')
    expect(screen.getByText('2 required variables are missing')).toBeInTheDocument()
  })

  it('FR88_owner_sets_up_vercel: binds an own connection and creates a Vercel project linked to the repo', async () => {
    const { user, project } = await openAsOwner((me) => {
      seedVercelConnection({ userId: me, label: 'Acme team', token: 'vcp_zzzzab12', teamSlug: 'acme' })
      const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper' })
      seedVercelConnection({ userId: grace.id, label: 'Not mine' })
    })
    expect(screen.getByText(/stack deploys to Vercel/)).toBeInTheDocument()
    const picker = await screen.findByLabelText('Vercel connection')
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['Pick a connection', 'Acme team (••••ab12) · team acme'])
    expect(screen.getByLabelText('Project name')).toHaveValue('inventory-tracker')

    await user.click(screen.getByRole('button', { name: 'Set up Vercel' }))
    expect((await screen.findAllByText('Pick one of your Vercel connections.')).length).toBeGreaterThan(0)

    await user.selectOptions(picker, 'Acme team (••••ab12) · team acme')
    await user.type(screen.getByLabelText('Root directory (optional)'), 'apps/web')
    await user.click(screen.getByRole('button', { name: 'Set up Vercel' }))

    expect(await screen.findByText('Vercel deployment is set up.')).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/deployment').at(-1)?.body).toEqual({
      provider: 'vercel',
      connectionId: project.deployment?.binding?.connectionId,
      create: { name: 'inventory-tracker', rootDirectory: 'apps/web' },
    })
    expect(await screen.findByTestId('deployment-binding')).toHaveTextContent('Vercel · connected by Ada Lovelace (••••ab12) · team acme')
    expect(screen.getByRole('region', { name: 'Environment variables' })).toBeInTheDocument()
  })

  it('FR88_git_not_connected: VERCEL_GIT_NOT_CONNECTED shows the copy and Vercel\'s install link', async () => {
    vercelDb.gitNotConnected = true
    const { user } = await openAsOwner((me) => {
      seedVercelConnection({ userId: me, label: 'Personal', token: 'vcp_q1w2' })
    })
    await user.selectOptions(await screen.findByLabelText('Vercel connection'), 'Personal (••••q1w2)')
    await user.click(screen.getByRole('radio', { name: /Link an existing Vercel project/ }))
    await user.type(screen.getByLabelText('Vercel project id or name'), 'prj_shop')
    await user.click(screen.getByRole('button', { name: 'Set up Vercel' }))

    const link = await screen.findByRole('link', { name: /Install Vercel's GitHub App/ })
    expect(link).toHaveAttribute('href', VERCEL_INSTALL_URL)
    expect(link.closest('[role="alert"]')).toHaveTextContent("Vercel's GitHub App can't access this repository")
  })

  it('FR88_plan_limit: VERCEL_PLAN_LIMIT explains the Hobby plan limit', async () => {
    vercelDb.planLimit = true
    const { user } = await openAsOwner((me) => {
      seedVercelConnection({ userId: me, label: 'Personal', token: 'vcp_q1w2' })
    })
    await user.selectOptions(await screen.findByLabelText('Vercel connection'), 'Personal (••••q1w2)')
    await user.click(screen.getByRole('button', { name: 'Set up Vercel' }))
    expect(await screen.findByText(/a Hobby account can't deploy a repository owned by a GitHub organisation/)).toBeInTheDocument()
  })

  it('FR88_rebind_when_changing_project: another Owner\'s binding means changing the project needs my own connection', async () => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      releaseVersions: supabaseVersion(),
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role: 'owner' },
      ],
    })
    seedDeploymentSettings(project, { connection: seedVercelConnection({ userId: grace.id, label: 'Grace', token: 'vcp_9999c0de' }) })
    const mine = seedVercelConnection({ userId: ada.id, label: 'Mine', token: 'vcp_aaaa1111' })
    const { user } = await openSettings('deployment', project)

    await user.click(await screen.findByRole('radio', { name: /Link an existing Vercel project/ }))
    expect(screen.getByText(/Changing the Vercel project uses your own Vercel connection: pick one of yours/)).toBeInTheDocument()
    await user.type(screen.getByLabelText('Vercel project id or name'), 'prj_other')
    await user.click(screen.getByRole('button', { name: 'Save deployment settings' }))
    expect((await screen.findAllByText('Changing the Vercel project uses your own Vercel connection. Pick one of yours.')).length).toBeGreaterThan(0)
    expect(requestsTo('PATCH', '/settings/deployment')).toHaveLength(0)

    await user.selectOptions(screen.getByLabelText('Vercel connection'), 'Mine (••••1111)')
    await user.click(screen.getByRole('button', { name: 'Save deployment settings' }))
    expect(await screen.findByText('Deployment settings saved.')).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/deployment').at(-1)?.body).toEqual({ provider: 'vercel', connectionId: mine.id, vercelProjectId: 'prj_other' })
    expect(await screen.findByTestId('deployment-binding')).toHaveTextContent('connected by Ada Lovelace (••••1111)')
  })

  it('FR88_rebind_server_copy: a 403 CREDENTIAL_NOT_OWNER from the server shows the rebind copy', async () => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role: 'owner' },
      ],
    })
    seedDeploymentSettings(project, { connection: seedVercelConnection({ userId: grace.id, label: 'Grace', token: 'vcp_9999c0de' }) })
    seedVercelConnection({ userId: ada.id, label: 'Mine', token: 'vcp_aaaa1111' })
    server.use(http.patch('*/api/projects/:id/settings/deployment', () => problem(403, 'CREDENTIAL_NOT_OWNER', 'Not yours.')))
    const { user } = await openSettings('deployment', project)
    await user.selectOptions(await screen.findByLabelText('Vercel connection'), 'Mine (••••1111)')
    await user.click(screen.getByRole('button', { name: 'Save deployment settings' }))
    expect(await screen.findByText(/pick one of yours, which rebinds the project to it/)).toBeInTheDocument()
  })

  it.each<Exclude<ProjectRole, 'owner'>>(['contributor', 'viewer'])(
    'FR88_read_only_for_%s: "Vercel · connected by <name>", no setup form and no env editor',
    async (role) => {
      const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
      const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
      const project = seedProject({
        releaseVersions: supabaseVersion(),
        members: [
          { userId: grace.id, role: 'owner' },
          { userId: ada.id, role },
        ],
      })
      seedDeploymentSettings(project, { connection: seedVercelConnection({ userId: grace.id, label: 'Grace', token: 'vcp_9999c0de' }) })
      await openSettings('deployment', project)

      expect(await screen.findByTestId('deployment-binding')).toHaveTextContent('Vercel · connected by Grace Hopper (••••c0de)')
      expect(screen.queryByRole('button', { name: /Save deployment settings|Set up Vercel/ })).not.toBeInTheDocument()
      expect(screen.queryByRole('region', { name: 'Environment variables' })).not.toBeInTheDocument()
      expect(screen.getByRole('list', { name: 'Required environment variables' })).toBeInTheDocument()
    },
  )
})

describe('Settings → Deployment → Environment variables (FR-88)', () => {
  it('FR88_env_value_cleared_after_save: the value is sent once, never listed, and not kept in memory', async () => {
    const secret = 'https://abcd.supabase.co'
    const { user, queryClient, project } = await openAsOwner((_me, p) => {
      seedDeploymentSettings(p)
    })
    const env = await screen.findByRole('region', { name: 'Environment variables' })
    await user.click(within(env).getByRole('button', { name: 'Add NEXT_PUBLIC_SUPABASE_URL' }))
    const form = within(env).getByRole('form', { name: 'Add a variable' })
    expect(within(form).getByLabelText('Name')).toHaveValue('NEXT_PUBLIC_SUPABASE_URL')
    const value = within(form).getByLabelText('Value')
    expect(value).toHaveAttribute('type', 'password')
    await user.type(value, secret)
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))

    expect(await screen.findByText('NEXT_PUBLIC_SUPABASE_URL saved and pushed to Vercel.')).toBeInTheDocument()
    const row = await within(env).findByRole('listitem', { name: 'Variable NEXT_PUBLIC_SUPABASE_URL' })
    expect(row).toHaveTextContent('Value set')
    expect(row).toHaveTextContent('Preview · Production')
    expect(row).toHaveTextContent(/Synced to Vercel/)
    expect(document.body).not.toHaveTextContent(secret)
    await waitFor(() => {
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain(secret)
    })
    expect(requestsTo('PUT', '/env/NEXT_PUBLIC_SUPABASE_URL').at(-1)?.body).toEqual({
      value: secret,
      targets: ['preview', 'production'],
      sensitive: false,
    })
    expect(project.deploymentEnv?.[0]?.value).toBe(secret)
    await waitFor(() => {
      expect(requiredStatus('NEXT_PUBLIC_SUPABASE_URL')).toHaveTextContent('Set')
    })
  })

  it('FR88_env_targets_required: a variable needs at least one target', async () => {
    const { user } = await openAsOwner((_me, p) => {
      seedDeploymentSettings(p)
    })
    const env = await screen.findByRole('region', { name: 'Environment variables' })
    await user.click(within(env).getByRole('button', { name: 'Add variable' }))
    const form = within(env).getByRole('form', { name: 'Add a variable' })
    await user.type(within(form).getByLabelText('Name'), 'API_URL')
    await user.type(within(form).getByLabelText('Value'), 'x')
    await user.click(within(form).getByLabelText('Preview'))
    await user.click(within(form).getByLabelText('Production'))
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))
    expect((await within(form).findAllByText('Pick at least one target: Preview or Production.')).length).toBeGreaterThan(0)
    expect(requestsTo('PUT', '/env/API_URL')).toHaveLength(0)
  })

  it('FR88_env_sync_failure: a 502 keeps the value in AIDE, says it will retry, and lists the sync error', async () => {
    vercelDb.envSyncFails = true
    const { user } = await openAsOwner((_me, p) => {
      seedDeploymentSettings(p)
    })
    const env = await screen.findByRole('region', { name: 'Environment variables' })
    await user.click(within(env).getByRole('button', { name: 'Add variable' }))
    const form = within(env).getByRole('form', { name: 'Add a variable' })
    await user.type(within(form).getByLabelText('Name'), 'API_URL')
    await user.type(within(form).getByLabelText('Value'), 'https://api.example.com')
    await user.click(within(form).getByLabelText('Sensitive'))
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))

    expect(await within(env).findByText(/Saved in AIDE, but pushing it to Vercel failed/)).toBeInTheDocument()
    const row = await within(env).findByRole('listitem', { name: 'Variable API_URL' })
    expect(within(row).getByTestId('env-sync-error')).toHaveTextContent('AIDE will retry on the next save or deployment.')
    expect(row).toHaveTextContent('Sensitive')
  })

  it('FR88_env_delete: deleting asks first and removes the variable', async () => {
    const { user, project } = await openAsOwner((_me, p) => {
      seedDeploymentSettings(p)
      seedEnvVar(p, { key: 'OLD_FLAG' })
    })
    const env = await screen.findByRole('region', { name: 'Environment variables' })
    await user.click(await within(env).findByRole('button', { name: 'Delete OLD_FLAG' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete OLD_FLAG?' })
    await user.click(within(confirm).getByRole('button', { name: 'Delete variable' }))
    expect(await screen.findByText('OLD_FLAG deleted in AIDE and Vercel.')).toBeInTheDocument()
    expect(project.deploymentEnv).toEqual([])
  })
})
