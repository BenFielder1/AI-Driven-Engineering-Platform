import { screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { errorMessage, errorMessages, toApiError } from '../../lib/errors'
import type { ProjectRole } from '../../lib/permissions'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import {
  deploymentDb,
  GITHUB_APP_SETTINGS_URL,
  NETLIFY_INSTALL_URL,
  seedEnvVar,
  seedGitHubPagesTargets,
  seedNetlifyRenderTargets,
} from '../../mocks/handlers/deployments'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import { deploymentSettingsPolling } from './deployments-api'

/** Ada owns the project; `seed` runs with her id before anything loads. */
async function openAsOwner(seed: (me: string, p: StoredProject) => void) {
  const me = signInAsOwner()
  const project = seedProject({})
  seed(me.id, project)
  const utils = await openSettings('deployment', project)
  await screen.findByRole('heading', { level: 2, name: 'Deployment' })
  return utils
}

const card = (name: string) => screen.findByRole('region', { name })
const envRow = (key: string) => screen.findByRole('listitem', { name: `Variable ${key}` })

beforeEach(() => {
  deploymentSettingsPolling.setupMs = 20
})

afterEach(() => {
  deploymentSettingsPolling.setupMs = 2_000
})

describe('Settings → Deployment targets (FR-144)', () => {
  it('FR144_target_cards_per_component: one card per component, with "connected by", databases and the Fake-mode note', async () => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const netlify = seedPlatformConnection({ userId: grace.id, provider: 'netlify', label: 'Acme Netlify', token: 'nfp_aaaa1111', accountName: 'acme' })
    const render = seedPlatformConnection({ userId: grace.id, provider: 'render', label: 'Render key', token: 'rnd_bbbb2222', accountName: 'Ada' })
    await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true, netlify, render })
    })

    const fe = await card('Frontend on Netlify')
    expect(within(fe).getByTestId('target-binding')).toHaveTextContent('Netlify · Acme Netlify (••••1111) · account acme · connected by Grace Hopper')
    expect(within(fe).getByTestId('target-status')).toHaveTextContent('Ready')
    expect(within(fe).getByRole('link', { name: /inventory-web\.netlify\.app/ })).toHaveAttribute('href', 'https://inventory-web.netlify.app')
    expect(within(fe).getByText('Build configuration (derived from the stack)')).toBeInTheDocument()
    expect(within(fe).getByText('dist')).toBeInTheDocument()

    const be = await card('Backend on Render')
    expect(within(be).getByTestId('target-binding')).toHaveTextContent('connected by Grace Hopper')
    expect(within(be).getByText('backend/Dockerfile')).toBeInTheDocument()
    expect(within(be).getByText('/api/health/ready')).toBeInTheDocument()

    const dbs = screen.getByRole('list', { name: 'Databases' })
    expect(within(dbs).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual(['Production database', 'Preview database'])
    expect(within(dbs).getAllByText('Database setup arrives with managed databases.')).toHaveLength(2)
    expect(screen.getByText(/deploys with its Fake provider/)).toBeInTheDocument()
  })

  it('FR144_bind_create_progress_ready: binds an own connection, creates the site, shows the setup steps, then Ready', async () => {
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
      const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper' })
      seedPlatformConnection({ userId: grace.id, provider: 'netlify', label: 'Not mine' })
    })
    const fe = await card('Frontend on Netlify')
    const form = within(fe).getByRole('form', { name: 'Set up the frontend on Netlify' })
    const picker = await within(form).findByLabelText('Netlify connection')
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['Pick a connection', 'Mine (••••3333) · ada'])

    await user.click(within(form).getByRole('button', { name: 'Set up Netlify' }))
    expect((await within(form).findAllByText('Pick one of your Netlify connections.')).length).toBeGreaterThan(0)
    expect(requestsTo('PUT', '/targets/frontend')).toHaveLength(0)

    await user.selectOptions(picker, 'Mine (••••3333) · ada')
    const name = within(form).getByLabelText('Name')
    await user.clear(name)
    await user.type(name, 'shop-web')
    await user.type(within(form).getByLabelText('Region (optional)'), 'eu')
    await user.click(within(form).getByRole('button', { name: 'Set up Netlify' }))

    expect(await within(fe).findByTestId('setup-progress')).toHaveTextContent(/Setting up/)
    expect(requestsTo('PUT', '/targets/frontend').at(-1)?.body).toEqual({
      provider: 'netlify',
      connectionId: expect.any(String) as string,
      create: { name: 'shop-web', region: 'eu' },
    })
    await waitFor(() => {
      expect(within(fe).getByTestId('target-status')).toHaveTextContent('Ready')
    })
    expect(within(fe).queryByTestId('setup-progress')).not.toBeInTheDocument()
    expect(within(fe).getByTestId('target-binding')).toHaveTextContent('connected by Ada Lovelace')
    expect(within(fe).getByRole('link', { name: /shop-web\.netlify\.app/ })).toBeInTheDocument()
  })

  it('FR144_link_picker: Link lists the resources the connection reaches and sends the picked id', async () => {
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_dddd4444' })
    })
    const be = await card('Backend on Render')
    const form = within(be).getByRole('form', { name: 'Set up the backend on Render' })
    await user.selectOptions(await within(form).findByLabelText('Render connection'), 'Render key (••••4444) · ada')
    await user.click(within(form).getByRole('radio', { name: /Link an existing Render service/ }))
    const existing = await within(form).findByLabelText('Existing service')
    await user.selectOptions(existing, 'inventory-services · A render service')
    await user.click(within(form).getByRole('button', { name: 'Set up Render' }))
    await waitFor(() => {
      expect(requestsTo('PUT', '/targets/backend').at(-1)?.body).toMatchObject({ provider: 'render', link: { resourceId: 'services_1' } })
    })
  })

  it('FR144_setup_job_error: a failed setup job shows the shared copy and the platform\'s fix link on the card', async () => {
    deploymentDb.gitNotConnected = true
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
    })
    const fe = await card('Frontend on Netlify')
    const form = within(fe).getByRole('form', { name: 'Set up the frontend on Netlify' })
    await user.selectOptions(await within(form).findByLabelText('Netlify connection'), 'Mine (••••3333) · ada')
    await user.click(within(form).getByRole('button', { name: 'Set up Netlify' }))

    const link = await within(fe).findByRole('link', { name: /Connect the repository in Netlify/ }, { timeout: 3000 })
    expect(link).toHaveAttribute('href', NETLIFY_INSTALL_URL)
    expect(link.closest('[role="alert"]')).toHaveTextContent("Netlify can't see this repository. Connect it in Netlify, then verify again.")
    expect(within(fe).getByTestId('target-status')).toHaveTextContent('Error')
  })

  it('FR144_setup_plan_limit: PLATFORM_PLAN_LIMIT from the job names the platform and its reason', async () => {
    deploymentDb.planLimit = true
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
    })
    const fe = await card('Frontend on Netlify')
    await user.selectOptions(await within(fe).findByLabelText('Netlify connection'), 'Mine (••••3333) · ada')
    await user.click(within(fe).getByRole('button', { name: 'Set up Netlify' }))
    expect(await within(fe).findByTestId('setup-error', {}, { timeout: 3000 })).toHaveTextContent(
      "Your Netlify plan doesn't allow this. A private organisation repository needs the Pro plan.",
    )
  })

  it('FR144_verify_checklist: Verify lists every readiness item with its state in words and fix links', async () => {
    const { user } = await openAsOwner((me, p) => {
      const c = seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
      seedNetlifyRenderTargets(p, { configured: true, netlify: c })
    })
    const be = await card('Backend on Render')
    deploymentDb.gitNotConnected = true
    await user.click(within(be).getByRole('button', { name: 'Verify the backend target' }))
    const list = within(be).getByRole('list', { name: 'Readiness checklist (backend)' })
    await waitFor(() => {
      expect(within(list).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual([
        'Connection',
        'Repository access',
        'Required variables',
        'AIDE branches not auto-deployed',
        'Preview environment',
        'Plan limits',
      ])
    })
    const git = within(list).getByRole('listitem', { name: 'Repository access' })
    expect(within(git).getByTestId('readiness-state')).toHaveTextContent('Problem')
    expect(within(git).getByRole('link', { name: /Fix: Repository access/ })).toHaveAttribute('href', NETLIFY_INSTALL_URL)
    const env = within(list).getByRole('listitem', { name: 'Required variables' })
    expect(within(env).getByTestId('readiness-state')).toHaveTextContent('Warning')
    expect(env).toHaveTextContent('Missing: JWT_SIGNING_KEY.')
    expect(within(within(list).getByRole('listitem', { name: 'Connection' })).getByTestId('readiness-state')).toHaveTextContent('Problem')
  })

  it('FR144_keep_rebinds: "Keep <resource>" sends only my connection (no create or link) and the card is rebound to me', async () => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role: 'owner' },
      ],
    })
    const theirs = seedPlatformConnection({ userId: grace.id, provider: 'netlify', label: 'Grace', token: 'nfp_9999c0de' })
    const mine = seedPlatformConnection({ userId: ada.id, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
    seedNetlifyRenderTargets(project, { configured: true, netlify: theirs })
    const { user } = await openSettings('deployment', project)

    const fe = await card('Frontend on Netlify')
    await user.click(within(fe).getByRole('button', { name: 'Change the frontend target' }))
    const form = within(fe).getByRole('form', { name: 'Set up the frontend on Netlify' })
    expect(within(form).getByRole('radio', { name: /Keep inventory-web/ })).toBeChecked()
    await within(form).findByLabelText('Netlify connection')
    await user.click(within(form).getByRole('button', { name: 'Save target' }))
    expect((await within(form).findAllByText('Pick one of your Netlify connections.')).length).toBeGreaterThan(0)
    expect(requestsTo('PUT', '/targets/frontend')).toHaveLength(0)

    await user.selectOptions(within(form).getByLabelText('Netlify connection'), 'Mine (••••3333) · ada')
    await user.click(within(form).getByRole('button', { name: 'Save target' }))
    await waitFor(() => {
      expect(requestsTo('PUT', '/targets/frontend').at(-1)?.body).toEqual({ provider: 'netlify', connectionId: mine.id })
    })
    await waitFor(() => {
      expect(within(fe).getByTestId('target-status')).toHaveTextContent('Ready')
    })
    expect(within(fe).getByTestId('target-binding')).toHaveTextContent('connected by Ada Lovelace')
    expect(within(fe).getByTestId('target-resource')).toHaveTextContent('inventory-web')
  })

  it('FR144_unbind: Unbind asks first, then the card is not set up', async () => {
    const { user } = await openAsOwner((me, p) => {
      const c = seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Mine', token: 'nfp_cccc3333' })
      seedNetlifyRenderTargets(p, { configured: true, netlify: c })
    })
    const fe = await card('Frontend on Netlify')
    await user.click(within(fe).getByRole('button', { name: 'Unbind the frontend target' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Unbind the frontend target?' })
    await user.click(within(confirm).getByRole('button', { name: 'Unbind' }))
    await waitFor(() => {
      expect(within(fe).getByTestId('target-status')).toHaveTextContent('Not set up')
    })
    expect(requestsTo('DELETE', '/targets/frontend')).toHaveLength(1)
  })

  it.each<Exclude<ProjectRole, 'owner'>>(['contributor', 'viewer'])('FR144_read_only_for_%s: no setup, verify, unbind or env editing', async (role) => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role },
      ],
    })
    const c = seedPlatformConnection({ userId: grace.id, provider: 'netlify', label: 'Grace', token: 'nfp_9999c0de' })
    seedNetlifyRenderTargets(project, { configured: true, netlify: c })
    seedEnvVar(project, { key: 'FEATURE_FLAG' })
    await openSettings('deployment', project)

    const fe = await card('Frontend on Netlify')
    expect(within(fe).getByTestId('target-binding')).toHaveTextContent('connected by Grace')
    expect(screen.getByText('Only project Owners can change the deployment settings.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Verify|Unbind|Change setup|Add variable|Replace the value|Add a value/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('form')).not.toBeInTheDocument()
    expect(await envRow('FEATURE_FLAG')).toBeInTheDocument()
  })
})

