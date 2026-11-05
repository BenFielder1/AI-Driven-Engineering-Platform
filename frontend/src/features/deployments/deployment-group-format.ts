import type { Schemas } from '../../api/client'
import { platformCopy } from '../../lib/errors'
import type { PlatformResourceType } from '../connections/platform-connections-api'
import { databaseStatusLabels } from './database-format'
import { targetStatusLabels } from './deployment-format'

type Part = Schemas['DeploymentPart']
type Capability = Schemas['SharedProjectCapability']

export const deploymentPartLabels: Record<Part, string> = {
  frontend: 'Frontend',
  backend: 'Backend',
  database: 'Database',
}

/** FR-188: "Frontend, backend and database on Railway", "Backend on Render". */
export function deploymentPartsTitle(parts: readonly Part[], platform: string): string {
  const words = parts.map((p) => p as string)
  const last = words.pop() ?? ''
  const text = words.length > 0 ? `${words.join(', ')} and ${last}` : last
  return `${text.charAt(0).toUpperCase()}${text.slice(1)} on ${platform}`
}

/** FR-192's sentence for a platform that shares a setup, not a project. */
export const SHARED_SETUP_NOTE = 'Vercel keeps each part in its own project; these use the same team and name.'

/** §4.28's reason a group's parts are outside the platform's project, while the group still works. */
export const PROJECT_PLACEMENT_UNAVAILABLE = 'project_placement_unavailable'

/** A group's or part's `statusReason` as text: the known reason, a code's copy, or the reason itself. */
export function groupReasonText(reason: string | null | undefined, provider: string): string | null {
  if (!reason) {
    return null
  }
  return reason === PROJECT_PLACEMENT_UNAVAILABLE ? "Render project grouping isn't available" : (platformCopy(reason, provider) ?? reason)
}

/** A part's status in words: a target's or a database's (never colour alone). */
export const groupPartStatusLabel = (status: string): string =>
  (targetStatusLabels as Readonly<Record<string, string>>)[status] ?? databaseStatusLabels[status] ?? status

export const groupStatusLabel = (status: string): string => (targetStatusLabels as Readonly<Record<string, string>>)[status] ?? status

/** Create or Link for a group: one project, or (Vercel) a shared setup of one project per part. */
export function groupModeOptions(capability: Capability) {
  return capability === 'shared_setup'
    ? [
        { value: 'create', label: 'Create new projects', description: 'One per part, named after the name below.' },
        { value: 'link', label: 'Link existing projects', description: 'Pick the project of each part.' },
      ]
    : [
        { value: 'create', label: 'Create a new project', description: 'Tevrin creates it, then puts every part in it.' },
        { value: 'link', label: 'Link an existing project', description: 'Then, for each part, pick an existing service or create it in this project.' },
      ]
}

/** The connection's picker for a group's project, or `null` when the Owner types the id (Fake). */
export const groupProjectPicker = (provider: Schemas['DeploymentProvider']): PlatformResourceType | null =>
  provider === 'railway' || provider === 'render' ? 'projects' : null

/** FR-190: the connection's picker for one part's existing resource. */
export function groupPartPicker(provider: Schemas['DeploymentProvider'], part: Part): PlatformResourceType {
  if (provider === 'vercel') {
    return 'projects'
  }
  return provider === 'render' && part === 'database' ? 'databases' : 'services'
}

export const groupPartNoun = (provider: Schemas['DeploymentProvider'], part: Part): string =>
  provider === 'vercel' ? 'project' : provider === 'render' && part === 'database' ? 'database' : 'service'
