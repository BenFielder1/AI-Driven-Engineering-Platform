import { errorMessage, isApiError, stackIncompatibleMessage, type ErrorContext } from '../../lib/errors'
import { databaseRequestBody, targetRequestBody } from '../deployments/host-setup-body'
import {
  deploymentSettingsPolling,
  fetchDeploymentSettings,
  putDeploymentTarget,
  putManagedDatabase,
  type DeploymentSettings,
} from '../deployments/deployments-api'
import { hostValues, isHostChosen, wizardHosts, type HostFormValues, type WizardHost } from './wizard-hosts'

export interface HostSetupFailure {
  title: string
  message: string
}

/** How long the wizard waits for the targets a dependent database needs. An object so tests can shorten it. */
export const hostSetupWait = { timeoutMs: 120_000 }

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

function failureText(error: unknown, context: ErrorContext): string {
  if (isApiError(error, 'STACK_INCOMPATIBLE') && error.violations.length > 0) {
    return stackIncompatibleMessage(error.violations)
  }
  if (isApiError(error, 'VALIDATION') && Object.keys(error.fieldErrors).length > 0) {
    return Object.values(error.fieldErrors).flat().join(' ')
  }
  return errorMessage(error, context)
}

const targetActive = (t: DeploymentSettings['targets'][number]) => t.setup?.state === 'queued' || t.setup?.state === 'running'

/** Polls until no target's `deployment_setup` job is running; `null` when it didn't settle in time or couldn't be read. */
async function settledTargets(projectId: string): Promise<DeploymentSettings['targets'] | null> {
  const deadline = Date.now() + hostSetupWait.timeoutMs
  try {
    for (;;) {
      const settings = await fetchDeploymentSettings(projectId)
      if (!settings.targets.some(targetActive)) {
        return settings.targets
      }
      if (Date.now() >= deadline) {
        return null
      }
      await delay(deploymentSettingsPolling.setupMs)
    }
  } catch {
    return null
  }
}

/**
 * §7: after the project is created, sets up the hosts chosen in the Deployment step through the same endpoints as Settings
 * → Deployment. Targets first; Render and Railway databases then wait for their target's setup job, as they read its
 * region or Railway project (§4.21). Each host is tried once and a failure never stops the others, so the project stays
 * usable and the caller can list what's left for Settings → Deployment. The API's 202s only queue the jobs.
 */
export async function applyHostSetups(projectId: string, values: HostFormValues): Promise<HostSetupFailure[]> {
  const chosen = wizardHosts(values).filter((h) => isHostChosen(values, h.slot))
  const targets = chosen.filter((h): h is Extract<WizardHost, { kind: 'target' }> => h.kind === 'target')
  const databases = chosen.filter((h): h is Extract<WizardHost, { kind: 'database' }> => h.kind === 'database')
  const failures: HostSetupFailure[] = []
  const queued: typeof targets = []

  for (const host of targets) {
    try {
      await putDeploymentTarget(projectId, host.component, targetRequestBody(host.provider, host.needsConnection, hostValues(values, host.slot)))
      queued.push(host)
    } catch (error) {
      failures.push({ title: host.title, message: failureText(error, 'deploymentTarget') })
    }
  }

  const dependsOnTargets = databases.some((d) => d.provider === 'render' || d.provider === 'railway')
  const needsWait = queued.length > 0 && dependsOnTargets
  const settled = needsWait ? await settledTargets(projectId) : null
  if (settled) {
    for (const host of queued) {
      const job = settled.find((t) => t.component === host.component)?.setup
      if (job?.state === 'failed') {
        failures.push({ title: host.title, message: job.message ?? 'The setup failed.' })
      }
    }
  }

  for (const host of databases) {
    const dependent = host.provider === 'render' || host.provider === 'railway'
    const blocked = dependent && targets.some((t) => t.provider === host.provider) && !settled?.some((t) => t.provider === host.provider && t.resourceId !== null)
    if (blocked) {
      failures.push({ title: host.title, message: `Not started: it needs the ${host.provider === 'render' ? 'Render' : 'Railway'} target to finish its setup first.` })
      continue
    }
    try {
      await putManagedDatabase(projectId, host.environment, databaseRequestBody(host.provider, host.needsConnection, hostValues(values, host.slot)))
    } catch (error) {
      failures.push({ title: host.title, message: failureText(error, 'databaseSetup') })
    }
  }
  return failures
}
