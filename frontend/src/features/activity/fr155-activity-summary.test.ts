import { describe, expect, it } from 'vitest'

import { describeActivity } from './activity-summary'

const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server summary' })

const dateOf = (iso: string) => new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(iso))

describe('FR-155: activity summaries of the v4 events', () => {
  it('FR155_STACK_CHANGED names the changed layers', () => {
    expect(d('STACK_CHANGED', { version: '1.3.0', layersChanged: ['backend', 'data'] })).toBe('Stack changed: Backend, Data')
  })

  it('FR155_PRESET_CHANGED history stays readable', () => {
    expect(d('PRESET_CHANGED', { version: '1.3.0', fromPresetId: null, presetId: 'nextjs-frontend', presetVersion: '1.0.0' })).toBe(
      'Preset of 1.3.0 changed from no preset to nextjs-frontend@1.0.0',
    )
  })

  it('FR155_STACK_DETECTED counts what the detector found', () => {
    expect(
      d('STACK_DETECTED', {
        version: '1.3.0',
        baseCommitSha: 'abcdef1234',
        suggestions: [{ choice: 'frontend' }, { choice: 'data' }],
        conflicts: ['frontend'],
        mismatches: [],
      }),
    ).toBe('Stack detected at the base of 1.3.0 (abcdef1): 2 suggestions, 1 conflict')
    expect(d('STACK_DETECTED', { version: '1.3.0', mismatches: [{}, {}] })).toBe('Stack detected at the base of 1.3.0: 2 mismatches')
  })

  it('FR155_GIT_HOST_INSTANCE_CHANGED names the host, instance and change', () => {
    expect(d('GIT_HOST_INSTANCE_CHANGED', { kind: 'gitlab', change: 'created', name: 'Acme GitLab' })).toBe(
      'GitLab instance "Acme GitLab" added',
    )
    expect(d('GIT_HOST_INSTANCE_CHANGED', { kind: 'bitbucket', change: 'updated', name: 'Bitbucket Cloud' })).toBe(
      'Bitbucket instance "Bitbucket Cloud" changed',
    )
    expect(d('GIT_HOST_INSTANCE_CHANGED', { kind: 'gitlab', change: 'removed' })).toBe('GitLab instance removed')
    expect(d('GIT_HOST_INSTANCE_CHANGED', { kind: 'gitlab', change: 'other' })).toBe('server summary')
  })

  it('FR155_GIT_BINDING_CLEARED says why, for every reason', () => {
    const tail = '; an Owner re-binds it in Settings → Git'
    expect(d('GIT_BINDING_CLEARED', { provider: 'gitlab', reason: 'owner_role_lost' })).toBe(
      `GitLab connection unbound: the Owner who bound it is no longer an Owner${tail}`,
    )
    expect(d('GIT_BINDING_CLEARED', { provider: 'bitbucket', reason: 'member_removed' })).toBe(
      `Bitbucket connection unbound: the Owner who bound it left the project${tail}`,
    )
    expect(d('GIT_BINDING_CLEARED', { provider: 'gitlab', reason: 'connection_deleted' })).toBe(
      `GitLab connection unbound: the connection was deleted${tail}`,
    )
    expect(d('GIT_BINDING_CLEARED', { provider: 'gitlab', reason: 'connection_invalid' })).toBe(
      `GitLab connection unbound: GitLab rejected the connection${tail}`,
    )
    expect(d('GIT_BINDING_CLEARED', {})).toBe(`Git connection unbound${tail}`)
  })

  it('FR155_DEPLOYMENT_TARGET_CONFIGURED names the component and platform', () => {
    expect(d('DEPLOYMENT_TARGET_CONFIGURED', { component: 'frontend', provider: 'netlify', status: 'ready' })).toBe(
      'Frontend deployment target set up on Netlify',
    )
    expect(d('DEPLOYMENT_TARGET_CONFIGURED', { component: 'backend', provider: 'render', status: 'attention' })).toBe(
      'Backend deployment target set up on Render; it needs attention',
    )
  })

  it('FR155_DEPLOYMENT_TARGET_FAILED words every reason', () => {
    const target = { component: 'frontend', provider: 'netlify' }
    expect(d('DEPLOYMENT_TARGET_FAILED', { ...target, reason: 'git_access_lost', errorCode: 'PLATFORM_GIT_NOT_CONNECTED' })).toBe(
      'Frontend deployment target on Netlify needs attention: Netlify can no longer reach the repository',
    )
    expect(d('DEPLOYMENT_TARGET_FAILED', { ...target, reason: 'token_invalid', errorCode: 'CONNECTION_INVALID' })).toBe(
      "Frontend deployment target on Netlify needs attention: Netlify rejected the connection's token",
    )
    expect(d('DEPLOYMENT_TARGET_FAILED', { ...target, reason: 'setup_failed' })).toBe(
      'Frontend deployment target on Netlify needs attention: its setup failed',
    )
    expect(d('DEPLOYMENT_TARGET_FAILED', { ...target, reason: 'readiness_failed', errorCode: 'PLATFORM_PLAN_LIMIT' })).toMatch(
      /^Frontend deployment target on Netlify needs attention: a readiness check failed\. Your Netlify plan/,
    )
  })

  it('FR155_DATABASE_PROVISIONED says how it became available and when it expires', () => {
    const db = { environment: 'preview', provider: 'render' }
    const expiresAt = '2026-11-20T12:00:00Z'
    expect(d('DATABASE_PROVISIONED', { ...db, mode: 'create', expiresAt })).toBe(
      `Preview database on Render Postgres created (expires ${dateOf(expiresAt)})`,
    )
    expect(d('DATABASE_PROVISIONED', { environment: 'production', provider: 'supabase', mode: 'link', expiresAt: null })).toBe(
      'Production database on Supabase linked',
    )
    expect(d('DATABASE_PROVISIONED', { environment: 'production', provider: 'external', mode: 'external' })).toBe(
      'Production database on External database connected',
    )
  })

  it('FR155_DATABASE_PROVISIONING_FAILED adds the code copy unless it only repeats the line', () => {
    const db = { environment: 'production', provider: 'render' }
    expect(d('DATABASE_PROVISIONING_FAILED', { ...db, errorCode: 'DATABASE_PROVISIONING_FAILED' })).toBe(
      'Production database on Render Postgres: setup failed',
    )
    expect(d('DATABASE_PROVISIONING_FAILED', { ...db, errorCode: 'DATABASE_PROVISIONING_TIMEOUT' })).toBe(
      'Production database on Render Postgres: setup failed. The database took too long to become available.',
    )
  })

  it('FR155_DATABASE_EXPIRING shows the date, or that the project is paused', () => {
    const expiresAt = '2026-11-08T12:00:00Z'
    expect(d('DATABASE_EXPIRING', { environment: 'preview', provider: 'render', reason: 'expiry', expiresAt })).toBe(
      `Preview database on Render Postgres expires on ${dateOf(expiresAt)}`,
    )
    expect(d('DATABASE_EXPIRING', { environment: 'production', provider: 'supabase', reason: 'paused', expiresAt: null })).toBe(
      'Production database on Supabase is paused: restore the project to keep using it',
    )
  })

  it('FR155_DATABASE_RESET names the provider and only claims a schema reset AIDE ran', () => {
    expect(d('DATABASE_RESET', { provider: 'supabase', method: 'reset_schema', environment: 'preview' })).toBe(
      'Preview database on Supabase reset: its schema was emptied',
    )
    expect(d('DATABASE_RESET', { provider: 'render', method: 'reset_schema_external', environment: 'preview' })).toBe(
      'Preview database on Render Postgres reset: its schema was emptied',
    )
    expect(d('DATABASE_RESET', { provider: 'railway', method: 'manual', environment: 'preview' })).toBe(
      'Preview database on Railway Postgres recorded as reset',
    )
  })

  it('FR155_MIGRATION_RUN_STARTED and COMPLETED name the environment and commit', () => {
    const run = { runId: 'r1', environment: 'production', gitSha: 'abcdef1234' }
    expect(d('MIGRATION_RUN_STARTED', run)).toBe('Production migration run (abcdef1) started')
    expect(d('MIGRATION_RUN_COMPLETED', { ...run, applied: 3 })).toBe('Production migration run (abcdef1) applied 3 files')
    expect(d('MIGRATION_RUN_COMPLETED', { ...run, applied: 1 })).toBe('Production migration run (abcdef1) applied 1 file')
    expect(d('MIGRATION_RUN_COMPLETED', { ...run, applied: 0 })).toBe('Production migration run (abcdef1) completed: nothing new to apply')
  })

  it('FR155_MIGRATION_RUN_FAILED covers a BaaS run and a host pre-deploy', () => {
    const run = { environment: 'preview', gitSha: 'abcdef1234' }
    expect(d('MIGRATION_RUN_FAILED', { ...run, runId: 'r1', errorCode: 'MIGRATION_FAILED' })).toBe('Preview migration run (abcdef1) failed')
    expect(d('MIGRATION_RUN_FAILED', { ...run, runId: 'r1', errorCode: 'MIGRATION_HISTORY_DIVERGED' })).toBe(
      "Preview migration run (abcdef1) failed. The database's migration history doesn't match the repository.",
    )
    expect(d('MIGRATION_RUN_FAILED', { ...run, runId: null, deploymentId: 'd1', errorCode: 'MIGRATION_FAILED' })).toBe(
      'Preview migrations (abcdef1) failed during the deployment',
    )
  })

  it('FR155_PREVIEW_ROLLOUT_COMPLETED reads every end state', () => {
    const rollout = { gitSha: 'abcdef1234' }
    expect(
      d('PREVIEW_ROLLOUT_COMPLETED', {
        ...rollout,
        state: 'succeeded',
        components: [
          { component: 'backend', status: 'ready', url: 'https://api.example.com' },
          { component: 'frontend', status: 'ready', url: 'https://web.example.com' },
        ],
      }),
    ).toBe('Preview rollout (abcdef1) succeeded: Backend at https://api.example.com, Frontend at https://web.example.com')
    expect(d('PREVIEW_ROLLOUT_COMPLETED', { ...rollout, state: 'succeeded', components: [] })).toBe('Preview rollout (abcdef1) succeeded')
    expect(d('PREVIEW_ROLLOUT_COMPLETED', { ...rollout, state: 'failed', errorCode: 'NOT_A_CODE', message: 'The build broke.' })).toBe(
      'Preview rollout (abcdef1) failed. The build broke.',
    )
    expect(d('PREVIEW_ROLLOUT_COMPLETED', { ...rollout, state: 'blocked', errorCode: 'PREVIEW_DATABASE_DIVERGED' })).toMatch(
      /^Preview rollout \(abcdef1\) blocked\. /,
    )
    expect(d('PREVIEW_ROLLOUT_COMPLETED', { ...rollout, state: 'canceled' })).toBe('Preview rollout (abcdef1) replaced by a newer preview')
    expect(d('PREVIEW_ROLLOUT_COMPLETED', { ...rollout, state: 'nonsense' })).toBe('server summary')
  })
})
