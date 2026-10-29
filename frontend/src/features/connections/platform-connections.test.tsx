import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedDeploymentSettings, seedVercelConnection } from '../../mocks/handlers/deployments'
import { platformDb, seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { projectsDb, seedProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'

function signInAda() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

async function openConnections() {
  const utils = await renderRoute('/connections')
  await screen.findByRole('heading', { level: 1, name: 'Connections' })
  return { ...utils, user: userEvent.setup() }
}

const platformsSection = () => screen.getByRole('region', { name: 'Deployment platforms' })
const databasesSection = () => screen.getByRole('region', { name: 'Databases' })
const providerRegion = (name: string) => screen.getByRole('region', { name })

async function openAdd(user: ReturnType<typeof userEvent.setup>, provider: string) {
  await user.click(await screen.findByRole('button', { name: `Add ${provider} connection` }))
  return screen.findByRole('dialog', { name: `Add a ${provider} connection` })
}

const mutationState = (queryClient: { getMutationCache: () => { getAll: () => { state: unknown }[] } }) =>
  JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))

describe('/connections → platform connections (FR-142)', () => {
  it('FR142_four_sections: LLM providers, Source control, Deployment platforms and Databases, each provider with Add', async () => {
    signInAda()
    await openConnections()

    expect(screen.getByRole('heading', { level: 2, name: 'LLM providers' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Source control' })).toBeInTheDocument()
    const sourceControl = screen.getByRole('region', { name: 'Source control' })
    expect(within(sourceControl).getByRole('region', { name: 'GitHub' })).toBeInTheDocument()
    expect(within(sourceControl).getByRole('region', { name: 'Bitbucket' })).toBeInTheDocument()

    const platforms = platformsSection()
    for (const name of ['Vercel', 'Netlify', 'Cloudflare', 'Render', 'Railway']) {
      const region = await within(platforms).findByRole('region', { name })
      expect(within(region).getByText('Not connected')).toBeInTheDocument()
      expect(within(region).getByRole('button', { name: `Add ${name} connection` })).toBeInTheDocument()
    }
    expect(within(databasesSection()).getByRole('region', { name: 'Supabase' })).toBeInTheDocument()
    expect(within(platforms).queryByRole('region', { name: 'Supabase' })).not.toBeInTheDocument()
    // Logos are inline SVG (no external images, SPEC §7).
    expect(document.querySelectorAll('img')).toHaveLength(0)
  })

  it('FR142_add_netlify: the dialog says what to create, then a valid token is checked, saved and listed, never shown', async () => {
    signInAda()
    const { user, queryClient } = await openConnections()
    const dialog = await openAdd(user, 'Netlify')

    const guidance = within(dialog).getByTestId('platform-token-guidance')
    expect(guidance).toHaveTextContent('Token type: a personal access token')
    expect(guidance).toHaveTextContent('Expiry: Netlify asks for an expiry')
    expect(within(dialog).getByRole('link', { name: /Create a token on app\.netlify\.com/ })).toHaveAttribute(
      'href',
      'https://app.netlify.com/user/applications#personal-access-tokens',
    )
    const token = within(dialog).getByLabelText('Personal access token')
    expect(token).toHaveAttribute('type', 'password')
    await user.type(within(dialog).getByLabelText('Label'), 'Acme sites')
    await user.type(token, 'nfp_secret9z8y')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    const list = await within(providerRegion('Netlify')).findByRole('list', { name: 'Your Netlify connections' })
    const row = within(list).getByRole('listitem', { name: 'Netlify connection Acme sites' })
    expect(within(row).getByTestId('platform-account')).toHaveTextContent('Team Acme')
    expect(within(row).getByTestId('platform-account')).toHaveTextContent(/Expires/)
    expect(within(row).getByTestId('credential-status')).toHaveTextContent('Valid')
    expect(within(row).getByTestId('platform-last4')).toHaveTextContent('••••9z8y')
    expect(within(row).getByText('Not used by any project')).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent('nfp_secret9z8y')
    await waitFor(() => {
      expect(mutationState(queryClient)).not.toContain('nfp_secret9z8y')
    })
    expect(platformDb.connections).toHaveLength(1)
    expect(projectsDb.requests.find((r) => r.path === '/api/me/connections/platforms')?.body).toMatchObject({
      provider: 'netlify',
      label: 'Acme sites',
      accountId: null,
    })
  })

  it('FR142_cloudflare_account_token: a cfat_ token explains Workers Builds needs a user token; nothing is saved', async () => {
    signInAda()
    const { user, queryClient } = await openConnections()
    const dialog = await openAdd(user, 'Cloudflare')
    const permissions = within(dialog).getByRole('list', { name: 'Cloudflare permissions to grant' })
    expect(within(permissions).getByText('Workers Scripts: Edit')).toBeInTheDocument()
    expect(within(permissions).getByText('Workers CI: Edit')).toBeInTheDocument()
    expect(within(dialog).getByTestId('platform-token-guidance')).toHaveTextContent('a user API token')

    await user.type(within(dialog).getByLabelText('Label'), 'Workers')
    await user.type(within(dialog).getByLabelText('API token'), 'cfat_account0001')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    expect(
      (await within(dialog).findAllByText(/This is a Cloudflare account-owned token \(it starts with cfat_\), so it wasn't saved\. Workers Builds needs a user API token/))
        .length,
    ).toBeGreaterThan(0)
    expect(within(dialog).getByLabelText('API token')).toHaveValue('')
    await waitFor(() => {
      expect(mutationState(queryClient)).not.toContain('cfat_account0001')
    })
    expect(platformDb.connections).toHaveLength(0)
  })

  it('FR142_supabase_insufficient_scope: a token without API Key Secrets: Read saves as Missing permissions and names it', async () => {
    signInAda()
    platformDb.missingPermissions.set('sbp_scoped0042', ['API Key Secrets: Read'])
    const { user } = await openConnections()
    const dialog = await openAdd(user, 'Supabase')
    const permissions = within(dialog).getByRole('list', { name: 'Supabase permissions to grant' })
    expect(within(permissions).getByText('API Key Secrets: Read')).toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: /Create a token on supabase\.com/ })).toHaveAttribute(
      'href',
      'https://supabase.com/dashboard/account/tokens',
    )

    await user.type(within(dialog).getByLabelText('Label'), 'Acme org')
    await user.type(within(dialog).getByLabelText('Access token'), 'sbp_scoped0042')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    const row = await within(databasesSection()).findByRole('listitem', { name: 'Supabase connection Acme org' })
    expect(within(row).getByTestId('credential-status')).toHaveTextContent('Missing permissions')
    expect(within(row).getByTestId('platform-account')).toHaveTextContent('Organisation Acme')
    const missing = within(row).getByRole('list', { name: 'Missing permissions of Acme org' })
    expect(within(missing).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['API Key Secrets: Read'])
  })

  it('FR142_railway_workspace_id: the dialog asks for the workspace id of a workspace token and sends it as accountId', async () => {
    signInAda()
    const { user } = await openConnections()
    const dialog = await openAdd(user, 'Railway')
    expect(within(dialog).getByLabelText('Workspace ID (optional)')).toBeInTheDocument()
    await user.type(within(dialog).getByLabelText('Label'), 'Acme workspace')
    await user.type(within(dialog).getByLabelText('Token'), 'rw_token0007')
    await user.type(within(dialog).getByLabelText('Workspace ID (optional)'), 'ws_acme')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    const row = await within(providerRegion('Railway')).findByRole('listitem', { name: 'Railway connection Acme workspace' })
    expect(within(row).getByTestId('platform-account')).toHaveTextContent('Workspace Acme workspace · No expiry')
    expect(projectsDb.requests.find((r) => r.path === '/api/me/connections/platforms')?.body).toMatchObject({
      provider: 'railway',
      accountId: 'ws_acme',
    })
  })

  it('FR142_rate_limited: a 429 from the platform names the wait, and nothing is saved', async () => {
    signInAda()
    platformDb.rateLimitedFor = 30
    const { user } = await openConnections()
    const dialog = await openAdd(user, 'Render')
    await user.type(within(dialog).getByLabelText('Label'), 'Render')
    await user.type(within(dialog).getByLabelText('API key'), 'rnd_key0001')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    expect(
      await within(dialog).findByText('Render is rate limiting requests. Nothing changed; try again in 30 seconds.', { selector: 'p, span, li, a' }),
    ).toBeInTheDocument()
    expect(platformDb.connections).toHaveLength(0)
  })

  it('FR142_retest: Re-test marks a rejected token Invalid, and a token missing permissions lists them', async () => {
    const me = signInAda()
    const rejected = seedVercelConnection({ userId: me.id, label: 'Personal', token: 'vcp_xxxx1234' })
    platformDb.rejectedTokens.add(rejected.token)
    const scoped = seedPlatformConnection({ userId: me.id, provider: 'supabase', label: 'Org', token: 'sbp_org0099' })
    platformDb.missingPermissions.set(scoped.token, ['Auth Config: Write'])
    const { user } = await openConnections()

    const vercelRow = await screen.findByRole('listitem', { name: 'Vercel connection Personal' })
    expect(within(vercelRow).getByTestId('platform-account')).toHaveTextContent('Personal account (ada)')
    await user.click(within(vercelRow).getByRole('button', { name: 'Re-test Personal' }))
    expect(await screen.findByText('"Personal" is invalid: Vercel rejected it.')).toBeInTheDocument()
    await waitFor(() => {
      expect(within(vercelRow).getByTestId('credential-status')).toHaveTextContent('Invalid')
    })
    expect(vercelRow).toHaveTextContent('Vercel rejected this token. Add a new connection, then delete this one.')

    const supabaseRow = within(databasesSection()).getByRole('listitem', { name: 'Supabase connection Org' })
    await user.click(within(supabaseRow).getByRole('button', { name: 'Re-test Org' }))
    expect(await screen.findByText('"Org" is missing permissions on Supabase: Auth Config: Write.')).toBeInTheDocument()
    await waitFor(() => {
      expect(within(supabaseRow).getByTestId('credential-status')).toHaveTextContent('Missing permissions')
    })
    expect(within(supabaseRow).getByTestId('platform-missing-permissions')).toHaveTextContent('Auth Config: Write')
  })

  it('FR142_rename: Rename changes the label; the token is unchanged', async () => {
    const me = signInAda()
    const c = seedPlatformConnection({ userId: me.id, provider: 'render', label: 'Old', token: 'rnd_keyabcd' })
    const { user } = await openConnections()
    const row = await screen.findByRole('listitem', { name: 'Render connection Old' })
    await user.click(within(row).getByRole('button', { name: 'Rename Old' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename "Old"' })
    const field = within(dialog).getByLabelText('Label')
    await user.clear(field)
    await user.type(field, 'Production key')
    await user.click(within(dialog).getByRole('button', { name: 'Save label' }))

    expect(await within(providerRegion('Render')).findByRole('listitem', { name: 'Render connection Production key' })).toBeInTheDocument()
    expect(c.label).toBe('Production key')
    expect(c.token).toBe('rnd_keyabcd')
  })

  it('FR142_delete_warns_bound_projects: deleting a bound connection names the projects that lose their binding', async () => {
    const me = signInAda()
    const c = seedVercelConnection({ userId: me.id, label: 'Acme team', token: 'vcp_aaaa5678', teamSlug: 'acme' })
    const project = seedProject({ name: 'Storefront' })
    seedDeploymentSettings(project, { connection: c })
    const { user } = await openConnections()
    const row = await screen.findByRole('listitem', { name: 'Vercel connection Acme team' })
    expect(within(row).getByTestId('platform-account')).toHaveTextContent('Team acme · account ada')
    expect(within(row).getByRole('link', { name: 'Storefront' })).toHaveAttribute('href', `/projects/${project.id}/settings/deployment`)

    await user.click(within(row).getByRole('button', { name: 'Delete Acme team' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete "Acme team"?' })
    expect(within(confirm).getByTestId('platform-delete-bound-warning')).toHaveTextContent('Storefront')
    await user.click(within(confirm).getByRole('button', { name: 'Delete connection' }))

    expect(await within(providerRegion('Vercel')).findByText('Not connected')).toBeInTheDocument()
    expect(project.deployment?.binding).toBeNull()
    expect(platformDb.connections).toHaveLength(0)
  })

  it('FR142_token_required: an empty token is caught before anything is sent', async () => {
    signInAda()
    const { user } = await openConnections()
    const dialog = await openAdd(user, 'Vercel')
    await user.type(within(dialog).getByLabelText('Label'), 'Mine')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))
    expect((await within(dialog).findAllByText('Paste the Vercel access token.')).length).toBeGreaterThan(0)
    expect(projectsDb.requests.filter((r) => r.path.endsWith('/connections/platforms'))).toHaveLength(0)
  })
})
