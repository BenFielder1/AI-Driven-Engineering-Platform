import { describe, expect, it } from 'vitest'

import { describeActivity } from './activity-summary'

const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server summary' })

describe('FR-92: activity summaries of the v2 events', () => {
  it('FR92_activity_summaries_describe_every_v2_event_from_its_payload', () => {
    expect(d('VERSION_STARTED', { version: '1.3.0', baseTag: 'v1.2.0' })).toBe('Version 1.3.0 started from v1.2.0')
    expect(d('CODEBASE_SUMMARY_CREATED', { type: 'codebase_summary', version: 2 })).toBe(
      'Codebase summary v2 created by the Repository Analyst',
    )
    expect(d('ARTIFACT_RESTORED', { type: 'technical_specification', version: 3, restoredFromVersion: 1 })).toMatch(
      /v1 restored as draft v3$/,
    )
    expect(
      d('RELEASE_AWAITING_MERGE', { tag: 'v1.2.0', reasons: [{ code: 'REVIEW_REQUIRED', message: 'A review is required' }] }),
    ).toBe('Release v1.2.0 is waiting for its pull request to be merged on GitHub: A review is required')
    expect(d('RELEASE_MERGED_EXTERNALLY', { tag: 'v1.2.0' })).toBe('Release v1.2.0: the pull request was merged on GitHub')
    expect(d('DEPLOYMENT_CREATED', { target: 'production', trigger: 'promote', gitSha: 'abcdef1234' })).toBe(
      'Production deployment (abcdef1) promoted',
    )
    expect(d('DEPLOYMENT_UPDATED', { target: 'preview', status: 'building' })).toBe('Preview deployment building')
    expect(d('DEPLOYMENT_FAILED', { target: 'production', releaseVersion: '1.2.0', errorCode: 'VERCEL_ERROR' })).toBe(
      'Production deployment of 1.2.0 failed: Vercel reported the build as failed',
    )
    expect(d('DEPLOYMENT_FAILED', { target: 'preview', errorCode: 'SOMETHING_ELSE' })).toBe('Preview deployment failed')
    expect(d('DEPLOYMENT_PREVIEW_READY', { target: 'preview', url: 'https://p.vercel.app', releaseVersion: '1.2.0' })).toBe(
      'Preview deployment of 1.2.0 ready at https://p.vercel.app',
    )
    expect(d('CONNECTION_INVALID', { provider: 'anthropic' })).toBe(
      'Anthropic connection marked invalid: the provider rejected its key during a run',
    )
    expect(d('CONNECTION_INVALID', { provider: 'vercel' })).toBe('Vercel connection marked invalid: Vercel rejected its token')
    expect(d('PRESET_CHANGED', { version: '1.3.0', presetId: 'nextjs-frontend', presetVersion: '1.0.0' })).toBe(
      'Preset of 1.3.0 changed from no preset to nextjs-frontend@1.0.0',
    )
    expect(d('LLM_BINDING_CLEARED', { provider: 'openai', reason: 'member_removed' })).toBe(
      'OpenAI connection unbound: the Owner who bound it left the project',
    )
  })

  it('FR92_activity_summary_of_a_project_less_GitHub_connection_invalid', () => {
    expect(d('CONNECTION_INVALID', { provider: 'github', login: 'ada', reason: 'refresh_expired' })).toBe(
      'GitHub connection of @ada marked invalid: its refresh token expired; reconnect it under Connections',
    )
    expect(d('CONNECTION_INVALID', { provider: 'github' })).toBe('GitHub connection marked invalid; reconnect it under Connections')
  })
})
