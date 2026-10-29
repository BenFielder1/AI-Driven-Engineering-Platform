import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { useAdminLlmPolicy } from '../connections/connections-api'
import { llmProviderLabels } from '../projects/labels'
import { AdminBitbucketPanel } from './admin-bitbucket-panel'
import { GitHubAppPanel } from './admin-github-app-panel'
import { AdminGitLabPanel } from './admin-gitlab-panel'
import { AdminStackCatalogPanel } from './admin-stack-catalog-panel'

/**
 * Admin → Integrations → LLM system keys (FR-79): read-only. The policy comes from configuration
 * (`Llm__AllowSystemKeys`), so there is nothing to change here.
 */
function LlmSystemKeysPanel() {
  const policy = useAdminLlmPolicy()
  let body
  if (policy.isPending) {
    body = <LoadingState label="Loading the system-key policy…" />
  } else if (policy.isError) {
    body = <ErrorState error={policy.error} title="The system-key policy couldn't load" onRetry={() => void policy.refetch()} />
  } else {
    const { allowSystemKeys, systemKeyProviders } = policy.data
    body = (
      <div className="flex flex-col gap-3 text-sm">
        <p className="flex flex-wrap items-center gap-2">
          <span className="font-medium">System keys</span>
          <StatusBadge tone={allowSystemKeys ? 'warning' : 'neutral'}>
            <span data-testid="system-keys-allowed">{allowSystemKeys ? 'Allowed' : 'Not allowed'}</span>
          </StatusBadge>
        </p>
        <p className="text-muted-foreground">
          {allowSystemKeys
            ? 'Projects with no bound connection use the server’s key for their provider.'
            : 'Projects must bind an Owner’s connection; without one, their runs fail with PROVIDER_NOT_CONFIGURED.'}
        </p>
        <p>
          <span className="font-medium">Keys configured: </span>
          {systemKeyProviders.length === 0
            ? 'none'
            : systemKeyProviders.map((p) => llmProviderLabels[p]).join(', ')}
        </p>
        <p className="text-muted-foreground">
          Set by <code>Llm__AllowSystemKeys</code> in configuration (<code>secrets.env</code> on Kubernetes, user-secrets in
          the dev loop) and applied on restart. Keys are never shown.
        </p>
      </div>
    )
  }
  return (
    <section aria-labelledby="admin-llm-keys-title" className={cn(panelClass, 'flex flex-col gap-3 p-5')}>
      <h3 id="admin-llm-keys-title" className="text-lg">
        LLM system keys
      </h3>
      {body}
    </section>
  )
}

/**
 * /admin/integrations (SPEC §7.2): the GitHub App (FR-80), GitLab instances (FR-130), the Bitbucket consumer (FR-133), the system-key policy and the stack catalog (FR-113; the v2
 * presets are retired, FR-129). `callback` is
 * the manifest flow's outcome from `?github=configured` / `?error=GITHUB_AUTH_FAILED`.
 */
export function AdminIntegrationsPage({ callback }: { callback?: 'configured' | 'failed' }) {
  useDocumentTitle('Integrations · Admin')
  return (
    <section aria-labelledby="admin-integrations-title" className="flex flex-col gap-4">
      <h2 id="admin-integrations-title" className="text-xl">
        Integrations
      </h2>
      <GitHubAppPanel callback={callback} />
      <AdminGitLabPanel />
      <AdminBitbucketPanel />
      <LlmSystemKeysPanel />
      <AdminStackCatalogPanel />
    </section>
  )
}