describe('GitHub Pages (FR-143)', () => {
  it('FR143_pages_permission_409: PLATFORM_PERMISSION_MISSING shows the copy and the App settings link', async () => {
    deploymentDb.pagesPermissionMissing = true
    const { user } = await openAsOwner((_me, p) => {
      seedGitHubPagesTargets(p)
    })
    const fe = await card('Frontend on GitHub Pages')
    expect(within(fe).getByTestId('target-binding')).toHaveTextContent("connected by the project's GitHub App installation")
    expect(within(fe).getByRole('list', { name: 'What GitHub Pages supports' })).toHaveTextContent('No previews on GitHub Pages')
    expect(within(fe).getByText('No previews on GitHub Pages', { selector: 'dd span' })).toBeInTheDocument()

    await user.click(within(fe).getByRole('button', { name: 'Set up GitHub Pages' }))
    const link = await within(fe).findByRole('link', { name: /Open the GitHub App settings/ })
    expect(link).toHaveAttribute('href', GITHUB_APP_SETTINGS_URL)
    expect(link.closest('[role="alert"]')).toHaveTextContent(errorMessages.PLATFORM_PERMISSION_MISSING)
    expect(requestsTo('PUT', '/targets/frontend').at(-1)?.body).toEqual({ provider: 'github_pages' })
  })

  it('FR143_pages_setup: with the permission, GitHub Pages sets up without a connection', async () => {
    const { user } = await openAsOwner((_me, p) => {
      seedGitHubPagesTargets(p)
    })
    const fe = await card('Frontend on GitHub Pages')
    expect(within(fe).queryByLabelText(/connection/)).not.toBeInTheDocument()
    await user.click(within(fe).getByRole('button', { name: 'Set up GitHub Pages' }))
    await waitFor(() => {
      expect(within(fe).getByTestId('target-status')).toHaveTextContent('Ready')
    })
  })
})

