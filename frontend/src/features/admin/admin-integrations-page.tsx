import { EmptyState } from '../../components/shared/empty-state'
import { useDocumentTitle } from '../../lib/use-document-title'

/**
 * /admin/integrations (SPEC §7.2): GitHub App registration and status, presets, system-key policy. A placeholder until
 * that work lands; the admin layout already guards the route (FR-3).
 */
export function AdminIntegrationsPage() {
  useDocumentTitle('Integrations · Admin')
  return (
    <section aria-labelledby="admin-integrations-title" className="flex flex-col gap-4">
      <h2 id="admin-integrations-title" className="text-xl">
        Integrations
      </h2>
      <EmptyState
        variant="not_connected"
        headingLevel={3}
        title="No integrations set up yet"
        description="Registering the GitHub App, the presets list and the system-key policy are coming soon."
      />
    </section>
  )
}
