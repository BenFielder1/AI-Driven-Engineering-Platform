import { createFileRoute, Outlet } from '@tanstack/react-router'

/** /projects/:id/settings/* (SPEC §7.2): the sections are sub-routes; `/settings` redirects to General. */
export const Route = createFileRoute('/_app/projects/$projectId/settings')({
  component: Outlet,
})
