import { adminNavItems, type AppPath } from '../shell/nav-config'

/** What each admin page is for (shown on the admin overview), keyed by its nav-config item key. */
const descriptions: Record<string, string> = {
  'admin-overview': 'What each admin section is for.',
  'admin-users': 'Everyone with an account, and their system role. Admins can make other users Admins.',
  'admin-events': 'Every project’s audit events in one log. Filter by project, type, category, outcome and date.',
  'admin-runs': 'Every agent run across projects, with model, tokens, cost, duration and status.',
  'admin-dead-letters': 'Background jobs that failed every attempt, with their last error. Retry re-queues the job.',
  'admin-integrations': 'GitHub App registration and status, the stack catalog and the system-key policy.',
}

export interface AdminSection {
  key: string
  to: AppPath
  label: string
  description: string
  /** Active only on this exact path (the overview). */
  exact: boolean
}

/** The admin sections (SPEC §7.2), derived from nav-config so the sidebar and the admin nav never disagree. */
export const adminSections: readonly AdminSection[] = adminNavItems.map((item) => ({
  key: item.key,
  to: item.to,
  label: item.label,
  description: descriptions[item.key] ?? '',
  exact: item.exact === true,
}))