describe('Environment inventory (FR-145)', () => {
  it('FR145_source_badges: user, derived (read-only, with its origin) and required-missing rows', async () => {
    await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
      seedEnvVar(p, { key: 'FEATURE_FLAG', appliesTo: ['frontend', 'backend'], environments: ['preview'] })
    })
    const list = await screen.findByRole('list', { name: 'Environment variables' })
    expect(within(list).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual([
      'Variable API_ORIGIN',
      'Variable DATABASE_URL',
      'Variable FEATURE_FLAG',
      'Variable JWT_SIGNING_KEY',
    ])

    const user = await envRow('FEATURE_FLAG')
    expect(within(user).getByTestId('env-source')).toHaveTextContent('User')
    expect(within(user).getByTestId('env-components')).toHaveTextContent('Frontend, Backend')
    expect(within(user).getByTestId('env-environments')).toHaveTextContent('Preview')
    expect(within(user).getByRole('button', { name: 'Replace the value of FEATURE_FLAG' })).toBeInTheDocument()

    const derived = await envRow('API_ORIGIN')
    expect(within(derived).getByTestId('env-source')).toHaveTextContent('Derived')
    expect(within(derived).getByTestId('env-derived-from')).toHaveTextContent('From the backend target on Render (read-only)')
    expect(within(derived).queryByRole('button')).not.toBeInTheDocument()

    const required = await envRow('JWT_SIGNING_KEY')
    expect(within(required).getByTestId('env-source')).toHaveTextContent('Required · missing')
    expect(within(required).getByText('Sensitive')).toBeInTheDocument()
    expect(within(required).getByRole('button', { name: 'Add a value for JWT_SIGNING_KEY' })).toBeInTheDocument()
    expect(screen.getByText('1 required variable is missing')).toBeInTheDocument()
  })

  it('FR145_add_required_value: the value is sent once with the row\'s components and never shown', async () => {
    const secret = 'super-signing-key-123'
    const { user, project } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
    })
    await user.click(await screen.findByRole('button', { name: 'Add a value for JWT_SIGNING_KEY' }))
    const form = screen.getByRole('form', { name: 'Add JWT_SIGNING_KEY' })
    expect(within(form).getByLabelText('Name')).toHaveAttribute('readonly')
    const value = within(form).getByLabelText('Value')
    expect(value).toHaveAttribute('type', 'password')
    await user.type(value, secret)
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))

    await waitFor(async () => {
      expect(within(await envRow('JWT_SIGNING_KEY')).getByTestId('env-source')).toHaveTextContent('User')
    })
    expect(requestsTo('PUT', '/env/JWT_SIGNING_KEY').at(-1)?.body).toEqual({
      value: secret,
      targets: ['preview', 'production'],
      appliesTo: ['backend'],
      sensitive: true,
      public: false,
    })
    expect(project.deploymentEnv?.[0]?.value).toBe(secret)
    expect(document.body).not.toHaveTextContent(secret)
  })

  it('FR145_public_secret_validation: a secret-looking public value is refused inline', async () => {
    const { user } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
    })
    await user.click(await screen.findByRole('button', { name: 'Add variable' }))
    const form = screen.getByRole('form', { name: 'Add a variable' })
    await user.type(within(form).getByLabelText('Name'), 'VITE_STRIPE_KEY')
    await user.type(within(form).getByLabelText('Value'), 'sk_live_abc')
    await user.click(within(form).getByLabelText('Public'))
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))

    const publicBox = within(form).getByLabelText('Public')
    await waitFor(() => {
      expect(publicBox).toHaveAttribute('aria-invalid', 'true')
    })
    expect(publicBox).toHaveAccessibleDescription(expect.stringContaining("Secret values can't be public.") as string)
    expect(within(form).getByLabelText('Value')).toHaveValue('')
  })

  it('FR145_derived_key_refused: a derived key is VALIDATION on the name field', async () => {
    const { user } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
    })
    await user.click(await screen.findByRole('button', { name: 'Add variable' }))
    const form = screen.getByRole('form', { name: 'Add a variable' })
    await user.type(within(form).getByLabelText('Name'), 'API_ORIGIN')
    await user.type(within(form).getByLabelText('Value'), 'https://x.example')
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))
    expect((await within(form).findAllByText(/AIDE derives API_ORIGIN from the deployment targets/)).length).toBeGreaterThan(0)
  })

  it('FR145_env_sync_failure: a 502 keeps the value and the row shows the sync error', async () => {
    deploymentDb.envSyncFails = true
    const { user } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
    })
    await user.click(await screen.findByRole('button', { name: 'Add variable' }))
    const form = screen.getByRole('form', { name: 'Add a variable' })
    await user.type(within(form).getByLabelText('Name'), 'API_KEY')
    await user.type(within(form).getByLabelText('Value'), 'abc')
    await user.click(within(form).getByRole('button', { name: 'Save variable' }))
    expect(await screen.findByText(/Saved in AIDE, but pushing it to a deployment platform failed/)).toBeInTheDocument()
    expect(within(await envRow('API_KEY')).getByTestId('env-sync-error')).toHaveTextContent('AIDE retries on the next save or deployment.')
  })

  it('FR145_env_delete: deleting a user variable asks first and removes it', async () => {
    const { user, project } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p, { configured: true })
      seedEnvVar(p, { key: 'OLD_FLAG' })
    })
    await user.click(await screen.findByRole('button', { name: 'Delete OLD_FLAG' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete OLD_FLAG?' })
    await user.click(within(confirm).getByRole('button', { name: 'Delete variable' }))
    expect(await screen.findByText('OLD_FLAG deleted.')).toBeInTheDocument()
    expect(project.deploymentEnv).toEqual([])
  })
})

