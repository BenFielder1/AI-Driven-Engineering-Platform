import { Alert } from '../../components/ui/alert'
import { errorMessages } from '../../lib/errors'
import { ExternalItemLink } from './external-item-link'
import { trackerNames } from './tracker-picks'
import { useProjectSources } from './trackers-api'

/**
 * FR-172: `UNCOVERED_SOURCE_ITEMS` on the Specification page: the imported, not-done items no requirement cites, linked
 * to the tracker. Advisory, never blocking. A key shows as plain text if its URL isn't known (yet).
 */
export function UncoveredSourceItems({
  projectId,
  releaseVersionId,
  keys,
}: {
  projectId: string
  releaseVersionId?: string
  keys: readonly string[]
}) {
  const sources = useProjectSources(projectId, releaseVersionId)
  const known = new Map(
    (sources.data?.imports ?? [])
      .filter((i) => i.status === 'completed')
      .flatMap((i) => i.items.map((item) => [item.key, { url: item.url, tracker: trackerNames[i.provider] }] as const)),
  )
  return (
    <Alert tone="warning" title="Imported items no requirement cites">
      <p>{errorMessages.UNCOVERED_SOURCE_ITEMS}</p>
      {keys.length > 0 && (
        <ul aria-label="Imported items not cited by any requirement" className="mt-1 flex flex-wrap gap-2">
          {keys.map((key) => {
            const item = known.get(key)
            return (
              <li key={key}>
                {item ? <ExternalItemLink url={item.url} itemKey={key} tracker={item.tracker} /> : <code className="text-xs">{key}</code>}
              </li>
            )
          })}
        </ul>
      )}
    </Alert>
  )
}
