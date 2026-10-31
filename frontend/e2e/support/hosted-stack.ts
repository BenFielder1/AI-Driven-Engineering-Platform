import { expect, type BrowserContext } from '@playwright/test'

import { apiJson } from './api.ts'

/**
 * SPEC §8 v4 E2E helpers on the Fake deployment and database providers (§9): platform connections, deployment targets and
 * managed databases set up through the real API (the jobs run on the API's own workers), and the rollouts they produce.
 */

export type Platform = 'netlify' | 'render' | 'supabase'

/** FR-142: a platform connection of the context's user. The Fake validator accepts any token not starting `fake_invalid`. */
export async function createPlatformConnection(context: BrowserContext, provider: Platform, label: string): Promise<string> {
  const connection = await apiJson<{ id: string; status: string }>(context, 'POST', '/api/me/connections/platforms', {
    provider,
    label,
    token: `fake_e2e_${provider}_${String(Date.now())}`,
  })
  expect(connection.status).toBe('valid')
  return connection.id
}

export const putTarget = (context: BrowserContext, projectId: string, component: 'frontend' | 'backend', provider: Platform, connectionId: string, name: string) =>
  apiJson(context, 'PUT', `/api/projects/${projectId}/settings/deployment/targets/${component}`, {
    provider,
    connectionId,
    create: { name },
  })

/** FR-152: provisions the environment's database (the Fake's resource ids differ per environment, so they are separate). */
export const putDatabase = (context: BrowserContext, projectId: string, environment: 'preview' | 'production', provider: Platform, connectionId: string, region: string) =>
  apiJson(context, 'PUT', `/api/projects/${projectId}/settings/deployment/databases/${environment}`, {
    provider,
    connectionId,
    create: { region, plan: 'free' },
  })

interface DeploymentSettings {
  targets: { component: string; setup: { state: string } | null; resourceId: string | null }[]
  databases: { environment: string; status: string }[]
}

/**
 * Waits until every target in `targets` finished its setup job and every database in `databases` is available
 * (FR-144, FR-152: the Fake database becomes available on its 2nd poll, 10 s apart).
 */
export async function waitForHostsReady(context: BrowserContext, projectId: string, targets: string[], databases: string[]) {
  await expect
    .poll(
      async () => {
        const s = await apiJson<DeploymentSettings>(context, 'GET', `/api/projects/${projectId}/settings/deployment`)
        return {
          targets: targets.map((c) => s.targets.find((t) => t.component === c)?.setup?.state ?? 'none'),
          databases: databases.map((e) => s.databases.find((d) => d.environment === e)?.status ?? 'none'),
        }
      },
      { timeout: 120_000, intervals: [500, 1000, 2000] },
    )
    .toEqual({ targets: targets.map(() => 'succeeded'), databases: databases.map(() => 'available') })
}

export interface RolloutStepDto {
  step: 'migrations' | 'backend' | 'frontend'
  state: string
  provider: string | null
  status: string | null
  url: string | null
  migrationRunId: string | null
}

export interface RolloutDto {
  state: string
  gitSha: string
  errorCode: string | null
  errorMessage: string | null
  steps: RolloutStepDto[]
}

/** FR-147: polls the project's preview environment until its current rollout has `state`; returns the rollout. */
export async function waitForPreviewRollout(context: BrowserContext, projectId: string, state: string, timeout = 180_000): Promise<RolloutDto> {
  let current: RolloutDto | null = null
  await expect
    .poll(
      async () => {
        current = (await apiJson<{ current: RolloutDto | null }>(context, 'GET', `/api/projects/${projectId}/deployments/preview`)).current
        return current ? `${current.state} ${current.errorCode ?? ''} ${current.errorMessage ?? ''}`.trim() : 'none'
      },
      { timeout, intervals: [500, 1000, 2000] },
    )
    .toBe(state)
  return current as unknown as RolloutDto
}

export interface ReleaseDto {
  tag: string | null
  status: string
  rollout: RolloutDto | null
  liveOn: { component: string; providerName: string; url: string | null }[] | null
}

/** FR-149: polls the latest release until its production rollout has succeeded (the rollout gives the platform up to 60 s per component to build the merge commit itself); returns the release. */
export async function waitForProductionRollout(context: BrowserContext, projectId: string, timeout = 300_000): Promise<ReleaseDto> {
  let release: ReleaseDto | null = null
  await expect
    .poll(
      async () => {
        release = await apiJson<ReleaseDto>(context, 'GET', `/api/projects/${projectId}/releases/latest`)
        const rollout = release.rollout
        return rollout ? `${rollout.state} ${rollout.errorCode ?? ''} ${rollout.errorMessage ?? ''}`.trim() : 'none'
      },
      { timeout, intervals: [1000, 2000] },
    )
    .toBe('succeeded')
  return release as unknown as ReleaseDto
}

/** SPEC §8's hosted composition, for a project created through the API (a Local repository needs the VCS layer, R5). */
export const NETLIFY_RENDER_STACK = {
  frontend: { pack: 'fe-react-vite', path: 'frontend' },
  backend: { pack: 'be-aspnet', path: 'backend' },
  data: { pack: 'data-efcore-postgres' },
  vcs: { pack: 'vcs-github' },
  deploy: { frontendHost: 'deploy-netlify', backendHost: 'deploy-render', databaseHost: 'render-postgres' },
}

/** The hosts of `NETLIFY_RENDER_STACK`: both targets, both databases and their connections, set up and waited for. */
export async function setUpNetlifyRenderHosts(context: BrowserContext, projectId: string, slug: string) {
  const netlify = await createPlatformConnection(context, 'netlify', 'Fake Netlify')
  const render = await createPlatformConnection(context, 'render', 'Fake Render')
  await putTarget(context, projectId, 'backend', 'render', render, `${slug}-api`)
  await putTarget(context, projectId, 'frontend', 'netlify', netlify, `${slug}-web`)
  await putDatabase(context, projectId, 'production', 'render', render, 'oregon')
  await putDatabase(context, projectId, 'preview', 'render', render, 'oregon')
  await waitForHostsReady(context, projectId, ['backend', 'frontend'], ['production', 'preview'])
}
