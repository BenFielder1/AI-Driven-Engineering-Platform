import type { ReactNode } from 'react'

import { Badge, type BadgeTone } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import type { GatePayload, PipelineDefinition, RunGate } from '../api/types'
import { ArtifactViewer } from '../components/artifact-viewer'
import { humanise } from '../format'
import { definitionLabels, gatePayloadKindLabels } from '../labels'
import { JsonBlock } from './json-block'

/** FR-364: the payload kinds the engine raises (D-0.8.0-32). */
export const PAYLOAD_KINDS = { variantSelect: 'variant_select', toolCall: 'tool_call', externalHandler: 'external_handler' } as const

/** FR-358: tool tiers, destructive in the danger tone (colour is decorative; the tier is text). */
const tierTones: Record<string, BadgeTone> = { read: 'neutral', write: 'info', destructive: 'danger' }

/** A gate's payload when it carries one the engine knows (tool call, external handler, variant select). */
export function knownPayload(gate: RunGate): GatePayload | null {
  const payload = gate.payload
  return payload && Object.values(PAYLOAD_KINDS).some((k) => k === payload.kind) ? payload : null
}

/** A gate's payload kind in words ("Tool approval"), or `null` for gates without one. */
export function payloadKindLabel(gate: RunGate): string | null {
  const payload = knownPayload(gate)
  return payload ? (gatePayloadKindLabels[payload.kind] ?? humanise(payload.kind)) : null
}

function Facts({ children }: { children: ReactNode }) {
  return <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">{children}</dl>
}

function Fact({ term, children }: { term: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  )
}

/** FR-359, FR-360: a tool call waiting for approval: the tool, its tier, the agent's reason and the redacted arguments. */
function ToolCall({ payload }: { payload: GatePayload }) {
  const tier = payload.tier ?? null
  return (
    <div className="flex flex-col gap-2" data-testid="gate-payload-tool-call">
      <Facts>
        <Fact term="Tool">
          <code className="font-mono text-xs">{payload.tool ?? '—'}</code>
        </Fact>
        <Fact term="Tier">{tier ? <Badge tone={tierTones[tier] ?? 'neutral'}>{humanise(tier)}</Badge> : '—'}</Fact>
        <Fact term="Agent's reason">
          <span className="whitespace-pre-wrap">{payload.reason?.trim() ? payload.reason : 'The agent gave no reason.'}</span>
        </Fact>
      </Facts>
      {payload.arguments != null && <JsonBlock label="Arguments (redacted)" text={payload.arguments} />}
    </div>
  )
}

/** FR-360: an external handler's action: the handler, its stage and the exact payload sent once approved. */
function ExternalHandler({ payload, labels }: { payload: GatePayload; labels: ReturnType<typeof definitionLabels> }) {
  return (
    <div className="flex flex-col gap-2" data-testid="gate-payload-external">
      <Facts>
        <Fact term="Handler">
          <code className="font-mono text-xs">{payload.handler ?? '—'}</code>
        </Fact>
        {payload.stage && <Fact term="Stage">{labels.stage(payload.stage)}</Fact>}
      </Facts>
      <p className="text-sm text-muted-foreground">Approving sends exactly this payload, once.</p>
      {payload.payload != null ? (
        <JsonBlock label="Payload to send" text={payload.payload} />
      ) : (
        <p className="text-sm text-muted-foreground">No payload was recorded.</p>
      )}
    </div>
  )
}

/** FR-349, FR-364: the variants side by side, each with its content, a pick button for deciders and the choice once made. */
function VariantSelect({
  gate,
  payload,
  definition,
  labels,
  onPick,
}: {
  gate: RunGate
  payload: GatePayload
  definition: PipelineDefinition | undefined
  labels: ReturnType<typeof definitionLabels>
  onPick: ((choice: string) => void) | undefined
}) {
  const variants = payload.variants ?? []
  if (variants.length === 0) {
    return <p className="text-sm text-muted-foreground">The gate lists no variants.</p>
  }
  return (
    <div className="flex flex-col gap-2" data-testid="gate-payload-variants">
      {payload.stage && <p className="text-sm text-muted-foreground">Variants of {labels.stage(payload.stage)}</p>}
      <ul aria-label="Variants" className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {variants.map((v) => {
          const headingId = `gate-${gate.id}-variant-${v.label}`
          const chosen = payload.choice === v.label
          return (
            <li key={v.label} aria-labelledby={headingId} className="flex min-w-0 flex-col gap-2 rounded-lg border border-border/60 p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <p id={headingId} className="text-sm font-semibold">
                  {v.label}
                </p>
                {chosen && <Badge tone="success">Chosen</Badge>}
              </div>
              {v.artifactKey && v.artifactVersion != null ? (
                <ArtifactViewer
                  runId={gate.runId}
                  artifactKey={v.artifactKey}
                  version={v.artifactVersion}
                  definition={definition?.artifacts.find((a) => a.key === v.artifactKey)}
                  title={`${labels.artifact(v.artifactKey)} ${v.label}`}
                />
              ) : (
                <p className="text-sm text-muted-foreground">Its content isn't available.</p>
              )}
              {onPick && (
                <Button
                  className="self-start"
                  aria-describedby={headingId}
                  onClick={() => {
                    onPick(v.label)
                  }}
                >
                  Pick {v.label}
                </Button>
              )}
            </li>
          )
        })}
      </ul>
    </div>
  )
}

/**
 * FR-364: what an engine-raised gate asks the decider to approve, by its payload kind: a tool call (tool, tier, the
 * agent's reason and the redacted arguments), an external action (the exact payload) or a variant select (the variants
 * side by side, with a pick for those who may decide). Viewers and up see the payload; only deciders get `onPick`.
 * `variants: false` leaves out the variant contents (the decision dialog, which already knows the pick).
 */
export function GatePayloadView({
  gate,
  definition,
  onPick,
  variants = true,
}: {
  gate: RunGate
  definition: PipelineDefinition | undefined
  onPick?: (choice: string) => void
  variants?: boolean
}) {
  const payload = knownPayload(gate)
  if (!payload) {
    return null
  }
  const labels = definitionLabels(definition, [gate])
  switch (payload.kind) {
    case PAYLOAD_KINDS.toolCall:
      return <ToolCall payload={payload} />
    case PAYLOAD_KINDS.externalHandler:
      return <ExternalHandler payload={payload} labels={labels} />
    default:
      return variants ? <VariantSelect gate={gate} payload={payload} definition={definition} labels={labels} onPick={onPick} /> : null
  }
}
