import { EmptyState, ForbiddenState, PageHeader } from '../../components/ui/states'
import { canViewAdmin } from '../../lib/permissions'
import { useCurrentUser } from '../auth/auth-api'

/** /projects. Placeholder until the projects feature lands (phase 3). */
export function ProjectsPage() {
  return (
    <>
      <PageHeader title="Projects" />
      <EmptyState title="Projects are coming soon">The projects list arrives in the next phase.</EmptyState>
    </>
  )
}

/** /notifications. Placeholder until notifications land. */
export function NotificationsPage() {
  return (
    <>
      <PageHeader title="Notifications" />
      <EmptyState title="No notifications yet">Notifications arrive in a later phase.</EmptyState>
    </>
  )
}

/** /admin. Forbidden for non-admins (SPEC §7); the admin pages arrive in a later phase. */
export function AdminPage() {
  const user = useCurrentUser()
  if (!canViewAdmin(user)) {
    return <ForbiddenState>The admin area is only available to administrators.</ForbiddenState>
  }
  return (
    <>
      <PageHeader title="Admin" />
      <EmptyState title="Admin tools are coming soon">Events, agent runs, dead letters and users arrive in a later phase.</EmptyState>
    </>
  )
}
