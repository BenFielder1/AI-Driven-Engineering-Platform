import type { ReactNode } from 'react'

/** /projects/:id/settings/* (SPEC §7.2): the sections are sub-routes; `/settings` redirects to General. */
export default function SettingsLayout({ children }: { children: ReactNode }) {
  return children
}
