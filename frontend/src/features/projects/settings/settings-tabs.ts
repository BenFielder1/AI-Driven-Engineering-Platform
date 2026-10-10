/** Settings tabs, in order (the `tab` search param of /projects/:id/settings). */
export const settingsTabs = ['general', 'members', 'git', 'llm', 'notifications', 'danger'] as const
export type SettingsTab = (typeof settingsTabs)[number]
