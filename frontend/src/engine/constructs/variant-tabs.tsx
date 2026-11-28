import { Badge } from '../../components/ui/badge'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/radix-tabs'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useRunArtifacts } from '../api/hooks'
import type { PipelineDefinition, StageDefinition } from '../api/types'
import { ArtifactViewer } from '../components/artifact-viewer'
import type { DefinitionLabels } from '../labels'

/**
 * FR-349, FR-363 variants: one tab per variant label the definition declares (`v1`…), the chosen one marked, each
 * showing that variant's newest output version. Opens on the chosen variant once there is one.
 */
export function VariantTabs({
  runId,
  stage,
  chosen,
  definition,
  labels,
}: {
  runId: string
  stage: StageDefinition
  chosen: string | null | undefined
  definition: PipelineDefinition | undefined
  labels: DefinitionLabels
}) {
  const artifacts = useRunArtifacts(runId)
  const variantLabels = stage.variants?.labels ?? []
  const key = stage.output
  const title = labels.stage(stage.id)
  if (variantLabels.length === 0 || !key) {
    return null
  }
  if (artifacts.isPending) {
    return <LoadingState label={`Loading the variants of ${title}…`} />
  }
  if (artifacts.isError) {
    return <ErrorState error={artifacts.error} onRetry={() => void artifacts.refetch()} title={`The variants of ${title} couldn't load`} />
  }
  const versions = artifacts.data.find((a) => a.key === key)?.versions ?? []
  const artifactTitle = labels.artifact(key)
  const first = chosen && variantLabels.includes(chosen) ? chosen : variantLabels[0]
  return (
    <div className="flex flex-col gap-2" data-testid="variant-tabs">
      <p className="text-sm text-muted-foreground">
        {chosen ? `${chosen} was chosen.` : 'No variant has been chosen yet.'}
      </p>
      <Tabs defaultValue={first}>
        <TabsList aria-label={`Variants of ${title}`}>
          {variantLabels.map((label) => (
            <TabsTrigger key={label} value={label}>
              {label}
              {label === chosen && (
                <>
                  <span aria-hidden="true">✓</span>
                  <span className="sr-only">(chosen)</span>
                </>
              )}
            </TabsTrigger>
          ))}
        </TabsList>
        {variantLabels.map((label) => {
          // Versions are newest first: the first with this label is the variant's newest output.
          const version = versions.find((v) => v.variant === label)
          return (
            <TabsContent key={label} value={label}>
              {label === chosen && <Badge tone="success">Chosen</Badge>}
              {version ? (
                <ArtifactViewer
                  runId={runId}
                  artifactKey={key}
                  version={version.version}
                  definition={definition?.artifacts.find((a) => a.key === key)}
                  title={`${artifactTitle} ${label}`}
                />
              ) : (
                <p className="rounded-lg border border-dashed border-border p-3 text-sm text-muted-foreground">
                  {label} has no output yet.
                </p>
              )}
            </TabsContent>
          )
        })}
      </Tabs>
    </div>
  )
}
