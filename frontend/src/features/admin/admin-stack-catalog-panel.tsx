import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { cn } from '../../lib/utils'
import { useAdminStackCatalog, type AdminStackPack } from '../stack/stack-api'
import { stackLayerLabels } from '../stack/stack-model'

const layerLabel = (layer: string | null) => (layer ? (stackLayerLabels[layer] ?? (layer === 'local' ? 'Local' : layer)) : 'Unknown')

function LoadStatus({ status }: { status: string }) {
  return (
    <StatusBadge tone={status === 'loaded' ? 'success' : 'destructive'}>{status === 'loaded' ? 'Loaded' : 'Failed'}</StatusBadge>
  )
}

function PackRow({ pack }: { pack: AdminStackPack }) {
  return (
    <tr className="border-t border-border/60 align-top">
      <td className="py-2 pr-3">{layerLabel(pack.layer)}</td>
      <th scope="row" className="py-2 pr-3 text-left font-normal">
        <span className="font-medium">{pack.name ?? pack.id}</span>
        <code className="block text-xs text-muted-foreground">{pack.id}</code>
      </th>
      <td className="py-2 pr-3">
        {pack.source ? (
          <>
            <code className="text-xs">
              {pack.source.plugin}@{pack.source.version}
            </code>
            <span className="block text-xs text-muted-foreground">snapshot {pack.source.snapshot}</span>
          </>
        ) : (
          <span className="text-muted-foreground">Unknown</span>
        )}
      </td>
      <td className="py-2 pr-3 font-mono text-xs tabular-nums">{pack.version ?? '—'}</td>
      <td className="py-2">
        <LoadStatus status={pack.status} />
        {pack.errors.length > 0 && (
          <ul className="mt-1 list-disc pl-5 text-xs text-destructive">
            {pack.errors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        )}
      </td>
    </tr>
  )
}

/**
 * Admin → Integrations → Stack catalog (§7.2, FR-113, FR-122): the catalog version, content hash and load status, and
 * each pack with its layer, stack-packs source (plugin@version), AIDE pack version and load status. Read-only: packs
 * ship with AIDE.
 */
export function AdminStackCatalogPanel() {
  const catalog = useAdminStackCatalog()
  let body
  if (catalog.isPending) {
    body = <LoadingState label="Loading the stack catalog…" />
  } else if (catalog.isError) {
    body = <ErrorState error={catalog.error} title="The stack catalog couldn't load" onRetry={() => void catalog.refetch()} />
  } else {
    const c = catalog.data
    body = (
      <div className="flex flex-col gap-3">
        <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
          <div className="flex gap-1">
            <dt className="text-muted-foreground">Catalog version:</dt>
            <dd className="font-medium" data-testid="stack-catalog-version">
              {c.version ?? 'Unknown'}
            </dd>
          </div>
          <div className="flex min-w-0 gap-1">
            <dt className="text-muted-foreground">Hash:</dt>
            <dd className="min-w-0 break-all">
              <code className="text-xs">{c.contentHash}</code>
            </dd>
          </div>
          <div className="flex items-center gap-1">
            <dt className="text-muted-foreground">Status:</dt>
            <dd>
              <LoadStatus status={c.status} />
            </dd>
          </div>
        </dl>
        {c.errors.length > 0 && (
          <ul aria-label="Catalog load errors" className="list-disc pl-5 text-sm text-destructive">
            {c.errors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        )}
        {/* Scrollable at narrow widths, so it is focusable for keyboard scrolling (WCAG 2.1.1). */}
        <div tabIndex={0} role="region" aria-label="Stack catalog packs" className="overflow-x-auto rounded focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
          <table className="w-full min-w-[40rem] text-sm">
            <caption className="sr-only">Stack packs</caption>
            <thead>
              <tr className="text-left text-muted-foreground">
                <th scope="col" className="py-1 pr-3 font-medium">Layer</th>
                <th scope="col" className="py-1 pr-3 font-medium">Pack</th>
                <th scope="col" className="py-1 pr-3 font-medium">Source</th>
                <th scope="col" className="py-1 pr-3 font-medium">AIDE version</th>
                <th scope="col" className="py-1 font-medium">Load status</th>
              </tr>
            </thead>
            <tbody>
              {c.packs.map((p) => (
                <PackRow key={p.id} pack={p} />
              ))}
            </tbody>
          </table>
        </div>
      </div>
    )
  }
  return (
    <section aria-labelledby="admin-stack-catalog-title" className={cn(panelClass, 'flex flex-col gap-3 p-5')}>
      <h3 id="admin-stack-catalog-title" className="text-lg">
        Stack catalog
      </h3>
      <p className="text-sm text-muted-foreground">
        The packs the Stack step offers, adapted from stack-packs. Release versions pin the catalog version and hash.
      </p>
      {body}
    </section>
  )
}