describe('Origin model (FR-146)', () => {
  it('FR146_origin_model_card: proxy is pack "same-origin", with the explanation and the other allowed model', async () => {
    await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p)
    })
    const origin = await screen.findByTestId('origin-model')
    expect(within(origin).getByTestId('origin-model-value')).toHaveTextContent('Proxy')
    expect(within(origin).getByTestId('origin-pack-model')).toHaveTextContent('same-origin')
    expect(origin).toHaveTextContent('Netlify rewrites /api/* to the backend (API_ORIGIN)')
    expect(origin).toHaveTextContent('This stack also allows Cross-origin.')
    expect(within(origin).getByRole('link', { name: 'Settings → Stack' })).toHaveAttribute('href', expect.stringContaining('/settings/stack') as string)
  })

  it('FR146_cross_origin_pages: a SPA on GitHub Pages is cross-origin, with VITE_API_BASE_URL derived public', async () => {
    await openAsOwner((_me, p) => {
      seedGitHubPagesTargets(p)
    })
    const origin = await screen.findByTestId('origin-model')
    expect(within(origin).getByTestId('origin-model-value')).toHaveTextContent('Cross-origin')
    expect(within(origin).getByTestId('origin-pack-model')).toHaveTextContent('cross-origin')
    const row = await envRow('VITE_API_BASE_URL')
    expect(within(row).getByTestId('env-source')).toHaveTextContent('Derived')
    expect(within(row).getByText('Public')).toBeInTheDocument()
    expect(within(await envRow('CORS_ALLOWED_ORIGINS')).getByTestId('env-components')).toHaveTextContent('Backend')
  })
})

describe('Platform error copy (FR-144, D-V4-4)', () => {
  it('FR144_platform_copy: {platform} comes from the ProblemDetails, plan limits append the reason', () => {
    const plan = toApiError({ errorCode: 'PLATFORM_PLAN_LIMIT', title: 'Hobby teams cannot deploy org repositories.', platform: 'vercel' }, new Response(null, { status: 409 }))
    expect(errorMessage(plan)).toBe("Your Vercel plan doesn't allow this. Hobby teams cannot deploy org repositories.")
    const unsupported = toApiError({ errorCode: 'DEPLOYMENT_ACTION_UNSUPPORTED', platform: 'github_pages' }, new Response(null, { status: 409 }))
    expect(errorMessage(unsupported)).toBe("GitHub Pages doesn't support this action.")
    const failed = toApiError({ errorCode: 'PLATFORM_ERROR' }, new Response(null, { status: 502 }))
    expect(errorMessage(failed)).toBe('The platform returned an error. Try again later.')
  })
})
