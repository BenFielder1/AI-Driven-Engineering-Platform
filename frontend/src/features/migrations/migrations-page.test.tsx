import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedSupabaseBaas } from '../../mocks/handlers/deployments'
import { seedMigrationRun } from '../../mocks/handlers/migrations'
import { currentVersionOf, type StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { hasSupabaseBaasDatabase } from '../shell/nav-config'

async function openMigrations(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/migrations`)
  await screen.findByRole('heading', { level: 2, name: 'Migrations' })
  return { ...utils, user: userEvent.setup() }
}

const runs = async () => within(await screen.findByRole('list', { name: 'Migration runs, newest first' })).getAllByRole('article')

/** A Supabase BaaS project (production database set up) with a succeeded production run and a newer failed preview run. */
function seedWithRuns(role: 'owner' | 'viewer' = 'viewer') {
  return seedProjectAs(role, (p) => {
    seedSupabaseBaas(p, { production: { status: 'available', name: 'shop' } })
    seedMigrationRun(p, {
      environment: 'production',
      gitSha: '1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      files: [
        { version: '20260901120000', name: 'init', ms: 300 },
        { version: '20260902120000', name: 'orders', ms: 1500 },
      ],
      totalMs: 1800,
      startedAt: '2026-09-02T10:00:00Z',
    })
    seedMigrationRun(p, {
      environment: 'preview',
      status: 'failed',
      gitSha: '2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      releaseVersionId: 'rv-other',
      files: [{ version: '20260903120000', name: 'refunds', ms: 90 }],
      totalMs: 90,
      errorCode: 'MIGRATION_FAILED',
      errorMessage: 'relation "refunds" already exists',
      advisorFindings: [
        {
          name: 'rls_disabled_in_public',
          level: 'ERROR',
          title: 'RLS Disabled in Public',
          detail: 'Table public.refunds is public, but RLS has not been enabled.',
          remediation: 'https://supabase.com/docs/guides/database/database-linter?lint=0013_rls_disabled_in_public',
        },
      ],
      startedAt: '2026-09-03T10:00:00Z',
    })
  })
}

describe('Migrations page (FR-153)', () => {
  it('FR153_runs_list: newest first, with environment, short SHA, files, duration and status', async () => {
    const project = seedWithRuns()
    await openMigrations(project)
    const [failed, succeeded] = (await runs()) as [HTMLElement, HTMLElement]
    expect(failed).toHaveAccessibleName('Preview migration run 2222222')
    expect(within(failed).getByTestId('migration-status')).toHaveTextContent('Failed')
    expect(succeeded).toHaveAccessibleName('Production migration run 1111111')
    expect(within(succeeded).getByTestId('migration-status')).toHaveTextContent('Succeeded')
    expect(within(succeeded).getByText(/took 1\.8 s · 2 files/)).toBeInTheDocument()
    const files = within(succeeded).getByRole('list', { name: 'Files applied' })
    expect(within(files).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['20260901120000 init300 ms', '20260902120000 orders1.5 s'])
    const version = currentVersionOf(project)?.version
    if (version) {
      expect(within(succeeded).getByText(`version ${version}`)).toBeInTheDocument()
    }
  })

  it("FR153_failed_run_error_and_advisors: a failed run's error copy and its advisor findings (level, title, remediation)", async () => {
    await openMigrations(seedWithRuns())
    const [failed, succeeded] = (await runs()) as [HTMLElement, HTMLElement]
    expect(within(failed).getByTestId('migration-error')).toHaveTextContent('A database migration failed: relation "refunds" already exists.')
    const finding = within(failed).getByTestId('advisor-finding')
    expect(within(finding).getByTestId('advisor-level')).toHaveTextContent('Error')
    expect(within(finding).getByText('RLS Disabled in Public')).toBeInTheDocument()
    expect(within(finding).getByText(/RLS has not been enabled/)).toBeInTheDocument()
    expect(within(finding).getByRole('link', { name: /How to fix it/ })).toHaveAttribute(
      'href',
      'https://supabase.com/docs/guides/database/database-linter?lint=0013_rls_disabled_in_public',
    )
    expect(within(succeeded).queryByTestId('migration-error')).not.toBeInTheDocument()
    expect(within(succeeded).queryByTestId('advisor-finding')).not.toBeInTheDocument()
  })

  it('FR153_version_filter: the release-version filter lists only that version’s runs', async () => {
    const project = seedWithRuns()
    const { user } = await openMigrations(project)
    expect(await runs()).toHaveLength(2)
    const version = currentVersionOf(project)
    expect(version).toBeDefined()
    await user.selectOptions(screen.getByLabelText('Release version'), version?.version ?? '')
    await waitFor(async () => {
      expect(await runs()).toHaveLength(1)
    })
    expect((await runs())[0]).toHaveAccessibleName('Production migration run 1111111')
  })

  it('FR153_empty_state: no runs yet', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedSupabaseBaas(p)
    })
    await openMigrations(project)
    expect(await screen.findByRole('heading', { name: 'No migration runs yet' })).toBeInTheDocument()
  })

  it('FR153_error_state: a failed load says so, with Retry', async () => {
    server.use(http.get('*/api/projects/:id/migrations', () => HttpResponse.json({ errorCode: 'INTERNAL', status: 500 }, { status: 500 })))
    await openMigrations(seedProjectAs('viewer'))
    expect(await screen.findByText("The migration runs couldn't load")).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Retry|Try again/ })).toBeInTheDocument()
  })
})

describe('Migrations nav item (FR-153, SPEC §7)', () => {
  it('FR153_nav_rule: data-supabase with a set-up Supabase database', () => {
    expect(hasSupabaseBaasDatabase(['data-supabase'], [{ provider: 'supabase', status: 'available' }])).toBe(true)
    expect(hasSupabaseBaasDatabase(['data-supabase'], [{ provider: 'supabase', status: 'not_configured' }])).toBe(false)
    expect(hasSupabaseBaasDatabase(['data-prisma'], [{ provider: 'supabase', status: 'available' }])).toBe(false)
    expect(hasSupabaseBaasDatabase(['data-supabase'], undefined)).toBe(false)
  })

  it('FR153_nav_item_shown: a Supabase BaaS project with a database shows Migrations in the project nav', async () => {
    const project = seedWithRuns()
    await openMigrations(project)
    const nav = await screen.findByRole('navigation', { name: 'Project sections' })
    const list = within(nav).getByRole('list', { name: 'Project' })
    const link = await within(list).findByRole('link', { name: 'Migrations' })
    expect(link).toHaveAttribute('href', `/projects/${project.id}/migrations`)
    expect(link).toHaveAttribute('aria-current', 'page')
  })

  it('FR153_nav_item_hidden: no Migrations item until the Supabase database is set up', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedSupabaseBaas(p)
    })
    await renderRoute(`/projects/${project.id}/settings`)
    const nav = await screen.findByRole('navigation', { name: 'Project sections' })
    const list = within(nav).getByRole('list', { name: 'Project' })
    await within(list).findByRole('link', { name: 'Settings' })
    // Let the deployment settings load (the rule reads them), then the item is still absent.
    await waitFor(() => {
      expect(within(list).queryByRole('link', { name: 'Migrations' })).not.toBeInTheDocument()
    })
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).not.toContain('Migrations')
  })
})
