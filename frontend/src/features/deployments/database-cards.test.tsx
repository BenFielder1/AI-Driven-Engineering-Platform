import { screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { errorMessages, withJobDetail } from '../../lib/errors'
import { seedUser } from '../../mocks/handlers/auth'
import { makeDatabase, seedNetlifyRenderTargets, seedSupabaseBaas, deploymentOf } from '../../mocks/handlers/deployments'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import { bindingOf } from '../../mocks/handlers/deployments'
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

const card = (name: 'Production database' | 'Preview database') => screen.findByRole('region', { name })

beforeEach(() => {
  deploymentSettingsPolling.setupMs = 20
})

afterEach(() => {
  deploymentSettingsPolling.setupMs = 2_000
})

describe('Settings → Deployment database cards (FR-152)', () => {
  it('FR152_render_create_provisioning_available: binds an own Render connection, creates Render Postgres, shows the steps, then Available with the derived DATABASE_URL (FR-145)', async () => {
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_dddd4444' })
    })
    const dbs = screen.getByRole('list', { name: 'Databases' })
    expect(within(dbs).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual(['Production database', 'Preview database'])

    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-provider')).toHaveTextContent('Render Postgres')
    expect(within(prod).getByTestId('database-status')).toHaveTextContent('Not set up')
    const form = within(prod).getByRole('form', { name: 'Set up the production database on Render Postgres' })
    await user.click(within(form).getByRole('button', { name: 'Set up Render Postgres' }))
    expect((await within(form).findAllByText('Pick one of your Render connections.')).length).toBeGreaterThan(0)
    expect(requestsTo('PUT', '/databases/production')).toHaveLength(0)

    await user.selectOptions(await within(form).findByLabelText('Render connection'), 'Render key (••••4444) · ada')
    await user.type(within(form).getByLabelText('Plan (optional)'), 'free')
    await user.click(within(form).getByRole('button', { name: 'Set up Render Postgres' }))

    expect(await within(prod).findByTestId('provisioning-progress')).toHaveTextContent(/Setting up/)
    expect(requestsTo('PUT', '/databases/production').at(-1)?.body).toEqual({
      provider: 'render',
      connectionId: expect.any(String) as string,
      create: { plan: 'free' },
    })
    await waitFor(() => {
      expect(within(prod).getByTestId('database-status')).toHaveTextContent('Available')
    })
    expect(within(prod).queryByTestId('provisioning-progress')).not.toBeInTheDocument()
    expect(within(prod).getByTestId('database-binding')).toHaveTextContent('Render · Render key (••••4444) · account ada · connected by Ada Lovelace')
    expect(within(prod).getByTestId('database-region')).toHaveTextContent('oregon')
    expect(within(prod).getByTestId('database-plan')).toHaveTextContent('free')
    expect(within(prod).getByTestId('database-expires')).toBeInTheDocument()
    expect(within(prod).getByTestId('database-warning')).toHaveTextContent(/Render's free Postgres expires on .*14-day grace period/)
    expect(within(prod).getByRole('link', { name: /Open in Render/ })).toHaveAttribute('target', '_blank')

    const row = await screen.findByRole('listitem', { name: 'Variable DATABASE_URL' })
    expect(within(row).getByTestId('env-derived-from')).toHaveTextContent('From managed database (Render Postgres) · read-only')
  })

  it('FR152_external_secret_field: the connection string is a password field, validated, sent once and never shown', async () => {
    const { user } = await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p)
      deploymentOf(p).databases = [makeDatabase('production', 'external')]
    })
    const prod = await card('Production database')
    const form = within(prod).getByRole('form', { name: 'Set up the production database on External database' })
    const field = within(form).getByLabelText('Production connection string')
    expect(field).toHaveAttribute('type', 'password')
    expect(field).toHaveAttribute('autocomplete', 'off')
    expect(within(prod).getByTestId('database-binding')).toHaveTextContent('No platform connection')

    await user.type(field, 'not a connection string')
    await user.click(within(form).getByRole('button', { name: 'Save the connection string' }))
    expect((await within(form).findAllByText(/Enter a postgres:\/\/ or postgresql:\/\/ URI/)).length).toBeGreaterThan(0)
    expect(requestsTo('PUT', '/databases/production')).toHaveLength(0)

    await user.clear(field)
    await user.type(field, 'postgresql://app:s3cret-pw@db.example.com:5432/shop')
    await user.click(within(form).getByRole('button', { name: 'Save the connection string' }))
    await waitFor(() => {
      expect(requestsTo('PUT', '/databases/production').at(-1)?.body).toEqual({
        provider: 'external',
        external: { connectionString: 'postgresql://app:s3cret-pw@db.example.com:5432/shop' },
      })
    })
    await waitFor(() => {
      expect(within(prod).getByTestId('database-status')).toHaveTextContent('Available')
    })
    expect(within(prod).getByTestId('database-connection-summary')).toHaveTextContent('db.example.com:5432/shop')
    expect(within(prod).queryByRole('form')).not.toBeInTheDocument()
    expect(document.body.innerHTML).not.toContain('s3cret-pw')
  })

  it('FR152_supabase_missing_permissions: a Supabase card lists the permissions its token lacks', async () => {
    await openAsOwner((me, p) => {
      const c = seedPlatformConnection({ userId: me, provider: 'supabase', label: 'Supabase PAT', token: 'sbp_eeee5555', accountName: 'Acme' })
      seedSupabaseBaas(p, {
        production: {
          status: 'available',
          binding: bindingOf(c),
          resourceId: 'abcdefghijklmnop',
          name: 'shop',
          region: 'eu-west-2',
          plan: 'free',
          missingPermissions: ['API Key Secrets: Read', 'Auth Config: Write'],
          warnings: ['supabase_free_pausing'],
        },
      })
    })
    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-binding')).toHaveTextContent('Supabase · Supabase PAT (••••5555) · account Acme · connected by Ada Lovelace')
    const missing = within(prod).getByRole('list', { name: 'Missing permissions' })
    expect(within(missing).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['API Key Secrets: Read', 'Auth Config: Write'])
    expect(within(prod).getByTestId('database-warning')).toHaveTextContent('Supabase pauses free-plan projects after 7 days of inactivity.')
  })

  it('FR152_paused_restore: a paused Supabase database says Paused and Restore brings it back', async () => {
    const { user } = await openAsOwner((me, p) => {
      const c = seedPlatformConnection({ userId: me, provider: 'supabase', label: 'Supabase PAT', token: 'sbp_ffff6666' })
      seedSupabaseBaas(p, {
        production: { status: 'paused', paused: true, canRestore: true, binding: bindingOf(c), resourceId: 'abcdefghijklmnop', name: 'shop' },
      })
    })
    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-status')).toHaveTextContent('Paused')
    expect(within(prod).getByText(/Supabase paused this database/)).toBeInTheDocument()
    await user.click(within(prod).getByRole('button', { name: 'Restore the production database' }))
    await waitFor(() => {
      expect(requestsTo('POST', '/databases/production/restore')).toHaveLength(1)
    })
    await waitFor(() => {
      expect(within(prod).getByTestId('database-status')).toHaveTextContent('Available')
    })
    expect(within(prod).queryByText(/Supabase paused this database/)).not.toBeInTheDocument()
  })

  it('FR152_expiry_warning: an expiring Render free database shows Expiring, the date and the warning as text', async () => {
    await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      const c = seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_gggg7777' })
      deploymentOf(p).databases[0] = makeDatabase('production', 'render', {
        status: 'expiring',
        binding: bindingOf(c),
        resourceId: 'dpg-123',
        name: 'shop-db',
        plan: 'free',
        expiresAt: '2026-11-05T00:00:00Z',
        warnings: ['render_free_expiry'],
      })
    })
    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-status')).toHaveTextContent('Expiring')
    expect(within(prod).getByTestId('database-expires')).toHaveTextContent(/2026/)
    expect(within(prod).getByTestId('database-warning')).toHaveTextContent(/^Warning: Render's free Postgres expires on .*2026/)
  })

  it('FR152_provisioning_timeout: a timed-out provisioning job shows the shared error copy', async () => {
    await openAsOwner((_me, p) => {
      seedSupabaseBaas(p, {
        production: {
          status: 'error',
          provisioning: {
            jobId: 'job-1',
            state: 'failed',
            step: null,
            errorCode: 'DATABASE_PROVISIONING_TIMEOUT',
            errorMessage: null,
            startedAt: '2026-09-02T10:00:00Z',
            finishedAt: '2026-09-02T10:10:00Z',
          },
        },
      })
    })
    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-status')).toHaveTextContent('Error')
    expect(within(prod).getByTestId('provisioning-error')).toHaveTextContent('The database took too long to become available.')
  })

  it('FR152_unlink_confirm: Unlink asks first, then the card is back to Not set up', async () => {
    const { user } = await openAsOwner((me, p) => {
      seedNetlifyRenderTargets(p)
      const c = seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_hhhh8888' })
      deploymentOf(p).databases[0] = makeDatabase('production', 'render', { status: 'available', binding: bindingOf(c), resourceId: 'dpg-1', name: 'shop-db' })
    })
    const prod = await card('Production database')
    await user.click(within(prod).getByRole('button', { name: 'Unlink the production database' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Unlink the production database?' })
    expect(within(dialog).getByText(/Nothing is deleted on Render/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Unlink' }))
    await waitFor(() => {
      expect(requestsTo('DELETE', '/databases/production')).toHaveLength(1)
    })
    await waitFor(() => {
      expect(within(prod).getByTestId('database-status')).toHaveTextContent('Not set up')
    })
  })

  it('FR152_read_only_for_members: a Contributor sees the cards without setup, Unlink or Restore', async () => {
    const project = seedProjectAs('contributor', (p) => {
      const owner = seedUser({ email: 'owner2@example.com', name: 'Owner Two', systemRole: 'user' })
      const c = seedPlatformConnection({ userId: owner.id, provider: 'supabase', label: 'Their PAT', token: 'sbp_iiii9999' })
      seedSupabaseBaas(p, { production: { status: 'paused', paused: true, canRestore: true, binding: bindingOf(c), name: 'shop' } })
    })
    await openSettings('deployment', project)
    const prod = await card('Production database')
    expect(within(prod).getByTestId('database-binding')).toHaveTextContent('connected by Owner Two')
    expect(within(prod).getByText(/A project Owner can restore it/)).toBeInTheDocument()
    expect(within(prod).queryByRole('button')).not.toBeInTheDocument()
    expect(within(await card('Preview database')).queryByRole('form')).not.toBeInTheDocument()
  })

  it('FR145_supabase_derived_rows: database-derived variables say where they come from', async () => {
    await openAsOwner((_me, p) => {
      seedSupabaseBaas(p, { production: { status: 'available', name: 'shop' } })
    })
    const row = await screen.findByRole('listitem', { name: 'Variable VITE_SUPABASE_URL' })
    expect(within(row).getByTestId('env-derived-from')).toHaveTextContent('From Supabase project (production) · read-only')
  })
})

describe('Database and migration error copy (FR-147, FR-152, FR-153, SPEC §6)', () => {
  it('FR152_FR153_error_copy: the new codes have the spec copy, with the job detail where the spec has {detail}', () => {
    expect(errorMessages.PREVIEW_DATABASE_DIVERGED).toBe(
      'The preview database has migrations this branch no longer has. Reset the preview database to continue.',
    )
    expect(errorMessages.MIGRATION_HISTORY_DIVERGED).toBe("The database's migration history doesn't match the repository.")
    expect(errorMessages.DATABASE_PROVISIONING_TIMEOUT).toBe('The database took too long to become available.')
    expect(withJobDetail('MIGRATION_FAILED', 'supabase', 'relation "orders" already exists')).toBe(
      'A database migration failed: relation "orders" already exists.',
    )
    expect(withJobDetail('DATABASE_PROVISIONING_FAILED', 'render', 'Quota exceeded.')).toBe('Creating the database failed: Quota exceeded.')
    expect(withJobDetail('DATABASE_PROVISIONING_FAILED', 'render', null)).toBe('Creating the database failed.')
    expect(withJobDetail('PLATFORM_PLAN_LIMIT', 'supabase', 'Two free projects at most.')).toBe(
      "Your Supabase plan doesn't allow this. Two free projects at most.",
    )
  })
})
