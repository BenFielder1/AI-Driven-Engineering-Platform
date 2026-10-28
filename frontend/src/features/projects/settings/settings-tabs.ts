/** Settings sections, in nav order: the `/projects/:id/settings/{section}` sub-routes (SPEC §7.2). */
export const settingsTabs = ['general', 'members', 'git', 'stack', 'llm', 'deployment', 'notifications', 'danger'] as const
export type SettingsTab = (typeof settingsTabs)[number]

export const settingsTabLabels: Record<SettingsTab, string> = {
  general: 'General',
  members: 'Members',
  git: 'Git',
  stack: 'Stack',
  llm: 'LLM',
  deployment: 'Deployment',
  notifications: 'Notifications',
  danger: 'Danger zone',
}

/** The section named by a URL segment or a legacy `?tab=` value, if it is one. */
export const parseSettingsTab = (value: unknown): SettingsTab | undefined => settingsTabs.find((t) => t === value)
