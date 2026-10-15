import { PageHeader } from '../../components/shared/page-header'
import { EmptyState } from '../../components/shared/empty-state'

/**
 * /connections (SPEC §7.2): the user's LLM providers, GitHub and Vercel. A placeholder until the connections work
 * (FR-73 onward) lands; the route exists so the sidebar and the user menu link somewhere real.
 */
export function ConnectionsPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader title="Connections" description="LLM providers, GitHub and Vercel, connected to your account." />
      <EmptyState
        variant="not_connected"
        title="Nothing connected yet"
        description="Connecting your own LLM keys, GitHub and Vercel is coming soon. Projects keep using their project settings until then."
      />
    </div>
  )
}
